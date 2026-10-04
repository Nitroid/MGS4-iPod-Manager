using System.Security.Cryptography;
using iPodManager;

internal static class DeploymentArtifactHashCacheTests
{
    internal static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL " + name);
            Console.WriteLine("PASS " + name);
        }

        string root = Path.Combine(Path.GetTempPath(), "deployment-hash-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string dbm = Path.Combine(root, "track.dbm");
            string cachePath = Path.Combine(root, "deployment-artifact-hashes.json");
            File.WriteAllBytes(dbm, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
            byte[] expected = SHA256.HashData(File.ReadAllBytes(dbm));

            DeploymentArtifactHashCache first = DeploymentArtifactHashCache.Load(cachePath);
            Check(first.GetValidatedHash(new FileInfo(dbm), expected, 7).SequenceEqual(expected) &&
                first.CacheMisses == 1 && first.FilesHashed == 1,
                "first deployment validation hashes an uncached DBM");
            first.RetainOnly(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(dbm) });
            first.Save();

            DeploymentArtifactHashCache second = DeploymentArtifactHashCache.Load(cachePath);
            Check(second.GetValidatedHash(new FileInfo(dbm), expected, 7).SequenceEqual(expected) &&
                second.CacheHits == 1 && second.FilesHashed == 0,
                "unchanged DBM reuses its prior successful SHA-256 validation");

            File.SetLastWriteTimeUtc(dbm, File.GetLastWriteTimeUtc(dbm).AddSeconds(2));
            DeploymentArtifactHashCache timestampChanged = DeploymentArtifactHashCache.Load(cachePath);
            _ = timestampChanged.GetValidatedHash(new FileInfo(dbm), expected, 7);
            Check(timestampChanged.CacheMisses == 1 && timestampChanged.FilesHashed == 1,
                "changed DBM timestamp forces a physical hash");

            byte[] larger = File.ReadAllBytes(dbm).Concat(new byte[] { 1 }).ToArray();
            File.WriteAllBytes(dbm, larger);
            byte[] largerHash = SHA256.HashData(larger);
            DeploymentArtifactHashCache sizeChanged = DeploymentArtifactHashCache.Load(cachePath);
            _ = sizeChanged.GetValidatedHash(new FileInfo(dbm), largerHash, 7);
            Check(sizeChanged.CacheMisses == 1 && sizeChanged.FilesHashed == 1,
                "changed DBM size forces a physical hash");

            byte[] wrongExpected = SHA256.HashData("different manifest hash"u8);
            DeploymentArtifactHashCache expectedChanged = DeploymentArtifactHashCache.Load(cachePath);
            byte[] actual = expectedChanged.GetValidatedHash(new FileInfo(dbm), wrongExpected, 7);
            Check(expectedChanged.CacheMisses == 1 && expectedChanged.FilesHashed == 1 &&
                !actual.SequenceEqual(wrongExpected),
                "changed manifest expectation cannot reuse or bless a cached hash");

            DeploymentArtifactHashCache generationChanged = DeploymentArtifactHashCache.Load(cachePath);
            _ = generationChanged.GetValidatedHash(new FileInfo(dbm), largerHash, 8);
            Check(generationChanged.CacheMisses == 1 && generationChanged.FilesHashed == 1,
                "changed deployment generation forces validation");

            generationChanged.Save();
            byte[] corrupt = (byte[])larger.Clone();
            corrupt[12] ^= 0x5a;
            File.WriteAllBytes(dbm, corrupt);
            DeploymentArtifactHashCache corrupted = DeploymentArtifactHashCache.Load(cachePath);
            byte[] corruptHash = corrupted.GetValidatedHash(new FileInfo(dbm), largerHash, 8);
            Check(corrupted.CacheMisses == 1 && corrupted.FilesHashed == 1 &&
                !corruptHash.SequenceEqual(largerHash),
                "changed corrupt DBM is rehashed and remains detectably corrupt");

            File.WriteAllText(cachePath, "{ malformed");
            DeploymentArtifactHashCache malformed = DeploymentArtifactHashCache.Load(cachePath);
            _ = malformed.GetValidatedHash(new FileInfo(dbm), SHA256.HashData(corrupt), 8);
            Check(malformed.CacheMisses == 1 && malformed.FilesHashed == 1,
                "malformed deployment hash cache falls back to full hashing");

            malformed.RetainOnly(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            malformed.Save();
            Check(DeploymentArtifactHashCache.Load(cachePath).EntryCount == 0,
                "stale deployment hash entries are pruned after validation");
            File.WriteAllText(cachePath, "{\"version\":1,\"entries\":[null]}");
            DeploymentArtifactHashCache nullEntry = DeploymentArtifactHashCache.Load(cachePath);
            Check(nullEntry.GetValidatedHash(new FileInfo(dbm), SHA256.HashData(corrupt), 8)
                .SequenceEqual(SHA256.HashData(corrupt)) && nullEntry.FilesHashed == 1,
                "null deployment-cache entries fall back to validation");
            nullEntry.Save();
            Check(DeploymentArtifactHashCache.Load(cachePath).EntryCount == 1,
                "deployment-cache null entries are repaired on save");

            File.Delete(dbm);
            Check(!File.Exists(dbm), "missing DBM remains observable before cache lookup");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
