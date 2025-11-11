
using System.Text.Json.Serialization;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Configuration;

// <summary>
/// Defines the execution modes for API and Feed connections.
/// </summary>
public class ExecutionConfig
{
    // JSON Deserializer가 "Testnet" 문자열을 ExecutionMode.Testnet enum으로 자동 변환
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExecutionMode Api { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExecutionMode Feed { get; set; }
}

/// <summary>
/// Represents the root of the subscription configuration.
/// </summary>
public class SubscriptionConfig
{
    public List<SubscriptionGroup> Subscriptions { get; set; } = new();
}

/// <summary>
/// Represents a single group of symbols to be subscribed from a specific
/// exchange and product type.
/// </summary>
public class SubscriptionGroup
{
    public string Exchange { get; set; } = string.Empty;

    public ExecutionConfig Execution { get; set; } = new();

    public string ProductType { get; set; } = string.Empty;

    public string[] Symbols { get; set; } = Array.Empty<string>();
}