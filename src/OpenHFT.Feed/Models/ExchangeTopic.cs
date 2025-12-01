using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using OpenHFT.Core.Models;

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
    /// Indicates whether this topic requires a specific symbol for subscription (e.g., market data topics).
    /// If false, it's an account-wide topic (e.g., user data topics).
    /// </summary>
    public bool IsSymbolSpecific { get; }
    /// <summary>
    /// The string value that appears in the 'e' or 'type' field of an incoming JSON message.
    /// </summary>
    public string EventTypeString { get; }
    public abstract ExchangeEnum Exchange { get; }

    protected ExchangeTopic(string eventTypeString, bool isSymbolSpecific = true)
    {
        TopicId = Interlocked.Increment(ref _nextId);
        EventTypeString = eventTypeString;
        IsSymbolSpecific = isSymbolSpecific;
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
/// Represents a system-level topic that is not tied to a specific exchange.
/// </summary>
public class SystemTopic : ExchangeTopic
{
    private readonly string _topicName;

    // A system topic's EventTypeString is its unique identifier.
    private SystemTopic(string eventTypeString, string topicName) : base(eventTypeString, false)
    {
        _topicName = topicName;
    }

    // NEW: System topics belong to a "None" or "System" exchange category.
    public override ExchangeEnum Exchange => ExchangeEnum.Undefined; // Assuming ExchangeEnum has a 'NONE' member

    // System topics don't typically have per-symbol stream names.
    public override string GetStreamName(string symbol) => EventTypeString;

    public override string GetTopicName() => _topicName;

    // NEW: Define common system topics here.
    public static SystemTopic FeedMonitor { get; } = new("system.feed.monitor", "FeedMonitor");

    public static IEnumerable<SystemTopic> GetAll()
    {
        return typeof(SystemTopic)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(SystemTopic))
            .Select(p => (SystemTopic)p.GetValue(null)!);
    }
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

    public override ExchangeEnum Exchange => ExchangeEnum.BINANCE;
    private static readonly Lazy<IEnumerable<BinanceTopic>> _allTopics = new(() =>
        typeof(BinanceTopic)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(BinanceTopic))
            .Select(p => (BinanceTopic)p.GetValue(null)!)
            .ToList());

    /// <summary>
    /// Gets all defined market data topics for this exchange.
    /// </summary>
    public static IEnumerable<BinanceTopic> GetAllMarketTopics()
    {
        return _allTopics.Value.Where(t => t.IsSymbolSpecific);
    }

    /// <summary>
    /// Gets all defined private user data topics for this exchange.
    /// </summary>
    public static IEnumerable<BinanceTopic> GetAllPrivateTopics()
    {
        return _allTopics.Value.Where(t => !t.IsSymbolSpecific);
    }
}

/// <summary>
/// Defines the specific WebSocket topics for BitMEX.
/// </summary>
public class BitmexTopic : ExchangeTopic
{
    private readonly string _topicSuffix;
    private readonly string _topicName;

    private BitmexTopic(string eventTypeString, string topicSuffix, string topicName, bool isSymbolSpecific = true) : base(eventTypeString, isSymbolSpecific)
    {
        _topicSuffix = topicSuffix;
        _topicName = topicName;
    }

    public override string GetStreamName(string symbol) => IsSymbolSpecific ? $"{_topicSuffix}{symbol.ToLowerInvariant()}" : _topicSuffix;
    public override string GetTopicName() => _topicName;

    // Static properties to access topics like an enum
    // Live trades
    public static BitmexTopic Trade { get; } = new("trade", "trade:", "trade");
    // Top level of the book
    public static BitmexTopic Quote { get; } = new("quote", "quote:", "quote");
    // Top 10 levels using traditional full book push
    public static BitmexTopic OrderBook10 { get; } = new("orderBook10", "orderBook10:", "orderBook10");
    public static BitmexTopic OrderBookL2_25 { get; } = new("orderBookL2_25", "orderBookL2_25:", "orderBookL2_25:");

    public static BitmexTopic Execution { get; } = new("execution", "execution", "Execution", isSymbolSpecific: false);

    public override ExchangeEnum Exchange => ExchangeEnum.BITMEX;

    private static readonly Lazy<IEnumerable<BitmexTopic>> _allTopics = new(() =>
        typeof(BitmexTopic)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(BitmexTopic))
            .Select(p => (BitmexTopic)p.GetValue(null)!)
            .ToList());

    /// <summary>
    /// Gets all defined market data topics for this exchange.
    /// </summary>
    public static IEnumerable<BitmexTopic> GetAllMarketTopics()
    {
        return _allTopics.Value.Where(t => t.IsSymbolSpecific && t != OrderBookL2_25);
    }

    public static IEnumerable<BitmexTopic> GetAllTopics()
    {
        return _allTopics.Value;
    }

    /// <summary>
    /// Gets all defined private user data topics for this exchange.
    /// </summary>
    public static IEnumerable<BitmexTopic> GetAllPrivateTopics()
    {
        return _allTopics.Value.Where(t => !t.IsSymbolSpecific);
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
        // Register all Binance topics (market and private)
        foreach (var topic in BinanceTopic.GetAllMarketTopics())
        {
            RegisterTopic(topic);
        }
        foreach (var topic in BinanceTopic.GetAllPrivateTopics())
        {
            RegisterTopic(topic);
        }

        // Register all BitMEX topics (market and private)
        foreach (var topic in BitmexTopic.GetAllTopics())
        {
            RegisterTopic(topic);
        }
    }

    private static void RegisterTopic(ExchangeTopic topic)
    {
        _topicsByEventType.TryAdd(topic.EventTypeString, topic);
        _topicsById.TryAdd(topic.TopicId, topic);
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
