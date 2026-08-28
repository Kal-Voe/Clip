using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Clip.Shell;

/// <summary>
/// A string-keyed image cache that forgets its least recently used entry when full.
///
/// Both raster caches used to wipe themselves completely the moment they hit their cap, which
/// threw away the very images the neighbour prefetch had just decoded: stepping through a run of
/// screenshots emptied the 12-slot preview cache every ~12 arrow presses, and every image after
/// that point decoded from disk again as though nothing had been prefetched. Dropping only the
/// oldest entry keeps everything recently looked at — the same Recent-list pattern
/// <see cref="SourceAppIcons"/> uses for its icon cache.
///
/// Not thread-safe on its own: callers guard access with their own gate, exactly as they did
/// around the plain dictionaries this replaced.
/// </summary>
internal sealed class RecentImageCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, ImageSource> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();

    public RecentImageCache(int capacity)
    {
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    public bool TryGet(string key, out ImageSource source)
    {
        if (!_entries.TryGetValue(key, out source!))
        {
            return false;
        }

        Touch(key);
        return true;
    }

    public void Remember(string key, ImageSource source)
    {
        _entries[key] = source;
        Touch(key);
        while (_recent.Count > _capacity)
        {
            var oldest = _recent.Last;
            if (oldest is null)
            {
                return;
            }

            _recent.RemoveLast();
            _entries.Remove(oldest.Value);
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _recent.Clear();
    }

    private void Touch(string key)
    {
        _recent.Remove(key);
        _recent.AddFirst(key);
    }
}
