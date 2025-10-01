
using Newtonsoft.Json;

namespace OpenHFT.Core.Configuration;

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

    // This attribute maps the JSON key "product-type" to the C# property "ProductType".
    [JsonProperty("product-type")]
    public string ProductType { get; set; } = string.Empty;

    [JsonProperty("symbols")]
    public string[] Symbols { get; set; } = Array.Empty<string>();
}