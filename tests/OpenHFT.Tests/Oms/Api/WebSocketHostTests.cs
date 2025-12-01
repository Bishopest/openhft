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
using OpenHFT.Core.Interfaces;
using OpenHFT.Hedging;

namespace OpenHFT.Tests.Oms.Api;

[TestFixture]
public class WebSocketHostTests
{
    private ServiceProvider _serviceProvider;
    private IHostedService _webSocketHost;
    private ClientWebSocket _client;
    private Mock<IQuotingInstanceManager> _mockManager;
    private JsonSerializerOptions _jsonOptions;

    [SetUp]
    public async Task Setup()
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
        services.AddSingleton(new Mock<IOrderRouter>().Object);
        services.AddSingleton(new Mock<IBookManager>().Object);
        services.AddSingleton(new Mock<IHedgerManager>().Object);
        services.AddHostedService<WebSocketNotificationService>();

        // WebSocketHost 등록 (IHostedService로)
        services.AddSingleton<IHostedService, WebSocketHost>();

        _serviceProvider = services.BuildServiceProvider();

        var hostedServices = _serviceProvider.GetServices<IHostedService>();
        foreach (var service in hostedServices)
        {
            await service.StartAsync(CancellationToken.None);
        }
        _webSocketHost = hostedServices.OfType<WebSocketHost>().First();

        _client = new ClientWebSocket();
        // WebSocketHost가 5001 포트에서 리스닝한다고 가정
        var serverUri = new Uri("ws://localhost:5001/");
        await _client.ConnectAsync(serverUri, CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        // 클라이언트 종료
        if (_client != null)
        {
            if (_client.State == WebSocketState.Open)
                await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test finished", CancellationToken.None);
            _client.Dispose();
        }

        // 호스트 서비스 종료
        if (_serviceProvider != null)
        {
            var hostedServices = _serviceProvider.GetServices<IHostedService>();
            foreach (var service in hostedServices.Reverse()) // 역순 종료 권장
            {
                await service.StopAsync(CancellationToken.None);
            }
            _serviceProvider.Dispose();
        }

        if (_webSocketHost != null)
        {
            await _webSocketHost.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task When_UpdateParametersCommandSent_Should_CallStrategyManager()
    {
        // --- Arrange ---
        var parameters = new QuotingParameters(123, "test", FairValueModel.Midp, 124, 1m, -1m, 1m, Quantity.FromDecimal(2m), 1, QuoterType.Single, true, Quantity.FromDecimal(6m), Quantity.FromDecimal(6m), HittingLogic.AllowAll);
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
}