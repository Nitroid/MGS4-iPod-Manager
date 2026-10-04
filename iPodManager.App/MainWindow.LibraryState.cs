using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Diagnostics;

namespace iPodManager;

public partial class MainWindow
{
    private GamePaths _paths = null!;
    private ApplicationPreferences _preferences = null!;
    private DeploymentManifest? _deploymentManifest;
    private IReadOnlyList<DeployedTrack> _deployedTracks = [];
    private string? _deploymentError;
    private bool _backgroundPlaybackEnabled;

    private sealed class ArtifactValidationStats
    {
        internal readonly HashSet<string> UniqueFiles = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Stopwatch ExistenceAndSize = new();
        internal readonly Stopwatch BankHashing = new();
        internal readonly ConcurrentDictionary<string, CalculatedArtifactHash> CalculatedHashes =
            new(StringComparer.OrdinalIgnoreCase);
        internal long DbmHashElapsedMilliseconds;
        internal int HashRequests;
        internal int HashCalculations;
        internal int HashReuses;
        internal int BankHashRequests;
        internal int BankHashCalculations;
        internal int DbmHashRequests;
        internal int DbmHashCalculations;
        internal int HealthyRecords;
        internal int MissingRecords;
        internal int CorruptRecords;
        internal int DbmHashConcurrency;
        internal DeploymentArtifactHashCache DbmCache = null!;
        internal readonly HashSet<string> HealthyDbmPaths = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CalculatedArtifactHash(
        byte[]? hash, Exception? error, int consumed, long length, long lastWriteUtcTicks)
    {
        internal byte[]? Hash { get; } = hash;
        internal Exception? Error { get; } = error;
        internal long Length { get; } = length;
        internal long LastWriteUtcTicks { get; } = lastWriteUtcTicks;
        internal int Consumed = consumed;
    }

    public string? DeploymentError => _deploymentError;

    private void InitializeApplicationState()
    {
        var stateTimer = Stopwatch.StartNew();
        _paths = GamePaths.Resolve(AppContext.BaseDirectory);
        AppLog.Info("Game root: " + _paths.Mgs4Root);
        AppLog.Info($"Library directories: Default={_paths.DefaultBanks}; Podcasts={_paths.PodcastContent}; Custom={_paths.CustomContent}.");
        var preferencesTimer = Stopwatch.StartNew();
        _preferences = ApplicationPreferences.Load(_paths.Preferences);
        AppLog.Info($"startup.preferences elapsed_ms={preferencesTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
        _qualityValue = _preferences.ConversionQuality;
        _soundEffectsDisabled = _preferences.UiSoundsDisabled;
        _backgroundPlaybackEnabled = _preferences.BackgroundPlaybackEnabled;
        AppLog.Info($"startup.application_state elapsed_ms={stateTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
    }

    private (DeploymentManifest? Manifest, IReadOnlyList<DeployedTrack> Tracks, string? Error) ReadDeploymentState(
        Action<int, int>? progress = null)
    {
        try
        {
            bool recoveryPending = Directory.Exists(_paths.DeploymentTransactionDirectory);
            var recoveryTimer = Stopwatch.StartNew();
            new DeploymentService(_paths).RecoverPendingTransaction();
            AppLog.Info($"startup.deployment.recovery pending={(recoveryPending ? 1 : 0)} elapsed_ms={recoveryTimer.ElapsedMilliseconds}");
            if (recoveryPending) AppLog.Info("Interrupted deployment recovery completed.");
            return LoadDeploymentState(progress);
        }
        catch (DeploymentException ex)
        {
            progress?.Invoke(0, 0);
            AppLog.Error("Interrupted deployment recovery failed.", ex);
            return (null, [], "An interrupted sync could not be recovered. Check iPodManager.log before syncing tracks.");
        }
    }

    private (DeploymentManifest? Manifest, IReadOnlyList<DeployedTrack> Tracks, string? Error) LoadDeploymentState(
        Action<int, int>? progress = null)
    {
        if (!File.Exists(_paths.DeploymentManifest))
        {
            progress?.Invoke(0, 0);
            return (null, [], null);
        }

        try
        {
            var totalTimer = Stopwatch.StartNew();
            var readTimer = Stopwatch.StartNew();
            byte[] manifestBytes = File.ReadAllBytes(_paths.DeploymentManifest);
            readTimer.Stop();
            var parseTimer = Stopwatch.StartNew();
            DeploymentManifest manifest = DeploymentManifestSerializer.Read(manifestBytes);
            parseTimer.Stop();
            var validation = new ArtifactValidationStats
            {
                DbmCache = DeploymentArtifactHashCache.Load(_paths.DeploymentArtifactHashCache)
            };
            int precalculatedDbms = PrecalculateDbmHashes(manifest, validation, progress);
            int validationUnits = precalculatedDbms + manifest.Records.Count;
            var tracks = new DeployedTrack[manifest.Records.Count];
            for (int index = 0; index < manifest.Records.Count; index++)
            {
                DeploymentManifestRecord record = manifest.Records[index];
                DeploymentHealth health = GetArtifactHealth(record, validation);
                if (health == DeploymentHealth.Healthy) validation.HealthyRecords++;
                else if (health == DeploymentHealth.ArtifactMissing) validation.MissingRecords++;
                else if (health == DeploymentHealth.ArtifactCorrupt) validation.CorruptRecords++;
                tracks[index] = new DeployedTrack(
                    record.Classification, record.SourceId, record.SourceSha256, record.BankSize,
                    record.SourcePath, record.Title, record.Artist, record.Album, health);
                progress?.Invoke(precalculatedDbms + index + 1, validationUnits);
            }
            validation.DbmCache.RetainOnly(validation.HealthyDbmPaths);
            validation.DbmCache.Save();
            AppLog.Info($"startup.deployment.validation records={manifest.Records.Count} healthy={validation.HealthyRecords} missing={validation.MissingRecords} corrupt={validation.CorruptRecords} unique_files={validation.UniqueFiles.Count} hash_requests={validation.HashRequests} hash_calculations={validation.HashCalculations} hash_reuses={validation.HashReuses} bank_hash_requests={validation.BankHashRequests} bank_hash_calculations={validation.BankHashCalculations} dbm_hash_requests={validation.DbmHashRequests} dbm_hash_calculations={validation.DbmHashCalculations} dbm_cache_hits={validation.DbmCache.CacheHits} dbm_cache_misses={validation.DbmCache.CacheMisses} dbm_files_hashed={validation.DbmCache.FilesHashed} dbm_hash_concurrency={validation.DbmHashConcurrency} manifest_read_ms={readTimer.ElapsedMilliseconds} manifest_parse_ms={parseTimer.ElapsedMilliseconds} exists_size_ms={validation.ExistenceAndSize.ElapsedMilliseconds} bank_hash_ms={validation.BankHashing.ElapsedMilliseconds} dbm_hash_ms={validation.DbmHashElapsedMilliseconds} hash_ms={validation.BankHashing.ElapsedMilliseconds + validation.DbmHashElapsedMilliseconds} elapsed_ms={totalTimer.ElapsedMilliseconds}");
            return (manifest, tracks, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or OverflowException)
        {
            progress?.Invoke(0, 0);
            AppLog.Error("Could not read the saved deployment state.", ex);
            return (null, [], "The saved deployment could not be read. Check iPodManager.log before syncing tracks.");
        }
    }

    private DeploymentHealth GetArtifactHealth(
        DeploymentManifestRecord record, ArtifactValidationStats validation)
    {
        string bankPath = Path.Combine(_paths.DefaultBanks, record.BankName);
        string dbmPath = Path.Combine(_paths.CommonDbm, Path.GetFileName(record.DbmPath));
        validation.ExistenceAndSize.Start();
        if (!string.Equals(Path.GetFileName(bankPath), record.BankName, StringComparison.Ordinal) ||
            !File.Exists(bankPath) || !File.Exists(dbmPath))
        {
            validation.ExistenceAndSize.Stop();
            return DeploymentHealth.ArtifactMissing;
        }
        validation.ExistenceAndSize.Stop();
        if (!MatchesArtifact(bankPath, record.BankSize, record.BankSha256, validation, bank: true) ||
            !MatchesArtifact(dbmPath, record.DbmSize, record.DbmSha256, validation, bank: false))
            return DeploymentHealth.ArtifactCorrupt;
        validation.HealthyDbmPaths.Add(Path.GetFullPath(dbmPath));
        return DeploymentHealth.Healthy;
    }

    private int PrecalculateDbmHashes(
        DeploymentManifest manifest, ArtifactValidationStats validation,
        Action<int, int>? progress)
    {
        var dbms = manifest.Records.Select(record => new
            {
                Path = Path.GetFullPath(Path.Combine(
                    _paths.CommonDbm, Path.GetFileName(record.DbmPath))),
                record.DbmSize,
                record.DbmSha256
            })
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(item => File.Exists(item.Path) &&
                (ulong)new FileInfo(item.Path).Length == item.DbmSize)
            .ToArray();
        validation.DbmHashConcurrency = Math.Min(4, Math.Max(1, Environment.ProcessorCount));
        int validationUnits = dbms.Length + manifest.Records.Count;
        int completed = 0;
        var timer = Stopwatch.StartNew();
        Parallel.ForEach(dbms, new ParallelOptions
        {
            MaxDegreeOfParallelism = validation.DbmHashConcurrency
        }, item =>
        {
            CalculatedArtifactHash result;
            var file = new FileInfo(item.Path);
            long length = file.Length;
            long lastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
            try
            {
                byte[] hash = validation.DbmCache.GetValidatedHash(
                    file, item.DbmSha256, manifest.Generation);
                result = new(hash, null, consumed: 0, length, lastWriteUtcTicks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = new(null, ex, consumed: 0, length, lastWriteUtcTicks);
            }
            validation.CalculatedHashes[item.Path] = result;
            progress?.Invoke(Interlocked.Increment(ref completed), validationUnits);
        });
        validation.HashCalculations += validation.DbmCache.FilesHashed;
        validation.DbmHashCalculations += validation.DbmCache.FilesHashed;
        validation.DbmHashElapsedMilliseconds = timer.ElapsedMilliseconds;
        return dbms.Length;
    }

    private static bool MatchesArtifact(
        string path, ulong size, byte[] hash, ArtifactValidationStats validation, bool bank)
    {
        string canonicalPath = Path.GetFullPath(path);
        validation.UniqueFiles.Add(canonicalPath);
        validation.ExistenceAndSize.Start();
        var file = new FileInfo(canonicalPath);
        long currentLength = file.Length;
        long currentLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
        bool sizeMatches = (ulong)currentLength == size;
        validation.ExistenceAndSize.Stop();
        if (!sizeMatches) return false;
        validation.HashRequests++;
        if (bank) validation.BankHashRequests++;
        else validation.DbmHashRequests++;
        if (validation.CalculatedHashes.TryGetValue(
            canonicalPath, out CalculatedArtifactHash? calculated) &&
            calculated.Length == currentLength &&
            calculated.LastWriteUtcTicks == currentLastWriteUtcTicks)
        {
            if (Interlocked.Exchange(ref calculated.Consumed, 1) != 0)
                validation.HashReuses++;
            if (calculated.Error != null)
                ExceptionDispatchInfo.Capture(calculated.Error).Throw();
            return calculated.Hash!.SequenceEqual(hash);
        }
        validation.HashCalculations++;
        if (bank) validation.BankHashCalculations++;
        else validation.DbmHashCalculations++;
        if (bank) validation.BankHashing.Start();
        var dbmTimer = bank ? null : Stopwatch.StartNew();
        using FileStream stream = File.OpenRead(canonicalPath);
        byte[] calculatedHash = SHA256.HashData(stream);
        if (bank) validation.BankHashing.Stop();
        else validation.DbmHashElapsedMilliseconds += dbmTimer!.ElapsedMilliseconds;
        validation.CalculatedHashes[canonicalPath] =
            new(calculatedHash, null, consumed: 1, currentLength, currentLastWriteUtcTicks);
        return calculatedHash.SequenceEqual(hash);
    }

    private void SavePreferences()
    {
        _preferences.ConversionQuality = _qualityValue;
        _preferences.UiSoundsDisabled = _soundEffectsDisabled;
        _preferences.BackgroundPlaybackEnabled = _backgroundPlaybackEnabled;
        try { _preferences.Save(_paths.Preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not save preferences.", ex);
            ShowError("Could not save settings. Check that the game folder is writable.");
        }
    }
}
