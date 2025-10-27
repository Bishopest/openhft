using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Gateway.ApiClient;

public record BinanceListenKeyResponse
{
    [JsonPropertyName("listenKey")]
    public string ListenKey { get; init; } = "";
}

/// <summary>
/// Represents the data structure for a Binance depth snapshot response.
/// </summary>
public record BinanceDepthSnapshot
{
    [JsonPropertyName("lastUpdateId")]
    public long LastUpdateId { get; init; }

    [JsonPropertyName("E")]
    public long MessageOutputTime { get; init; }

    [JsonPropertyName("T")]
    public long TransactionTime { get; init; }

    [JsonPropertyName("bids")]
    [JsonConverter(typeof(PriceLevelConverter))]
    public IReadOnlyList<(decimal Price, decimal Quantity)> Bids { get; init; } = Array.Empty<(decimal, decimal)>();

    [JsonPropertyName("asks")]
    [JsonConverter(typeof(PriceLevelConverter))]
    public IReadOnlyList<(decimal Price, decimal Quantity)> Asks { get; init; } = Array.Empty<(decimal, decimal)>();
}

/// <summary>
/// Represents the data structure for a Binance server time response.
/// </summary>
public record BinanceServerTime
{
    [JsonPropertyName("serverTime")]
    public long ServerTime { get; init; }
}

/// <summary>
/// Custom JSON converter to deserialize Binance's price level arrays [string, string] into a list of (decimal, decimal) tuples.
/// </summary>
public class PriceLevelConverter : JsonConverter<IReadOnlyList<(decimal Price, decimal Quantity)>>
{
    public override IReadOnlyList<(decimal, decimal)> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected start of array for price levels.");

        var levels = new List<(decimal Price, decimal Quantity)>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray) continue;

            reader.Read();
            var price = decimal.Parse(reader.GetString()!);
            reader.Read();
            var quantity = decimal.Parse(reader.GetString()!);
            reader.Read(); // End of inner array
            levels.Add((price, quantity));
        }
        return levels;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<(decimal Price, decimal Quantity)> value, JsonSerializerOptions options)
    {
        throw new NotImplementedException(); // Serialization is not needed for this use case.
    }
}

/// <summary>
/// REST API client for Binance, supporting both Spot and Futures markets.
/// </summary>
public class BinanceRestApiClient : BaseRestApiClient
{
    protected override string BaseUrl => ProdType switch
    {
        ProductType.PerpetualFuture => "https://fapi.binance.com",
        ProductType.Spot => "https://api.binance.com",
        _ => throw new InvalidOperationException($"Unsupported product type for Binance: {ProdType}")
    };

    public override ExchangeEnum SourceExchange => ExchangeEnum.BINANCE;

    public BinanceRestApiClient(ILogger<BinanceRestApiClient> logger, IInstrumentRepository instrumentRepository, HttpClient httpClient, ProductType productType)
        : base(logger, instrumentRepository, httpClient, productType) { }

    public Task<BinanceDepthSnapshot> GetDepthSnapshotAsync(string symbol, int limit = 20, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/fapi/v1/depth?symbol={symbol.ToUpper()}&limit={limit}";
        return SendRequestAsync<BinanceDepthSnapshot>(HttpMethod.Get, endpoint, cancellationToken: cancellationToken);
    }

    public override async Task<long> GetServerTimeAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = "/fapi/v1/time";
        var response = await SendRequestAsync<BinanceServerTime>(HttpMethod.Get, endpoint, cancellationToken: cancellationToken);
        return response.ServerTime;
    }

    public Task<BinanceListenKeyResponse> CreateListenKeyAsync(ProductType type, CancellationToken ct = default)
    {
        // POST /fapi/v1/listenKey
        // NOTE: This is a private, signed endpoint.
        // The BaseRestApiClient needs to be extended with a SendPrivateRequestAsync method.
        // return SendPrivateRequestAsync<BinanceListenKeyResponse>(HttpMethod.Post, "/fapi/v1/listenKey", null, ct);
        return Task.FromResult(new BinanceListenKeyResponse()); // Placeholder 
    }
}