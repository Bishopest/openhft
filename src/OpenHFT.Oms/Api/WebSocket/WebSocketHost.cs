using System;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Oms.Api.WebSocket;

public class WebSocketHost : IHostedService
{
    private readonly ILogger<WebSocketHost> _logger;
    private readonly IWebSocketCommandRouter _commandRouter;
    private readonly WebSocketChannel _webSocketChannel;
    private readonly IHost _webHost;

    public WebSocketHost(ILogger<WebSocketHost> logger, IWebSocketCommandRouter manager, WebSocketChannel webSocketChannel)
    {
        _logger = logger;
        _commandRouter = manager;
        _webSocketChannel = webSocketChannel;

        _webHost = new HostBuilder()
            .ConfigureWebHostDefaults(wb =>
            {
                wb.UseKestrel(options =>
                {
                    options.ListenAnyIP(5001);
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(HandleWebSocketRequestAsync);
                });
            })
            .Build();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Websocket Host starting on port 5001...");
        return _webHost.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Websocket Host stopping on port 5001...");
        return _webHost.StopAsync(cancellationToken);
    }

    private async Task HandleWebSocketRequestAsync(HttpContext context)
    {

        if (_webSocketChannel.IsConnected)
        {
            _logger.LogWarningWithCaller("Rejecting new WebSocket connection attempt. A client is already connected.");
            context.Response.StatusCode = 409;  // Forbidden
            await context.Response.WriteAsync("A client is already connected.");
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            _webSocketChannel.SetSocket(webSocket);
            _logger.LogInformationWithCaller("WebSocket connection accepted.");

            var remoteIp = context.Connection.RemoteIpAddress?.ToString();
            var remotePort = context.Connection.RemotePort;
            var localIp = context.Connection.LocalIpAddress?.ToString();
            var localPort = context.Connection.LocalPort;
            var connectionId = context.Connection.Id;
            var protocol = context.Request.Protocol;

            _logger.LogInformationWithCaller(
                $"[{connectionId}] Connection established from {remoteIp}:{remotePort} to {localIp}:{localPort} using {protocol}");

            await HandleConnectionAsync(webSocket);
        }
        else
        {
            context.Response.StatusCode = 400;  // Bad Request
        }
    }

    private async Task HandleConnectionAsync(System.Net.WebSockets.WebSocket webSocket)
    {
        var buffer = new byte[1024 * 4];

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }

                // 수신된 메시지를 처리합니다.
                var messageJson = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                await _commandRouter.RouteAsync(messageJson);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "An error occurred in WebSocket connection handling.");
        }
        finally
        {
            _webSocketChannel.ClearSocket();
            _logger.LogInformationWithCaller("WebSocket client disconnected.");
        }
    }
}
