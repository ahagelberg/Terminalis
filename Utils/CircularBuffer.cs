using System.Collections.Generic;

namespace TabbySSH.Utils;

public class CircularBuffer<T>
{
    private readonly List<T> _buffer = new();
    private readonly int _maxSize;
    private int _startIndex = 0;

    public CircularBuffer(int maxSize)
    {
        _maxSize = maxSize;
    }

    public int Count => _buffer.Count;

    public void Add(T item)
    {
        if (_buffer.Count < _maxSize)
        {
            _buffer.Add(item);
        }
        else
        {
            _buffer[_startIndex] = item;
            _startIndex = (_startIndex + 1) % _maxSize;
        }
    }

    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public T[] ToArray()
    {
        if (_buffer.Count < _maxSize)
        {
            return _buffer.ToArray();
        }
        
        var result = new T[_maxSize];
        for (int i = 0; i < _maxSize; i++)
        {
            result[i] = _buffer[(_startIndex + i) % _maxSize];
        }
        return result;
    }

    public void Clear()
    {
        _buffer.Clear();
        _startIndex = 0;
    }
}
