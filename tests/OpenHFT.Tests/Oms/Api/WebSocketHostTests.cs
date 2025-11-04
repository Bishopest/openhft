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

namespace OpenHFT.Tests.Oms.Api;

[TestFixture]
public class WebSocketHostTests
{
    private ServiceProvider _serviceProvider;
    private IHostedService _webSocketHost;
    private ClientWebSocket _client;
    private Mock<IQuotingInstanceManager> _mockManager;

    [OneTimeSetUp]
    public async Task OnetTimeSetup()
    {
        _mockManager = new Mock<IQuotingInstanceManager>();

        var services = new ServiceCollection();

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
        var parameters = new QuotingParameters(123, FairValueModel.Midp, 124, 1m, 1m, Quantity.FromDecimal(2m), 1, QuoterType.Single);
        var updateCommand = new UpdateParametersCommand(parameters);
        var commandJson = JsonSerializer.Serialize(updateCommand, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var commandBytes = Encoding.UTF8.GetBytes(commandJson);

        // --- Act ---
        await _client.SendAsync(new ArraySegment<byte>(commandBytes), WebSocketMessageType.Text, true, CancellationToken.None);

        await Task.Delay(200);

        // --- Assert ---
        _mockManager.Verify(
            manager => manager.DeployStrategy(It.Is<QuotingParameters>(p =>
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
        // Arrange
        _mockManager.Setup(m => m.DeployStrategy(It.IsAny<QuotingParameters>())).Returns(true);
        var command = new UpdateParametersCommand(new QuotingParameters(123, FairValueModel.Midp, 124, 1m, 1m, Quantity.FromDecimal(2m), 1, QuoterType.Single));
        var commandJson = JsonSerializer.Serialize(command);
        var commandBytes = Encoding.UTF8.GetBytes(commandJson);

        // Act
        await _client.SendAsync(commandBytes, WebSocketMessageType.Text, true, CancellationToken.None);

        await Task.Delay(200);

        // Assert: Acknowledgment 메시지 수신 확인
        var buffer = new byte[1024];
        var receiveResult = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var responseJson = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);

        var ackEvent = JsonSerializer.Deserialize<AcknowledgmentEvent>(responseJson);
        ackEvent.Should().NotBeNull();
        ackEvent!.Success.Should().BeTrue();
        ackEvent.Message.Should().Contain("successfully");
    }
}