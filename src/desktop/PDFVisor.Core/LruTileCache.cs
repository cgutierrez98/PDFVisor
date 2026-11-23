namespace PDFVisor.Core;

public readonly record struct TileKey(
    int DocumentRevision,
    int PageIndex,
    int Rotation,
    int FullWidthPx,
    int FullHeightPx,
    int TileX,
    int TileY,
    int TileWidthPx,
    int TileHeightPx);

/// <summary>
/// Thread-safe, O(1), memory-budgeted Least-Recently-Used (LRU) cache for rendered PDF page tiles.
/// </summary>
public sealed class LruTileCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(TKey Key, TValue Value, long ByteSize);

    private readonly object _sync = new();
    private readonly long _maxBytes;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lruList = new();
    private long _currentBytes;

    public long MaxBytes => _maxBytes;

    public long CurrentBytes
    {
        get { lock (_sync) return _currentBytes; }
    }

    public int Count
    {
        get { lock (_sync) return _map.Count; }
    }

    public LruTileCache(long maxBytes = 192L * 1024 * 1024)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    public void Put(TKey key, TValue value, long byteSize)
    {
        if (byteSize < 0) byteSize = 0;

        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.ByteSize;
                _lruList.Remove(existing);
                _map.Remove(key);
            }

            // Evict least-recently-used entries until within budget
            while (_currentBytes + byteSize > _maxBytes && _lruList.Last is not null)
            {
                var victim = _lruList.Last;
                _lruList.RemoveLast();
                _map.Remove(victim.Value.Key);
                _currentBytes -= victim.Value.ByteSize;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, value, byteSize));
            _lruList.AddFirst(node);
            _map[key] = node;
            _currentBytes += byteSize;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _lruList.Clear();
            _currentBytes = 0;
        }
    }
}
