using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Gateway.ApiClient;

/// <summary>
/// Represents a single level in the BitMEX order book.
/// </summary>
public record BitmexOrderBookL2
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("side")]
    public string Side { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public decimal Size { get; init; }

    [JsonPropertyName("price")]
    public decimal Price { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// REST API client for Binance, supporting both Spot and Futures markets.
/// </summary>
public class BitmexRestApiClient : BaseRestApiClient
{
    protected override string GetBaseUrl(ExecutionMode mode)
    {
        return mode switch
        {
            ExecutionMode.Live => "https://www.bitmex.com",
            ExecutionMode.Testnet => "https://testnet.bitmex.com",
            _ => throw new InvalidOperationException($"Unsupported Execution Mode for Bitmex: {mode}")
        };
    }

    public override ExchangeEnum SourceExchange => ExchangeEnum.BITMEX;

    public BitmexRestApiClient(ILogger<BitmexRestApiClient> logger,
        IInstrumentRepository instrumentRepository,
        HttpClient httpClient, ProductType productType, ExecutionMode mode,
        string? apiSecret = null, string? apiKey = null)
        : base(logger, instrumentRepository, httpClient, productType, mode, apiSecret, apiKey) { }

    public override async Task<long> GetServerTimeAsync(CancellationToken cancellationToken = default)
    {
        // BitMEX does not have a dedicated /time endpoint.
        // We make a lightweight request and use the 'timestamp' from the response body.
        var endpoint = "/api/v1/orderbook/L2?symbol=XBTUSD&depth=1";
        var result = await SendRequestAsync<IReadOnlyList<BitmexOrderBookL2>>(HttpMethod.Get, endpoint, cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            _logger.LogWarningWithCaller($"Failed to get server time: {result.Error.Message}");
            throw result.Error;
        }

        var firstLevel = result.Data.FirstOrDefault() ?? throw new InvalidOperationException("BitMEX server time response was empty.");
        return firstLevel.Timestamp.ToUnixTimeMilliseconds();
    }

    protected override void AddSignatureToRequest(HttpRequestMessage request, string fullPath, string? queryString, Dictionary<string, object>? bodyParams)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds();
        string data = bodyParams != null
            ? JsonSerializer.Serialize(bodyParams)
            : string.Empty;

        // BitMEX 서명: verb + path(with query) + expires + data
        string signatureString = request.Method.Method.ToUpper() + fullPath + expires + data;
        string signature = CreateHmacSha256Signature(signatureString); // Base class helper

        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("api-key", ApiKey);
        request.Headers.Add("api-expires", expires.ToString());
        request.Headers.Add("api-signature", signature);

        if (bodyParams != null)
        {
            request.Content = new StringContent(data, null, "application/json");
        }
    }
}