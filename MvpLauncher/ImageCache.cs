using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace MvpLauncher.Infrastructure
{
    public static class ImageCache
    {
        private const int MaxEntries = 128;
        private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Bmp)>> _map = new();
        private static readonly LinkedList<(string Key, BitmapImage Bmp)> _lru = new();
        private static readonly object _lock = new();

        public static BitmapImage? GetOrLoad(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Bmp;
                }
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(key, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();

                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var existing))
                    {
                        _lru.Remove(existing);
                        _lru.AddFirst(existing);
                        return existing.Value.Bmp;
                    }

                    var node = new LinkedListNode<(string, BitmapImage)>((key, bmp));
                    _lru.AddFirst(node);
                    _map[key] = node;

                    while (_map.Count > MaxEntries)
                    {
                        var last = _lru.Last;
                        if (last == null) break;
                        _lru.RemoveLast();
                        _map.Remove(last.Value.Key);
                    }
                }
                return bmp;
            }
            catch { return null; }
        }
    }
}