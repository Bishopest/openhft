using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using OpenHFT.Oms.Api.WebSocket;
using System.Text;
using System.Text.Json;
using OpenHFT.Quoting.Interfaces;
using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Net.WebSockets;
using FluentAssertions;
using OpenHFT.Quoting;
using OpenHFT.Core.Models;
using Microsoft.Extensions.Hosting;
using OpenHFT.Oms.Api.WebSocket.CommandHandlers;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using OpenHFT.Core.Instruments;
using Microsoft.Extensions.Configuration;

namespace OpenHFT.Tests.Oms.Api;

[TestFixture]
public class WebSocketHostTests
{
    private ServiceProvider _serviceProvider;
    private IHostedService _webSocketHost;
    private ClientWebSocket _client;
    private Mock<IQuotingInstanceManager> _mockManager;
    private JsonSerializerOptions _jsonOptions;


    [OneTimeSetUp]
    public async Task OnetTimeSetup()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            // For SENDING data to the server in camelCase
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

            // For RECEIVING data from the server, be flexible with casing.
            // This will correctly map "instrumentId" (from server) to "InstrumentId" (in C#).
            PropertyNameCaseInsensitive = true,

            // For RECEIVING enum values as strings (e.g., "Spot" instead of 0)
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        _mockManager = new Mock<IQuotingInstanceManager>();

        var services = new ServiceCollection();
        services.AddSingleton(_jsonOptions);
        var inMemorySettings = new Dictionary<string, string>
        {
            { "omsIdentifier", "test-oms" }
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        services.AddSingleton(configuration);
        // --- 1. 필요한 모든 서비스 등록 ---
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

        // Command Handlers and Router
        services.AddSingleton<IWebSocketCommandHandler, UpdateParametersCommandHandler>();
        services.AddSingleton<IWebSocketCommandHandler, RetireInstanceCommandHandler>();
        services.AddSingleton<IWebSocketCommandRouter, WebSocketCommandRouter>();

        // WebSocket Channel (단일 연결 모델)
        services.AddSingleton<WebSocketChannel>();
        services.AddSingleton<IWebSocketChannel>(p => p.GetRequiredService<WebSocketChannel>());

        // Mock Strategy Manager
        services.AddSingleton(_mockManager.Object);

        // WebSocketHost 등록 (IHostedService로)
        services.AddSingleton<IHostedService, WebSocketHost>();

        _serviceProvider = services.BuildServiceProvider();

        // --- 2. 호스트 서비스 가져오기 ---
        // GetServices를 사용하여 모든 IHostedService를 가져온 후 WebSocketHost를 찾음
        _webSocketHost = _serviceProvider.GetServices<IHostedService>()
                                         .OfType<WebSocketHost>()
                                         .First();
        await _webSocketHost.StartAsync(CancellationToken.None);
    }
    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_webSocketHost != null)
        {
            await _webSocketHost.StopAsync(CancellationToken.None);
        }
        _serviceProvider.Dispose();
    }

    [SetUp]
    public async Task Setup()
    {
        _client = new ClientWebSocket();
        // WebSocketHost가 5001 포트에서 리스닝한다고 가정
        var serverUri = new Uri("ws://localhost:5001/");
        await _client.ConnectAsync(serverUri, CancellationToken.None);

        // Mock을 초기화
        _mockManager.Reset();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_client.State == WebSocketState.Open)
        {
            await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test finished", CancellationToken.None);
        }
        _client.Dispose();
    }

    [Test]
    public async Task When_UpdateParametersCommandSent_Should_CallStrategyManager()
    {
        // --- Arrange ---
        var parameters = new QuotingParameters(123, "test", FairValueModel.Midp, 124, 1m, -1m, 1m, Quantity.FromDecimal(2m), 1, QuoterType.Single, true, Quantity.FromDecimal(6m), Quantity.FromDecimal(6m));
        var updateCommand = new UpdateParametersCommand(parameters);
        var commandJson = JsonSerializer.Serialize(updateCommand, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var commandBytes = Encoding.UTF8.GetBytes(commandJson);

        // --- Act ---
        await _client.SendAsync(new ArraySegment<byte>(commandBytes), WebSocketMessageType.Text, true, CancellationToken.None);

        await Task.Delay(200);

        // --- Assert ---
        _mockManager.Verify(
            manager => manager.UpdateInstanceParameters(It.Is<QuotingParameters>(p =>
                p.InstrumentId == 123 &&
                p.FvModel == FairValueModel.Midp
            )),
            Times.Once,
            "UpdateParameters should have been called with the correct parameters."
        );
    }

    [Test]
    public async Task When_UnknownCommandSent_Should_ReceiveErrorResponse()
    {
        // --- Arrange ---
        var unknownCommand = new { type = "UNKNOWN_COMMAND", payload = "test" };
        var commandJson = JsonSerializer.Serialize(unknownCommand);
        var commandBytes = Encoding.UTF8.GetBytes(commandJson);

        // --- Act ---
        await _client.SendAsync(new ArraySegment<byte>(commandBytes), WebSocketMessageType.Text, true, CancellationToken.None);

        // --- Assert ---
        var buffer = new byte[1024];
        var receiveResult = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var responseJson = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);

        using var jsonDoc = JsonDocument.Parse(responseJson);
        var root = jsonDoc.RootElement;

        root.GetProperty("type").GetString().Should().Be("ERROR");
    }

    [Test]
    public async Task When_UpdateParametersCommandHandled_Should_ReceiveAcknowledgment()
    {
        var mockInstrument = new CryptoPerpetual(
                instrumentId: 1001,
                symbol: "BTCUSDT",
                exchange: ExchangeEnum.BINANCE,
                baseCurrency: Currency.BTC,
                quoteCurrency: Currency.USDT,
                tickSize: Price.FromDecimal(0.1m),
                lotSize: Quantity.FromDecimal(0.001m),
                multiplier: 1m,
                minOrderSize: Quantity.FromDecimal(0.001m)
        );
        // --- Arrange ---
        var parameters = new QuotingParameters(mockInstrument.InstrumentId, "test", FairValueModel.Midp, 124, 1m, -1m, 1m, Quantity.FromDecimal(2m), 1, QuoterType.Single, true, Quantity.FromDecimal(6m), Quantity.FromDecimal(6m));

        // Mock QuotingInstanceManager가 파라미터를 받았을 때,
        // 검증에 필요한 QuotingInstance를 반환하도록 설정합니다.
        var mockEngine = new Mock<IQuotingEngine>();
        mockEngine.Setup(e => e.CurrentParameters).Returns(parameters);
        mockEngine.Setup(e => e.IsActive).Returns(true);
        mockEngine.Setup(e => e.QuotingInstrument).Returns(mockInstrument);
        var mockInstance = new QuotingInstance(mockEngine.Object);

        // UpdateInstanceParameters가 호출되면 위에서 만든 mockInstance를 반환하도록 설정
        _mockManager.Setup(m => m.UpdateInstanceParameters(It.IsAny<QuotingParameters>()))
                    .Returns(mockInstance);

        var command = new UpdateParametersCommand(parameters);
        var commandJson = JsonSerializer.Serialize(command, _jsonOptions);
        var commandBytes = Encoding.UTF8.GetBytes(commandJson);

        // --- Act ---
        await _client.SendAsync(new ArraySegment<byte>(commandBytes), WebSocketMessageType.Text, true, CancellationToken.None);

        // 서버가 메시지를 처리하고 응답을 보낼 시간을 줍니다.
        await Task.Delay(200);

        // --- Assert: 이제 InstanceStatusEvent 메시지를 수신하고 검증합니다. ---
        var buffer = new byte[1024];
        var receiveResult = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var responseJson = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);

        TestContext.WriteLine($"Received Response: {responseJson}");

        // 1. 메시지 타입을 먼저 확인하여 올바른 종류의 이벤트인지 확인합니다.
        using var jsonDoc = JsonDocument.Parse(responseJson);
        var messageType = jsonDoc.RootElement.GetProperty("type").GetString();
        messageType.Should().Be("INSTANCE_STATUS");

        // 2. 전체 메시지를 InstanceStatusEvent로 역직렬화합니다.
        var statusEvent = JsonSerializer.Deserialize<InstanceStatusEvent>(responseJson, _jsonOptions);

        // 3. 수신된 이벤트의 내용을 상세하게 검증합니다.
        statusEvent.Should().NotBeNull();
        var payload = statusEvent!.Payload;
        payload.Should().NotBeNull();

        payload.InstrumentId.Should().Be(parameters.InstrumentId);
        payload.IsActive.Should().BeTrue();

        // 페이로드에 포함된 Parameters가 우리가 보낸 것과 동일한지 확인합니다.
        // QuotingParameters에 IEquatable이 구현되어 있으므로 직접 비교할 수 있습니다.
        payload.Parameters.Should().Be(parameters);
    }
}