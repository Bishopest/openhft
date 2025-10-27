using System;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

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
    protected override string BaseUrl => "https://www.bitmex.com";

    public override ExchangeEnum SourceExchange => ExchangeEnum.BITMEX;

    public BitmexRestApiClient(ILogger<BitmexRestApiClient> logger, IInstrumentRepository instrumentRepository, HttpClient httpClient, ProductType productType)
        : base(logger, instrumentRepository, httpClient, productType) { }

    public override async Task<long> GetServerTimeAsync(CancellationToken cancellationToken = default)
    {
        // BitMEX does not have a dedicated /time endpoint.
        // We make a lightweight request and use the 'timestamp' from the response body.
        var endpoint = "/api/v1/orderbook/L2?symbol=XBTUSD&depth=1";
        var response = await SendRequestAsync<IReadOnlyList<BitmexOrderBookL2>>(HttpMethod.Get, endpoint, cancellationToken: cancellationToken);

        var firstLevel = response.FirstOrDefault() ?? throw new InvalidOperationException("BitMEX server time response was empty.");
        return firstLevel.Timestamp.ToUnixTimeMilliseconds();
    }
}