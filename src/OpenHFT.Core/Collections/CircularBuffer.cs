using System;

namespace OpenHFT.Core.Collections;

/// <summary>
/// fixed size buffer to calculate average over rolling N items
/// use lock for thread-safety
/// </summary>
/// <typeparam name="T"></typeparam>
public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _tail;
    private int _count;
    private readonly object _lock = new();

    public CircularBuffer(int capacity)
    {
        _buffer = new T[capacity];
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
            else
            {
                _tail = (_tail + 1) % _buffer.Length;
            }
        }
    }

    public double Average
    {
        get
        {
            lock (_lock)
            {
                if (_count == 0) return 0.0;

                double sum = 0;
                for (int i = 0; i < _count; i++)
                {
                    sum += Convert.ToDouble(_buffer[(_tail + i) % _buffer.Length]);
                }
                return sum / _count;
            }
        }
    }

    /// <summary>
    /// Copies the elements of the circular buffer to a new array.
    /// </summary>
    /// <returns>A new array containing the elements of the circular buffer.</returns>
    public T[] ToArray()
    {
        lock (_lock)
        {
            var array = new T[_count];
            for (int i = 0; i < _count; i++)
            {
                array[i] = _buffer[(_tail + i) % _buffer.Length];
            }
            return array;
        }
    }
}