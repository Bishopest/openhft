using System.Runtime.CompilerServices;
using System.Threading;

namespace OpenHFT.Core.Collections;

/// <summary>
/// High-performance lock-free ring buffer for market data events
/// Single producer, single consumer (SPSC) design for maximum throughput
/// Uses memory barriers for thread safety without locks
/// </summary>
/// <typeparam name="T">Event type</typeparam>
public unsafe class LockFreeRingBuffer<T> where T : unmanaged
{
    private readonly T* _buffer;
    private readonly int _capacity;
    private readonly int _mask;
    private long _writeIndex;
    private long _readIndex;
    private readonly IntPtr _bufferPtr;

    public LockFreeRingBuffer(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of 2", nameof(capacity));

        _capacity = capacity;
        _mask = capacity - 1;
        _writeIndex = 0;
        _readIndex = 0;

        // Allocate aligned memory for better cache performance
        int sizeInBytes = capacity * sizeof(T);
        _bufferPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeInBytes);
        _buffer = (T*)_bufferPtr.ToPointer();

        // Initialize memory to zero
        Unsafe.InitBlockUnaligned(_buffer, 0, (uint)sizeInBytes);
    }

    ~LockFreeRingBuffer()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_bufferPtr != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(_bufferPtr);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Try to write an item to the buffer (producer side)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in T item)
    {
        long currentWrite = Volatile.Read(ref _writeIndex);
        long currentRead = Volatile.Read(ref _readIndex);

        // Check if buffer is full
        if (currentWrite - currentRead >= _capacity)
            return false;

        // Write the item
        _buffer[currentWrite & _mask] = item;

        // Memory barrier to ensure write completes before advancing index
        Thread.MemoryBarrier();

        // Advance write index
        Volatile.Write(ref _writeIndex, currentWrite + 1);

        return true;
    }

    /// <summary>
    /// Try to read an item from the buffer (consumer side)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out T item)
    {
        long currentRead = Volatile.Read(ref _readIndex);
        long currentWrite = Volatile.Read(ref _writeIndex);

        // Check if buffer is empty
        if (currentRead >= currentWrite)
        {
            item = default;
            return false;
        }

        // Read the item
        item = _buffer[currentRead & _mask];

        // Memory barrier to ensure read completes before advancing index
        Thread.MemoryBarrier();

        // Advance read index
        Volatile.Write(ref _readIndex, currentRead + 1);

        return true;
    }

    /// <summary>
    /// Get current number of items in buffer
    /// </summary>
    public long Count => Math.Max(0, Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex));

    /// <summary>
    /// Check if buffer is empty
    /// </summary>
    public bool IsEmpty => Volatile.Read(ref _readIndex) >= Volatile.Read(ref _writeIndex);

    /// <summary>
    /// Check if buffer is full
    /// </summary>
    public bool IsFull => Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex) >= _capacity;

    /// <summary>
    /// Get buffer capacity
    /// </summary>
    public int Capacity => _capacity;
}

/// <summary>
/// Multi-producer, single-consumer ring buffer for scenarios with multiple writers
/// Uses CAS operations for thread-safe writing
/// </summary>
/// <typeparam name="T">Event type</typeparam>
public class MPSCRingBuffer<T> where T : class
{
    private readonly T?[] _buffer;
    private readonly int _capacity;
    private readonly int _mask;
    private long _writeIndex;
    private long _readIndex;
    private readonly object _writeLock = new();

    public MPSCRingBuffer(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of 2", nameof(capacity));

        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = new T?[capacity];
        _writeIndex = 0;
        _readIndex = 0;
    }

    /// <summary>
    /// Try to write an item to the buffer (thread-safe for multiple producers)
    /// </summary>
    public bool TryWrite(T item)
    {
        lock (_writeLock)
        {
            long currentWrite = Volatile.Read(ref _writeIndex);
            long currentRead = Volatile.Read(ref _readIndex);

            // Check if buffer is full
            if (currentWrite - currentRead >= _capacity)
                return false;

            // Write the item
            _buffer[currentWrite & _mask] = item;

            // Advance write index
            Volatile.Write(ref _writeIndex, currentWrite + 1);

            return true;
        }
    }

    /// <summary>
    /// Try to read an item from the buffer (single consumer)
    /// </summary>
    public bool TryRead(out T? item)
    {
        long currentRead = Volatile.Read(ref _readIndex);
        long currentWrite = Volatile.Read(ref _writeIndex);

        // Check if buffer is empty
        if (currentRead >= currentWrite)
        {
            item = default;
            return false;
        }

        // Read the item
        item = _buffer[currentRead & _mask];
        _buffer[currentRead & _mask] = default; // Clear reference

        // Advance read index
        Volatile.Write(ref _readIndex, currentRead + 1);

        return true;
    }

    /// <summary>
    /// Get current number of items in buffer
    /// </summary>
    public long Count => Math.Max(0, Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex));

    /// <summary>
    /// Check if buffer is empty
    /// </summary>
    public bool IsEmpty => Volatile.Read(ref _readIndex) >= Volatile.Read(ref _writeIndex);

    /// <summary>
    /// Check if buffer is full
    /// </summary>
    public bool IsFull => Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex) >= _capacity;
}
