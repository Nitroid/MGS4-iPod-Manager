using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace iPodManager;

internal sealed class SourceHashCache
{
    private const int CurrentVersion = 5;
    private const int PreviousVersion = 4;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Dictionary<string, CacheEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    private SourceHashCache(string path) => _path = path;

    internal int CacheHits { get; private set; }
    internal int CacheMisses { get; private set; }
    internal int FilesHashed { get; private set; }
    internal int MetadataCacheHits { get; private set; }
    internal int MetadataCacheMisses { get; private set; }
    internal int MetadataFilesRead { get; private set; }
    private long _metadataReadElapsedTicks;
    internal long MetadataReadElapsedMilliseconds =>
        (long)TimeSpan.FromTicks(_metadataReadElapsedTicks).TotalMilliseconds;
    internal int EntryCount => _entries.Count;

    internal static SourceHashCache Load(string path)
    {
        var cache = new SourceHashCache(path);
        if (!File.Exists(path))
            return cache;

        try
        {
            CacheFile? file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllBytes(path), JsonOptions);
            if (file?.Version is not (CurrentVersion or PreviousVersion) || file.Entries == null)
            {
                cache._dirty = true;
                return cache;
            }

            foreach (SerializedEntry? entry in file.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path) || entry.Size < 0 ||
                    !TryReadHash(entry.Sha256, out byte[] hash))
                {
                    cache._dirty = true;
                    continue;
                }

                string canonicalPath;
                try { canonicalPath = Path.GetFullPath(entry.Path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or SecurityException)
                {
                    cache._dirty = true;
                    continue;
                }

                bool metadataIncludesResolvedAnnotation = file.Version == CurrentVersion;
                cache._entries[canonicalPath] = new CacheEntry(
                    entry.Size, entry.LastWriteTimeUtcTicks, hash,
                    metadataIncludesResolvedAnnotation && entry.Title != null &&
                        entry.Artist != null && entry.Album != null
                        ? new SourceMetadata(entry.Title, entry.Artist, entry.Album,
                            entry.Comment ?? string.Empty, entry.TrackNumber, entry.DiscNumber)
                        : null);
            }
            if (file.Version == PreviousVersion)
                cache._dirty = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            SecurityException or JsonException)
        {
            AppLog.Warn($"Source hash cache could not be read; source files will be hashed normally: {ex.Message}");
            cache._entries.Clear();
            cache._dirty = true;
        }

        return cache;
    }

    internal byte[] GetHash(FileInfo file)
    {
        string canonicalPath = Path.GetFullPath(file.FullName);
        long size = file.Length;
        long lastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks;
        if (_entries.TryGetValue(canonicalPath, out CacheEntry? cached) &&
            cached.Size == size && cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks)
        {
            CacheHits++;
            return cached.Sha256;
        }

        CacheMisses++;
        FilesHashed++;
        using FileStream stream = File.OpenRead(canonicalPath);
        byte[] hash = SHA256.HashData(stream);
        _entries[canonicalPath] = new CacheEntry(size, lastWriteTimeUtcTicks, hash, null);
        _dirty = true;
        return hash;
    }

    internal bool TryGetMetadata(FileInfo file, out SourceMetadata metadata)
    {
        string canonicalPath = Path.GetFullPath(file.FullName);
        if (_entries.TryGetValue(canonicalPath, out CacheEntry? cached) &&
            cached.Size == file.Length && cached.LastWriteTimeUtcTicks == file.LastWriteTimeUtc.Ticks &&
            cached.Metadata != null)
        {
            MetadataCacheHits++;
            metadata = cached.Metadata;
            return true;
        }
        MetadataCacheMisses++;
        metadata = null!;
        return false;
    }

    internal void StoreMetadata(FileInfo file, SourceMetadata metadata, TimeSpan elapsed)
    {
        string canonicalPath = Path.GetFullPath(file.FullName);
        if (!_entries.TryGetValue(canonicalPath, out CacheEntry? cached) ||
            cached.Size != file.Length || cached.LastWriteTimeUtcTicks != file.LastWriteTimeUtc.Ticks)
            return;
        MetadataFilesRead++;
        _metadataReadElapsedTicks += elapsed.Ticks;
        if (cached.Metadata != metadata)
        {
            _entries[canonicalPath] = cached with { Metadata = metadata };
            _dirty = true;
        }
    }

    internal void RemoveMissingEntries()
    {
        foreach (string path in _entries.Keys.Where(path => !File.Exists(path)).ToArray())
        {
            _entries.Remove(path);
            _dirty = true;
        }
    }

    internal void Save()
    {
        if (!_dirty)
            return;

        string? directory = Path.GetDirectoryName(_path);
        string temporary = _path + ".tmp";
        try
        {
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var file = new CacheFile
            {
                Version = CurrentVersion,
                Entries = _entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new SerializedEntry
                    {
                        Path = entry.Key,
                        Size = entry.Value.Size,
                        LastWriteTimeUtcTicks = entry.Value.LastWriteTimeUtcTicks,
                        Sha256 = Convert.ToHexString(entry.Value.Sha256),
                        Title = entry.Value.Metadata?.Title,
                        Artist = entry.Value.Metadata?.Artist,
                        Album = entry.Value.Metadata?.Album,
                        Comment = entry.Value.Metadata?.Comment,
                        TrackNumber = entry.Value.Metadata?.TrackNumber ?? 0,
                        DiscNumber = entry.Value.Metadata?.DiscNumber ?? 0
                    }).ToList()
            };
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            AppLog.Warn($"Source hash cache could not be written: {ex.Message}");
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or SecurityException)
            {
                // The cache and its temporary file are disposable diagnostics-free state.
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

    internal sealed record SourceMetadata(
        string Title, string Artist, string Album, string Comment = "",
        uint TrackNumber = 0, uint DiscNumber = 0);

    private sealed record CacheEntry(long Size, long LastWriteTimeUtcTicks, byte[] Sha256,
        SourceMetadata? Metadata);

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public List<SerializedEntry>? Entries { get; set; }
    }

    private sealed class SerializedEntry
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Comment { get; set; }
        public uint TrackNumber { get; set; }
        public uint DiscNumber { get; set; }
    }
}
