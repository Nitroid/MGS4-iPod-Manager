using System.Security.Cryptography;
using iPodManager;

internal static class SourceHashCacheTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "iPodManager-source-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AppLog.Start(Path.Combine(root, "test.log"));
            string cachePath = Path.Combine(root, "source-hashes.json");
            string sourcePath = Path.Combine(root, "track.flac");
            File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);

            SourceHashCache first = SourceHashCache.Load(cachePath);
            Check(!first.TryGetMetadata(new FileInfo(sourcePath), out _),
                "first discovery has no cached source metadata");
            byte[] firstHash = first.GetHash(new FileInfo(sourcePath));
            var originalMetadata = new SourceHashCache.SourceMetadata(
                "Full source title", "Source artist", "Source album",
                "Unicode annotation 注釈\nSecond line");
            first.StoreMetadata(new FileInfo(sourcePath), originalMetadata, TimeSpan.FromMilliseconds(3));
            Check(first.CacheHits == 0 && first.CacheMisses == 1 && first.FilesHashed == 1,
                "source hash cache first discovery hashes file");
            Check(first.MetadataCacheMisses == 1 && first.MetadataFilesRead == 1,
                "first source metadata read is stored with the source identity");
            Check(firstHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(sourcePath))),
                "source hash cache preserves authoritative SHA-256");
            first.Save();

            SourceHashCache unchanged = SourceHashCache.Load(cachePath);
            byte[] unchangedHash = unchanged.GetHash(new FileInfo(sourcePath));
            bool metadataHit = unchanged.TryGetMetadata(new FileInfo(sourcePath), out var unchangedMetadata);
            Check(unchanged.CacheHits == 1 && unchanged.FilesHashed == 0 &&
                unchangedHash.SequenceEqual(firstHash),
                "source hash cache reuses unchanged file hash");
            Check(metadataHit && unchanged.MetadataCacheHits == 1 &&
                unchanged.MetadataFilesRead == 0 && unchangedMetadata == originalMetadata,
                "unchanged source reuses title, artist, album, and Comment without another source read");

            string legacyCachePath = Path.Combine(root, "source-hashes-v3.json");
            File.WriteAllText(legacyCachePath,
                File.ReadAllText(cachePath).Replace("\"version\": 5", "\"version\": 4",
                    StringComparison.Ordinal));
            SourceHashCache legacy = SourceHashCache.Load(legacyCachePath);
            Check(!legacy.TryGetMetadata(new FileInfo(sourcePath), out _) &&
                legacy.GetHash(new FileInfo(sourcePath)).SequenceEqual(firstHash) &&
                legacy.CacheHits == 1 && legacy.FilesHashed == 0,
                "version 4 cache retains the source hash but refreshes metadata for track and disc numbers");

            File.SetLastWriteTimeUtc(sourcePath, File.GetLastWriteTimeUtc(sourcePath).AddSeconds(5));
            DateTime changedTimestamp = File.GetLastWriteTimeUtc(sourcePath);
            SourceHashCache timestampChanged = SourceHashCache.Load(cachePath);
            Check(!timestampChanged.TryGetMetadata(new FileInfo(sourcePath), out _),
                "changed timestamp invalidates cached source metadata");
            timestampChanged.GetHash(new FileInfo(sourcePath));
            timestampChanged.StoreMetadata(new FileInfo(sourcePath),
                new("Timestamp title", "Timestamp artist", "Timestamp album"), TimeSpan.Zero);
            Check(timestampChanged.CacheMisses == 1 && timestampChanged.FilesHashed == 1,
                "source hash cache rehashes changed timestamp");
            timestampChanged.Save();

            File.AppendAllText(sourcePath, "changed-size");
            File.SetLastWriteTimeUtc(sourcePath, changedTimestamp);
            SourceHashCache sizeChanged = SourceHashCache.Load(cachePath);
            Check(!sizeChanged.TryGetMetadata(new FileInfo(sourcePath), out _),
                "changed size invalidates cached source metadata");
            sizeChanged.GetHash(new FileInfo(sourcePath));
            Check(sizeChanged.CacheMisses == 1 && sizeChanged.FilesHashed == 1,
                "source hash cache rehashes changed size");
            sizeChanged.Save();

            string newPath = Path.Combine(root, "new-track.mp3");
            File.WriteAllBytes(newPath, [5, 6, 7]);
            SourceHashCache newFile = SourceHashCache.Load(cachePath);
            Check(!newFile.TryGetMetadata(new FileInfo(newPath), out _),
                "new source has no cached metadata");
            newFile.GetHash(new FileInfo(newPath));
            var fallbackMetadata = new SourceHashCache.SourceMetadata(
                "new-track", string.Empty, "CUSTOM", "   ");
            newFile.StoreMetadata(new FileInfo(newPath), fallbackMetadata, TimeSpan.Zero);
            Check(newFile.CacheMisses == 1 && newFile.FilesHashed == 1,
                "source hash cache hashes new file");
            newFile.Save();

            File.Delete(sourcePath);
            SourceHashCache deleted = SourceHashCache.Load(cachePath);
            deleted.RemoveMissingEntries();
            deleted.Save();
            Check(SourceHashCache.Load(cachePath).EntryCount == 1,
                "source hash cache removes deleted file");

            File.WriteAllText(cachePath, "{ malformed cache");
            SourceHashCache malformed = SourceHashCache.Load(cachePath);
            Check(!malformed.TryGetMetadata(new FileInfo(newPath), out _),
                "malformed source cache cannot supply stale metadata");
            byte[] recoveredHash = malformed.GetHash(new FileInfo(newPath));
            malformed.Save();
            Check(malformed.FilesHashed == 1 &&
                recoveredHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(newPath))),
                "malformed source hash cache falls back safely");
            Check(fallbackMetadata.Title == "new-track" && fallbackMetadata.Artist.Length == 0 &&
                fallbackMetadata.Album == "CUSTOM" && string.IsNullOrWhiteSpace(fallbackMetadata.Comment),
                "missing source tags retain the existing filename, empty artist, and category fallback semantics");
            File.WriteAllText(cachePath, "{\"version\":5,\"entries\":[null]}");
            SourceHashCache nullEntry = SourceHashCache.Load(cachePath);
            Check(nullEntry.GetHash(new FileInfo(newPath)).SequenceEqual(recoveredHash) &&
                nullEntry.FilesHashed == 1, "null source-cache entries fall back to hashing");
            nullEntry.Save();
            Check(SourceHashCache.Load(cachePath).EntryCount == 1,
                "source-cache null entries are repaired on save");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }
}

