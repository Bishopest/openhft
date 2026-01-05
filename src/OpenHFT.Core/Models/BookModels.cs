using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Models;

[StructLayout(LayoutKind.Sequential, Pack = 8)] // 메모리 정렬 최적화
public struct PriceLevel
{
    public Price Price;
    public Quantity TotalQuantity;
    public long OrderCount;
    public long LastUpdateSequence;
    public long LastUpdateTimestamp;

    public void Update(Quantity quantity, long sequence, long timestamp)
    {
        TotalQuantity = quantity;
        LastUpdateSequence = sequence;
        LastUpdateTimestamp = timestamp;
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
    private readonly Side _side;
    private readonly PriceLevel[] _data;
    private int _count;
    private readonly bool _isAscending;
    private readonly int _maxDepth;

    public BookSide(Side side, int maxDepth = 5000)
    {
        _side = side;
        _maxDepth = maxDepth;
        _data = new PriceLevel[maxDepth];
        _isAscending = side == Side.Sell;
    }

    public Side Side => _side;
    public int LevelCount => _count;
    public ReadOnlySpan<PriceLevel> AsSpan() => _data.AsSpan(0, _count);

    // 1. 최우선 호가 조회 (O(1), Zero-copy)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly PriceLevel GetBestLevel()
    {
        if (_count == 0) return ref Unsafe.NullRef<PriceLevel>();
        return ref _data[0]; // 무조건 첫 번째 요소가 Best
    }

    // 2. 인덱스 기반 조회 (O(1), Zero-copy)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly PriceLevel GetLevelAt(int index)
    {
        if (index < 0 || index >= _count) return ref Unsafe.NullRef<PriceLevel>();
        return ref _data[index];
    }

    // 3. 특정 가격의 레벨 찾기 (O(log N))
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindIndex(Price price)
    {
        int low = 0;
        int high = _count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int comp = _data[mid].Price.CompareTo(price);
            if (!_isAscending) comp *= -1;

            if (comp == 0) return mid;
            if (comp < 0) low = mid + 1;
            else high = mid - 1;
        }
        return ~low;
    }

    // 4. 레벨 업데이트 (O(N)이나 실제로는 Array.Copy로 인해 극도로 빠름)
    public void UpdateLevel(Price price, Quantity quantity, long sequence, long timestamp)
    {
        int index = FindIndex(price);

        if (quantity.ToTicks() <= 0)
        {
            // --- 삭제 로직 ---
            if (index >= 0)
            {
                if (index < _count - 1)
                {
                    Array.Copy(_data, index + 1, _data, index, _count - index - 1);
                }
                _count--;
            }
        }
        else
        {
            // --- 추가 또는 업데이트 로직 ---
            if (index >= 0)
            {
                // 1. 이미 존재하는 가격이면 업데이트
                _data[index].Update(quantity, sequence, timestamp);
            }
            else
            {
                // 2. 새로운 가격인 경우 삽입 위치 찾기
                int insertIdx = ~index;

                // [바이낸스 지침 반영] 
                // 만약 배열이 꽉 찼는데, 삽입하려는 위치가 마지막 인덱스(maxDepth)와 같거나 크다면?
                // 이 가격은 현재 우리가 추적하는 상위 5000개보다 더 나쁜 가격이므로 무시합니다.
                if (_count == _maxDepth && insertIdx >= _maxDepth)
                {
                    return;
                }

                // 삽입 위치가 maxDepth를 넘지 않도록 다시 한번 보정 (안전 장치)
                if (insertIdx >= _maxDepth) return;

                if (insertIdx < _count)
                {
                    // 배열이 가득 찼다면 마지막 요소는 버려질 것이므로 이동 개수를 조정
                    int elementsToMove = (_count == _maxDepth)
                        ? (_maxDepth - insertIdx - 1)
                        : (_count - insertIdx);

                    if (elementsToMove > 0)
                    {
                        Array.Copy(_data, insertIdx, _data, insertIdx + 1, elementsToMove);
                    }
                }

                // 새 데이터 삽입
                _data[insertIdx] = new PriceLevel { Price = price };
                _data[insertIdx].Update(quantity, sequence, timestamp);

                // 개수 증가
                if (_count < _maxDepth)
                {
                    _count++;
                }
            }
        }
    }

    public IEnumerable<PriceLevel> GetTopLevels(int count)
    {
        // _data 배열의 0부터 실제 데이터 개수(_count) 혹은 요청 개수 중 작은 값만큼만 반환
        int limit = Math.Min(count, _count);
        for (int i = 0; i < limit; i++)
        {
            yield return _data[i];
        }
    }

    public IEnumerable<PriceLevel> GetAllLevels()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return _data[i];
        }
    }

    public void Clear()
    {
        _count = 0;
    }

    /// <summary>
    /// Calculate total depth (quantity) for top N levels
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Quantity GetDepth(int levels)
    {
        Quantity totalDepth = Quantity.FromTicks(0);
        int limit = Math.Min(levels, _count);

        // LINQ나 foreach 대신 인덱스 기반 for 루프를 사용하여 성능 최적화
        for (int i = 0; i < limit; i++)
        {
            totalDepth += _data[i].TotalQuantity;
        }

        return totalDepth;
    }
}
