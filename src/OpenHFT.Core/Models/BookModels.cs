using System.Runtime.CompilerServices;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Models;

/// <summary>
/// Represents a single price level in the order book
/// Optimized for performance with minimal allocations
/// </summary>
public class PriceLevel
{
    public Price Price { get; set; }
    public Quantity TotalQuantity { get; set; }
    public long OrderCount { get; set; }
    public long LastUpdateSequence { get; set; }
    public long LastUpdateTimestamp { get; set; }

    // L3 specific: list of individual orders at this level (for simulation)
    public List<OrderEntry>? Orders { get; set; }

    public PriceLevel(Price price)
    {
        Price = price;
        TotalQuantity = Quantity.FromTicks(0);
        OrderCount = 0;
        LastUpdateSequence = 0;
        LastUpdateTimestamp = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(Quantity quantity, long sequence, long timestamp)
    {
        TotalQuantity = quantity;
        LastUpdateSequence = sequence;
        LastUpdateTimestamp = timestamp;

        // Update order count (simplified - in real L3 this would be managed differently)
        OrderCount = quantity.ToTicks() > 0 ? Math.Max(1, OrderCount) : 0;
    }

    public bool IsEmpty => TotalQuantity.ToTicks() <= 0;

    public override string ToString() => $"{Price}@{TotalQuantity}({OrderCount})";
}

/// <summary>
/// Represents an individual order in L3 book (simulation)
/// </summary>
public class OrderEntry
{
    public long OrderId { get; set; }
    public Quantity Quantity { get; set; }
    public long Timestamp { get; set; }
    public long Sequence { get; set; }
    public int Priority { get; set; } // Time priority within price level

    public OrderEntry(long orderId, Quantity quantity, long timestamp, long sequence)
    {
        OrderId = orderId;
        Quantity = quantity;
        Timestamp = timestamp;
        Sequence = sequence;
        Priority = 0;
    }

    public override string ToString() => $"Order[{OrderId}]: {Quantity}@{Timestamp}";
}

/// <summary>
/// Book side (Bid or Ask) with sorted price levels
/// </summary>
public class BookSide
{
    private readonly SortedDictionary<Price, PriceLevel> _levels;
    private readonly Side _side;
    private readonly bool _isAscending;

    public BookSide(Side side)
    {
        _side = side;
        _isAscending = side == Side.Sell; // Asks ascending, Bids descending

        // Custom comparer for price levels
        var comparer = _isAscending ? Comparer<Price>.Default : Comparer<Price>.Create((x, y) => y.CompareTo(x));
        _levels = new SortedDictionary<Price, PriceLevel>(comparer);
    }

    public Side Side => _side;
    public int LevelCount => _levels.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateLevel(Price price, Quantity quantity, long sequence, long timestamp)
    {
        if (quantity.ToTicks() <= 0)
        {
            // Remove level
            _levels.Remove(price);
        }
        else
        {
            // Add or update level
            if (!_levels.TryGetValue(price, out var level))
            {
                level = new PriceLevel(price);
                _levels[price] = level;
            }
            level.Update(quantity, sequence, timestamp);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PriceLevel? GetBestLevel()
    {
        return _levels.Count > 0 ? _levels.First().Value : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PriceLevel? GetLevelAt(int index)
    {
        if (index >= _levels.Count) return null;
        return _levels.Skip(index).First().Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PriceLevel? GetLevel(Price price)
    {
        return _levels.TryGetValue(price, out var level) ? level : null;
    }

    public IEnumerable<PriceLevel> GetTopLevels(int count)
    {
        return _levels.Values.Take(count);
    }

    public IEnumerable<PriceLevel> GetAllLevels()
    {
        return _levels.Values;
    }

    public void Clear()
    {
        _levels.Clear();
    }

    /// <summary>
    /// Calculate total depth (quantity) for top N levels
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Quantity GetDepth(int levels)
    {
        Quantity totalDepth = Quantity.FromTicks(0);
        int count = 0;

        foreach (var level in _levels.Values)
        {
            if (count >= levels) break;
            totalDepth += level.TotalQuantity; // Uses Quantity's operator+
            count++;
        }

        return totalDepth;
    }

    /// <summary>
    /// Get total quantity available at or better than specified price
    /// </summary>
    public Quantity GetQuantityAtOrBetter(Price price)
    {
        Quantity totalQuantity = Quantity.FromTicks(0);

        foreach (var kvp in _levels)
        {
            var levelPrice = kvp.Key;
            var level = kvp.Value;

            bool includeLevel = _side == Side.Buy ? levelPrice >= price : levelPrice <= price;

            if (includeLevel)
            {
                totalQuantity += level.TotalQuantity;
            }
            else
            {
                break; // Levels are sorted, so we can stop here
            }
        }

        return totalQuantity;
    }
}
