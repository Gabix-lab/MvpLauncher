using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MvpLauncher.Infrastructure
{
    public sealed class CachedMetadata
    {
        public long Size { get; set; }
        public long Ticks { get; set; }
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Author { get; set; } = "Unknown";
        public string Description { get; set; } = "";
        public List<string> Depends { get; set; } = new();
        public List<string> Provides { get; set; } = new();
    }

    public sealed class ModMetadataCache
    {
        private const int CacheVersion = 4;

        private readonly string _path;
        private readonly Dictionary<string, CachedMetadata> _entries;
        private readonly object _lock = new();
        private bool _dirty;

        public ModMetadataCache()
        {
            _path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher",
                "mod-metadata.json");

            _entries = Load();
        }

        public bool TryGet(string fileName, FileInfo fi, out FabricModInfo info)
        {
            info = null!;
            lock (_lock)
            {
                if (!_entries.TryGetValue(fileName, out var c)) return false;
                if (c.Size != fi.Length) return false;
                if (c.Ticks != fi.LastWriteTimeUtc.Ticks) return false;

                info = new FabricModInfo
                {
                    Id = c.Id,
                    Name = c.Name,
                    Version = c.Version,
                    Author = c.Author,
                    Description = c.Description
                };
                foreach (var d in c.Depends) info.Depends.Add(d);
                foreach (var p in c.Provides) info.Provides.Add(p);
                return true;
            }
        }

        public void Set(string fileName, FileInfo fi, FabricModInfo info)
        {
            lock (_lock)
            {
                _entries[fileName] = new CachedMetadata
                {
                    Size = fi.Length,
                    Ticks = fi.LastWriteTimeUtc.Ticks,
                    Id = info.Id,
                    Name = info.Name,
                    Version = info.Version,
                    Author = info.Author,
                    Description = info.Description,
                    Depends = info.Depends.ToList(),
                    Provides = info.Provides.ToList()
                };
                _dirty = true;
            }
        }

        public void PruneMissing(IEnumerable<string> existingFileNames)
        {
            var set = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                var toRemove = _entries.Keys.Where(k => !set.Contains(k)).ToList();
                if (toRemove.Count == 0) return;
                foreach (var k in toRemove) _entries.Remove(k);
                _dirty = true;
            }
        }

        public void SaveIfDirty()
        {
            lock (_lock)
            {
                if (!_dirty) return;
                try
                {
                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var wrapper = new CacheWrapper { Version = CacheVersion, Entries = _entries };

                    File.WriteAllText(_path, JsonSerializer.Serialize(wrapper, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    }));
                    _dirty = false;
                }
                catch { }
            }
        }

        private Dictionary<string, CachedMetadata> Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var wrapper = JsonSerializer.Deserialize<CacheWrapper>(json);
                    if (wrapper?.Version == CacheVersion && wrapper.Entries != null)
                        return wrapper.Entries;
                }
            }
            catch { }
            return new Dictionary<string, CachedMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class CacheWrapper
        {
            public int Version { get; set; }
            public Dictionary<string, CachedMetadata>? Entries { get; set; }
        }
    }
}