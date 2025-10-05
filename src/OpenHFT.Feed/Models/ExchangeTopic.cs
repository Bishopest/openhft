using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace OpenHFT.Feed.Models;

/// <summary>
/// Represents a specific WebSocket topic for an exchange.
/// This abstract class provides a standard way to define and use topics.
/// </summary>
public abstract class ExchangeTopic
{
    private static int _nextId = 0;

    /// <summary>
    /// A unique identifier for this topic instance.
    /// </summary>
    public int TopicId { get; }
    /// <summary>
    /// The string value that appears in the 'e' or 'type' field of an incoming JSON message.
    /// </summary>
    public string EventTypeString { get; }

    protected ExchangeTopic(string eventTypeString)
    {
        TopicId = Interlocked.Increment(ref _nextId);
        EventTypeString = eventTypeString;
    }

    /// <summary>
    /// Generates the full stream name for a subscription (e.g., "btcusdt@aggTrade").
    /// </summary>
    /// <param name="symbol">The trading symbol (e.g., BTCUSDT).</param>
    /// <returns>The formatted stream name for the subscription.</returns>
    public abstract string GetStreamName(string symbol);

    /// <summary>
    /// Returns the simple name of the topic (e.g., AggTrade, BookTicker).
    /// </summary>
    public abstract string GetTopicName();
}

/// <summary>
/// Defines the specific WebSocket topics for Binance.
/// </summary>
public class BinanceTopic : ExchangeTopic
{
    private readonly string _topicSuffix;
    private readonly string _topicName;

    private BinanceTopic(string eventTypeString, string topicSuffix, string topicName) : base(eventTypeString)
    {
        _topicSuffix = topicSuffix;
        _topicName = topicName;
    }

    public override string GetStreamName(string symbol) => $"{symbol.ToLowerInvariant()}{_topicSuffix}";
    public override string GetTopicName() => _topicName;

    // Static properties to access topics like an enum
    public static BinanceTopic AggTrade { get; } = new("aggTrade", "@aggTrade", "AggTrade");
    public static BinanceTopic BookTicker { get; } = new("bookTicker", "@bookTicker", "BookTicker");
    public static BinanceTopic DepthUpdate { get; } = new("depthUpdate", "@depth20@100ms", "DepthUpdate");

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
    private static readonly ConcurrentDictionary<int, ExchangeTopic> _topicsById = new();

    static TopicRegistry()
    {
        // Register all Binance topics
        foreach (var topic in BinanceTopic.GetAll())
        {
            _topicsByEventType.TryAdd(topic.EventTypeString, topic);
            _topicsById.TryAdd(topic.TopicId, topic);
        }
        // Future exchanges can be registered here as well
        // foreach (var topic in BybitTopic.GetAll()) { ... }
    }

    /// <summary>
    /// Tries to get the ExchangeTopic associated with a given event type string.
    /// </summary>
    public static bool TryGetTopic(string eventType, [NotNullWhen(true)] out ExchangeTopic? topic)
    {
        return _topicsByEventType.TryGetValue(eventType, out topic);
    }

    /// <summary>
    /// Tries to get the ExchangeTopic associated with a given topic ID.
    /// </summary>
    public static bool TryGetTopic(int topicId, [NotNullWhen(true)] out ExchangeTopic? topic)
    {
        return _topicsById.TryGetValue(topicId, out topic);
    }
}
