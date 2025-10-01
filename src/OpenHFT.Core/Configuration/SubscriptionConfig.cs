
using System.Text.Json.Serialization;

namespace OpenHFT.Core.Configuration;

/// <summary>
/// Represents the root of the subscription configuration.
/// </summary>
public class SubscriptionConfig
{
    [JsonPropertyName("subscriptions")]
    public List<SubscriptionGroup> Subscriptions { get; set; } = new();
}

/// <summary>
/// Represents a single group of symbols to be subscribed from a specific
/// exchange and product type.
/// </summary>
public class SubscriptionGroup
{
    [JsonPropertyName("exchange")]
    public string Exchange { get; set; } = string.Empty;

    // This attribute maps the JSON key "product-type" to the C# property "ProductType".
    [JsonPropertyName("product-type")]
    public string ProductType { get; set; } = string.Empty;

    [JsonPropertyName("symbols")]
    public string[] Symbols { get; set; } = Array.Empty<string>();
}