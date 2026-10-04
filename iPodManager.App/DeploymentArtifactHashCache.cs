using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace iPodManager;

internal sealed class DeploymentArtifactHashCache
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Dictionary<string, CacheEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _dirty;

    private DeploymentArtifactHashCache(string path) => _path = path;

    internal int CacheHits;
    internal int CacheMisses;
    internal int FilesHashed;
    internal int EntryCount { get { lock (_gate) return _entries.Count; } }

    internal static DeploymentArtifactHashCache Load(string path)
    {
        var cache = new DeploymentArtifactHashCache(path);
        if (!File.Exists(path))
            return cache;

        try
        {
            CacheFile? file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllBytes(path), JsonOptions);
            if (file?.Version != CurrentVersion || file.Entries == null)
            {
                cache._dirty = true;
                return cache;
            }

            foreach (SerializedEntry? entry in file.Entries)
            {
                if (entry == null || entry.Generation == 0 || string.IsNullOrWhiteSpace(entry.Path) || entry.Size < 0 ||
                    !TryReadHash(entry.ValidatedSha256, out byte[] hash))
                {
                    cache._dirty = true;
                    continue;
                }
                try
                {
                    string canonicalPath = Path.GetFullPath(entry.Path);
                    cache._entries[canonicalPath] = new(entry.Generation, entry.Size,
                        entry.LastWriteTimeUtcTicks, hash);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or SecurityException)
                {
                    cache._dirty = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            AppLog.Warn($"Deployment artifact hash cache could not be read; DBMs will be hashed normally: {ex.Message}");
            cache._entries.Clear();
            cache._dirty = true;
        }
        return cache;
    }

    internal byte[] GetValidatedHash(FileInfo file, byte[] expectedHash, ulong generation)
    {
        string canonicalPath = Path.GetFullPath(file.FullName);
        long size = file.Length;
        long lastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks;
        lock (_gate)
        {
            if (_entries.TryGetValue(canonicalPath, out CacheEntry? cached) &&
                cached.Generation == generation && cached.Size == size &&
                cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks &&
                cached.ValidatedSha256.SequenceEqual(expectedHash))
            {
                CacheHits++;
                return cached.ValidatedSha256;
            }
            CacheMisses++;
        }

        using FileStream stream = File.OpenRead(canonicalPath);
        byte[] hash = SHA256.HashData(stream);
        file.Refresh();
        if (file.Length != size || file.LastWriteTimeUtc.Ticks != lastWriteTimeUtcTicks)
            throw new IOException("Deployment artifact changed during validation.");

        lock (_gate)
        {
            FilesHashed++;
            if (hash.SequenceEqual(expectedHash))
            {
                var entry = new CacheEntry(generation, size, lastWriteTimeUtcTicks, hash);
                if (!_entries.TryGetValue(canonicalPath, out CacheEntry? prior) || prior != entry)
                {
                    _entries[canonicalPath] = entry;
                    _dirty = true;
                }
            }
            else if (_entries.Remove(canonicalPath))
            {
                _dirty = true;
            }
        }
        return hash;
    }

    internal void RetainOnly(IReadOnlySet<string> paths)
    {
        lock (_gate)
        {
            foreach (string path in _entries.Keys.Where(path => !paths.Contains(path)).ToArray())
            {
                _entries.Remove(path);
                _dirty = true;
            }
        }
    }

    internal void Save()
    {
        CacheFile file;
        lock (_gate)
        {
            if (!_dirty)
                return;
            file = new CacheFile
            {
                Version = CurrentVersion,
                Entries = _entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new SerializedEntry
                    {
                        Path = entry.Key,
                        Generation = entry.Value.Generation,
                        Size = entry.Value.Size,
                        LastWriteTimeUtcTicks = entry.Value.LastWriteTimeUtcTicks,
                        ValidatedSha256 = Convert.ToHexString(entry.Value.ValidatedSha256)
                    }).ToList()
            };
        }

        string? directory = Path.GetDirectoryName(_path);
        string temporary = _path + ".tmp";
        try
        {
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            lock (_gate) _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            AppLog.Warn($"Deployment artifact hash cache could not be written: {ex.Message}");
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or SecurityException)
            {
                // This cache and its temporary file are disposable derived state.
            }
        }
    }

    private static bool TryReadHash(string? value, out byte[] hash)
    {
        hash = [];
        if (value?.Length != 64)
            return false;
        try { hash = Convert.FromHexString(value); }
        catch (FormatException) { return false; }
        return hash.Length == 32;
    }

    private sealed record CacheEntry(ulong Generation, long Size, long LastWriteTimeUtcTicks,
        byte[] ValidatedSha256);

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public List<SerializedEntry>? Entries { get; set; }
    }

    private sealed class SerializedEntry
    {
        public string Path { get; set; } = string.Empty;
        public ulong Generation { get; set; }
        public long Size { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public string ValidatedSha256 { get; set; } = string.Empty;
    }
}
