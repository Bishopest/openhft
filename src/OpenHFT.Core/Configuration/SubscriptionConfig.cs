
using Newtonsoft.Json;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Configuration;

// <summary>
/// Defines the execution modes for API and Feed connections.
/// </summary>
public class ExecutionConfig
{
    // JSON Deserializer가 "Testnet" 문자열을 ExecutionMode.Testnet enum으로 자동 변환
    [JsonProperty("api")]
    public ExecutionMode Api { get; set; }

    [JsonProperty("feed")]
    public ExecutionMode Feed { get; set; }
}

/// <summary>
/// Represents the root of the subscription configuration.
/// </summary>
public class SubscriptionConfig
{
    [JsonProperty("subscriptions")]
    public List<SubscriptionGroup> Subscriptions { get; set; } = new();
}

/// <summary>
/// Represents a single group of symbols to be subscribed from a specific
/// exchange and product type.
/// </summary>
public class SubscriptionGroup
{
    [JsonProperty("exchange")]
    public string Exchange { get; set; } = string.Empty;

    [JsonProperty("execution")]
    public ExecutionConfig Execution { get; set; } = new();

    [JsonProperty("productType")]
    public string ProductType { get; set; } = string.Empty;

    [JsonProperty("symbols")]
    public string[] Symbols { get; set; } = Array.Empty<string>();
}