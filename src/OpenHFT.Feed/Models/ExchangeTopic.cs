using System.Collections.Concurrent;
using System.Reflection;

namespace OpenHFT.Feed.Models;

/// <summary>
/// Represents a specific WebSocket topic for an exchange.
/// This abstract class provides a standard way to define and use topics.
/// </summary>
public abstract class ExchangeTopic
{
    /// <summary>
    /// The string value that appears in the 'e' or 'type' field of an incoming JSON message.
    /// </summary>
    public string EventTypeString { get; }

    protected ExchangeTopic(string eventTypeString)
    {
        EventTypeString = eventTypeString;
    }

    /// <summary>
    /// Generates the full stream name for a subscription (e.g., "btcusdt@aggTrade").
    /// </summary>
    /// <param name="symbol">The trading symbol (e.g., BTCUSDT).</param>
    /// <returns>The formatted stream name for the subscription.</returns>
    public abstract string GetStreamName(string symbol);
}

/// <summary>
/// Defines the specific WebSocket topics for Binance.
/// </summary>
public class BinanceTopic : ExchangeTopic
{
    private readonly string _topicSuffix;

    private BinanceTopic(string eventTypeString, string topicSuffix) : base(eventTypeString)
    {
        _topicSuffix = topicSuffix;
    }

    public override string GetStreamName(string symbol) => $"{symbol.ToLowerInvariant()}{_topicSuffix}";

    // Static properties to access topics like an enum
    public static BinanceTopic AggTrade { get; } = new("aggTrade", "@aggTrade");
    public static BinanceTopic BookTicker { get; } = new("bookTicker", "@bookTicker");
    public static BinanceTopic DepthUpdate { get; } = new("depthUpdate", "@depth20@100ms");

    /// <summary>
    /// Gets all defined topics for this exchange using reflection.
    /// </summary>
    public static IEnumerable<BinanceTopic> GetAll()
    {
        return typeof(BinanceTopic)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(BinanceTopic))
            .Select(p => (BinanceTopic)p.GetValue(null)!);
    }
}

/// <summary>
/// A registry to map event type strings to their corresponding ExchangeTopic objects for fast lookups.
/// </summary>
public static class TopicRegistry
{
    private static readonly ConcurrentDictionary<string, ExchangeTopic> _topicsByEventType = new();

    static TopicRegistry()
    {
        // Register all Binance topics
        foreach (var topic in BinanceTopic.GetAll())
        {
            _topicsByEventType.TryAdd(topic.EventTypeString, topic);
        }
        // Future exchanges can be registered here as well
        // foreach (var topic in BybitTopic.GetAll()) { ... }
    }

    /// <summary>
    /// Tries to get the ExchangeTopic associated with a given event type string.
    /// </summary>
    public static bool TryGetTopic(string eventType, out ExchangeTopic? topic)
    {
        return _topicsByEventType.TryGetValue(eventType, out topic);
    }
}
