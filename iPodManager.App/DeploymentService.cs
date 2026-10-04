using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace iPodManager;

internal enum DeploymentFailureState { BeforeMutation, PreviousRestored, RecoveryRequired }
internal sealed class DeploymentException(string message, Exception? inner = null,
    DeploymentFailureState state = DeploymentFailureState.BeforeMutation,
    string? userMessage = null) : Exception(message, inner)
{
    public DeploymentFailureState FailureState { get; } = state;
    public string? UserMessage { get; } = userMessage;
}
internal sealed class SimulatedProcessTerminationException : Exception;

internal sealed class DeploymentService
{
    private readonly GamePaths _paths;
    private readonly Action<string>? _activationProbe;
    private readonly Action<DeploymentWorkStage, int, int>? _progress;
    private string? _resolvedGameRoot;
    private const string JournalName = "deployment-transaction.json";
    private sealed record TransactionEntry(string RelativePath, bool PreviousExisted,
        ulong PreviousSize, string? PreviousSha256, ulong NewSize, string? NewSha256);
    private sealed record DeploymentTransaction(int Version, string Phase, ulong PreviousGeneration,
        ulong NewGeneration, List<Guid> PreviousSourceIds, List<Guid> NewSourceIds,
        List<string> PreviousPaths, List<string> NewPaths, List<TransactionEntry> Entries);

    public DeploymentService(GamePaths paths) : this(paths, null, null) { }
    public DeploymentService(GamePaths paths, Action<string>? activationProbe) : this(paths, activationProbe, null) { }

    internal DeploymentService(GamePaths paths, Action<string>? activationProbe, Action<DeploymentWorkStage, int, int>? progress)
    {
        _paths = paths;
        _activationProbe = activationProbe;
        _progress = progress;
    }

    public async Task<DeploymentResult> BuildAndDeployAsync(IReadOnlyList<DeploymentTrackIntent> intents, byte[] stockEnabled, int quality, bool dryRun = false, CancellationToken token = default)
    {
        if (stockEnabled.Length != DeploymentContract.StockCount || stockEnabled.Any(x => x > 1))
            throw new DeploymentException("Stock selection must contain 73 zero/one values.");
        var enabled = intents.Where(intent => intent.Enabled).ToArray();
        if (enabled.Any(intent => intent.Category is not (LibraryCategory.Custom or LibraryCategory.Podcast)))
            throw new DeploymentException("Deployment intents may contain only Custom and Podcast tracks.");
        if (!DeploymentContract.SupportsNonDefaultTrackCount(enabled.Length))
        {
            int excess = enabled.Length - DeploymentContract.MaxNonDefaultTracks;
            throw new DeploymentException(
                $"The deployment contains {enabled.Length} non-default tracks; the supported maximum is {DeploymentContract.MaxNonDefaultTracks}.",
                userMessage: "Too many custom tracks are selected.\n\n" +
                    $"Snake's iPod supports up to {DeploymentContract.MaxNonDefaultTracks} Custom and Podcast tracks at once. " +
                    $"Please deselect {excess} {(excess == 1 ? "track" : "tracks")} and try again.");
        }

        _progress?.Invoke(DeploymentWorkStage.Validating, 0, intents.Count);
        if (enabled.Any(intent => intent.StableId == Guid.Empty) ||
            enabled.Select(intent => intent.StableId).Distinct().Count() != enabled.Length)
            throw new DeploymentException("Track stable identities are empty or duplicated.");

        for (int i = 0; i < enabled.Length; i++)
        {
            DeploymentTrackIntent intent = enabled[i];
            string relative = DeploymentContract.NormalizeRelative(intent.RelativeSourcePath);
            try { DeploymentManifestSerializer.ValidateSourcePath(relative); }
            catch (InvalidDataException ex)
            {
                throw new DeploymentException(ex.Message, ex, userMessage:
                    "A selected track's source path is too long. Shorten its folder or file name and try again. Check iPodManager.log for details.");
            }
            try
            {
                ArtifactPackager.DbmMetadata metadata = ArtifactPackager.PrepareDbmMetadata(
                    intent.Title, intent.Artist, intent.Album);
                LogMetadataTruncation(intent, "title", intent.Title, metadata.Title,
                    ArtifactPackager.TitlePayloadByteLimit);
                LogMetadataTruncation(intent, "artist", intent.Artist, metadata.Artist,
                    ArtifactPackager.ArtistPayloadByteLimit);
                LogMetadataTruncation(intent, "album", intent.Album, metadata.Album,
                    ArtifactPackager.AlbumPayloadByteLimit);
                enabled[i] = intent with
                {
                    Title = metadata.Title,
                    Artist = metadata.Artist,
                    Album = metadata.Album,
                    RelativeSourcePath = relative
                };
            }
            catch (DbmMetadataException ex)
            {
                throw new DeploymentException($"Track '{intent.Title}' has invalid DBM metadata: {ex.Message}",
                    ex, userMessage:
                        $"A selected track cannot be synced because its {ex.Field} contains an invalid character.");
            }
        }

        try { _ = DeploymentManifestSerializer.GetDirectStreamSize(enabled); }
        catch (InvalidDataException ex)
        {
            throw new DeploymentException(ex.Message, ex, userMessage:
                "The selected tracks need too much deployment data. Select fewer tracks or shorten source paths and try again. Check iPodManager.log for details.");
        }

        using FileStream transferLock = OpenTransferLock();
        RecoverPendingTransactionCore();
        DeploymentManifest? previousManifest = LoadManifest();
        var previousById = previousManifest?.Records.ToDictionary(record => record.SourceId) ?? [];
        Dictionary<Guid, uint> controlIds = ControlIdAllocator.Allocate(enabled.Select(intent =>
            (intent.StableId, previousById.TryGetValue(intent.StableId, out var prior) ? (uint?)prior.ControlId : null)));
        var identities = enabled.Select(intent => new DeploymentIdentity(
            intent.StableId,
            controlIds[intent.StableId],
            ArtifactNaming.RuntimeId(intent.StableId))).ToArray();
        if (identities.Select(identity => identity.RuntimeId).Distinct(StringComparer.Ordinal).Count() != identities.Length)
            throw new DeploymentException("Runtime identity collision.");

        string stagingDirectory = Path.Combine(_paths.IpodRoot, ".deployment-staging", Guid.NewGuid().ToString("N"));
        CheckDeploymentPath(stagingDirectory, "create staging");
        Directory.CreateDirectory(stagingDirectory);
        var preparedTracks = new List<BuiltTrack>();
        var planItems = new List<DeploymentPlanItem>();
        try
        {
            string dbmTemplate = ResolveDbmTemplate();
            string programmerBank = Path.Combine(AppContext.BaseDirectory, "tools", DeploymentContract.ProgrammerBankName);
            if (!File.Exists(programmerBank))
                throw new DeploymentException("The shared direct-stream programmer BANK is missing.");
            string stagedBank = Path.Combine(stagingDirectory, DeploymentContract.ProgrammerBankName);
            CheckDeploymentPath(stagedBank, "stage BANK");
            File.Copy(programmerBank, stagedBank);
            byte[] bankHash = SHA256.HashData(File.ReadAllBytes(stagedBank));
            ulong bankSize = (ulong)new FileInfo(stagedBank).Length;

            for (int i = 0; i < enabled.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var intent = enabled[i];
                var identity = identities[i];
                _progress?.Invoke(DeploymentWorkStage.Converting, i, enabled.Length);
                SourceAudioArtifact audio = await ReadSourceAudioAsync(intent.SourcePath, token);
                _progress?.Invoke(DeploymentWorkStage.Building, i, enabled.Length);

                string runtimeId = identity.RuntimeId;
                byte[] dbm = ArtifactPackager.BuildDbm(
                    dbmTemplate, intent.Title, intent.Artist, intent.Album, audio.DurationSeconds, identity.ControlId);
                string dbmPath = Path.Combine(stagingDirectory, ArtifactNaming.DbmName(runtimeId));
                CheckDeploymentPath(dbmPath, "stage DBM");
                await File.WriteAllBytesAsync(dbmPath, dbm, token);
                byte[] dbmHash = SHA256.HashData(dbm);

                preparedTracks.Add(new(
                    intent, identity, audio, dbmPath, dbmHash, (ulong)dbm.Length,
                    stagedBank, bankHash, bankSize, DeploymentContract.ProgrammerEventGuid,
                    ArtifactNaming.DbmRequest(runtimeId), ArtifactNaming.EventPath(runtimeId)));
                bool existed = previousById.ContainsKey(intent.StableId);
                planItems.Add(new(intent.StableId, intent.Title,
                    existed ? DeploymentPlanAction.Update : DeploymentPlanAction.Add,
                    identity.ControlId));
            }

            foreach (var removed in previousManifest?.Records.Where(record => enabled.All(intent => intent.StableId != record.SourceId)) ?? [])
                planItems.Add(new(removed.SourceId, removed.RuntimeId, DeploymentPlanAction.Remove, removed.ControlId));

            ulong generation = checked((previousManifest?.Generation ?? 0) + 1);
            var records = preparedTracks.Select(track => new DeploymentManifestRecord(
                track.Intent.Category == LibraryCategory.Podcast ? TrackClassification.Podcast : TrackClassification.Music,
                track.Audio.DurationSeconds, track.Intent.StableId, track.Audio.SourceSha256,
                track.BankSha256, track.BankSize, DeploymentContract.DirectStreamProfile, 1,
                track.Identity.RuntimeId, track.Intent.RelativeSourcePath,
                DeploymentContract.ProgrammerBankName, track.Intent.Title, track.Intent.Artist,
                track.Intent.Album, track.Identity.ControlId, track.DbmSize, track.EventGuid,
                track.DbmSha256, ArtifactNaming.BuilderVersion,
                track.DbmRequestPath,
                track.DbmRequestPath, track.EventPath, DeploymentContract.ProgrammerInstrumentName)).ToArray();
            var manifest = new DeploymentManifest(generation, stockEnabled, records);
            byte[] manifestBytes = DeploymentManifestSerializer.Write(manifest);
            _ = DeploymentManifestSerializer.Read(manifestBytes);
            string stagedManifest = Path.Combine(stagingDirectory, "deployment.bin");
            CheckDeploymentPath(stagedManifest, "stage manifest");
            await File.WriteAllBytesAsync(stagedManifest, manifestBytes, token);
            ValidateStagedArtifacts(preparedTracks, manifest);

            var plan = new DeploymentPlan(planItems, records.Length, stagingDirectory);
            if (dryRun)
                return new(plan, stagedManifest, generation, preparedTracks);

            _progress?.Invoke(DeploymentWorkStage.Installing, enabled.Length, enabled.Length);
            ActivateDeployment(stagingDirectory, preparedTracks, manifestBytes, previousManifest, generation);
            return new(plan, _paths.DeploymentManifest, generation, preparedTracks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DeploymentException and not SimulatedProcessTerminationException)
        {
            throw new DeploymentException("Deployment failed: " + ex.Message, ex);
        }
        finally
        {
            if (!dryRun)
            {
                try
                {
                    if (Directory.Exists(stagingDirectory)) DeleteDeploymentDirectory(stagingDirectory);
                }
                catch (Exception ex) { AppLog.Warn($"Deployment staging cleanup blocked or failed: {ex}"); }
            }
        }
    }

    private static void LogMetadataTruncation(
        DeploymentTrackIntent intent, string field, string original, string prepared, int byteLimit)
    {
        if (prepared == original)
            return;
        AppLog.Warn($"MGS4 metadata shortened track='{intent.RelativeSourcePath}' field={field} " +
            $"original_bytes={Encoding.UTF8.GetByteCount(original)} limit={byteLimit}.");
    }

    private static async Task<SourceAudioArtifact> ReadSourceAudioAsync(string path, CancellationToken token)
    {
        FileInfo before = new(path);
        if (!before.Exists || before.Length == 0)
            throw new InvalidDataException("Source audio is missing or empty: " + path);
        long originalLength = before.Length;
        DateTime originalWriteTime = before.LastWriteTimeUtc;
        byte[] hash;
        await using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = await SHA256.HashDataAsync(stream, token);
        ulong seconds;
        using (TagLib.File media = TagLib.File.Create(path))
            seconds = checked((ulong)Math.Max(1, Math.Floor(media.Properties.Duration.TotalSeconds)));
        before.Refresh();
        if (!before.Exists || before.Length != originalLength || before.LastWriteTimeUtc != originalWriteTime)
            throw new InvalidDataException("Source audio changed while it was being prepared: " + path);
        if (seconds > int.MaxValue / 1000UL)
            throw new InvalidDataException("Source audio duration exceeds MGS4's supported range: " + path);
        return new(hash, seconds);
    }

    private static void ValidateStagedArtifacts(IReadOnlyList<BuiltTrack> preparedTracks, DeploymentManifest manifest)
    {
        DeploymentManifestSerializer.Validate(manifest);
        foreach (var track in preparedTracks)
        {
            byte[] dbm = File.ReadAllBytes(track.DbmPath);
            byte[] bank = File.ReadAllBytes(track.BankPath);
            ArtifactPackager.ValidateDbm(dbm, track.Identity.ControlId, track.Audio.DurationSeconds);
            if (!SHA256.HashData(dbm).SequenceEqual(track.DbmSha256) ||
                !SHA256.HashData(bank).SequenceEqual(track.BankSha256))
                throw new InvalidDataException("Staged artifact hash mismatch.");
        }
    }

    private void ActivateDeployment(string stagingDirectory, IReadOnlyList<BuiltTrack> preparedTracks,
        byte[] manifest, DeploymentManifest? previousManifest, ulong generation)
    {
        ManagedDeploymentInventory previousInventory = LoadAndValidateInventory(previousManifest);
        var artifactTargets = new List<(string Source, string Relative)>();
        foreach (var track in preparedTracks)
        {
            artifactTargets.Add((track.DbmPath, track.DbmRequestPath));
        }
        if (preparedTracks.Count != 0)
            artifactTargets.Add((preparedTracks[0].BankPath, "common/bank/default/" + DeploymentContract.ProgrammerBankName));
        var inventoriedTargets = new List<(string Source, string Relative)>(artifactTargets)
        {
            (Path.Combine(stagingDirectory, "deployment.bin"), "iPod/deployment.bin")
        };

        var nextInventory = new ManagedDeploymentInventory
        {
            Version = 1,
            Generation = generation,
            Files = inventoriedTargets.Select(target =>
            {
                var file = new FileInfo(target.Source);
                return new ManagedDeploymentFile(target.Relative,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target.Source))), (ulong)file.Length);
            }).ToList()
        };
        string stagedInventory = Path.Combine(stagingDirectory, "deployment-managed-files.json");
        string stagedInventoryTemporary = stagedInventory + ".tmp";
        CheckDeploymentPath(stagedInventoryTemporary, "stage inventory");
        File.WriteAllBytes(stagedInventoryTemporary, JsonSerializer.SerializeToUtf8Bytes(nextInventory,
            new JsonSerializerOptions { WriteIndented = true }));
        CheckDeploymentPath(stagedInventoryTemporary, "move staged inventory");
        CheckDeploymentPath(stagedInventory, "move staged inventory");
        File.Move(stagedInventoryTemporary, stagedInventory, true);

        var targets = new List<(string Source, string Relative)>(inventoriedTargets)
        {
            (stagedInventory, "iPod/deployment-managed-files.json")
        };
        PreflightOwnership(previousInventory, targets);
        string[] stale = previousInventory.Files
            .Where(file => nextInventory.Files.All(next => next.RelativePath != file.RelativePath))
            .Select(file => file.RelativePath).ToArray();
            DeploymentTransaction transaction = PrepareTransaction(previousManifest, previousInventory,
                targets, stale, preparedTracks.Select(track => track.Intent.StableId).ToList(),
                nextInventory.Files.Select(file => file.RelativePath).Append("iPod/deployment-managed-files.json").ToList(), generation);
            _progress?.Invoke(DeploymentWorkStage.TransactionPrepared, preparedTracks.Count, preparedTracks.Count);
            bool committed = false;
            try
            {
            _activationProbe?.Invoke("journal-created");
            foreach (var item in artifactTargets)
            {
                Publish(item);
            }
            foreach (string relative in stale)
            {
                _activationProbe?.Invoke("before-remove:" + relative);
                TransactionEntry entry = transaction.Entries.Single(item => item.RelativePath == relative);
                VerifyCurrentVersion(entry);
                string path = ResolveManagedPath(relative, "remove stale artifact");
                    if (File.Exists(path)) File.Delete(path);
                    _activationProbe?.Invoke("removed:" + relative);
                }
                _progress?.Invoke(DeploymentWorkStage.FilesApplied, preparedTracks.Count, preparedTracks.Count);
                Publish(targets[^2]); // deployment.bin becomes visible after artifacts and stale removal.
                Publish(targets[^1]); // The inventory is the final metadata publication.
                _progress?.Invoke(DeploymentWorkStage.StatePublished, preparedTracks.Count, preparedTracks.Count);
                if (!IsNewDeploymentComplete(transaction))
                    throw new InvalidDataException("Published deployment did not verify as a complete generation.");
                committed = true;
                transaction = transaction with { Phase = "Committed" };
                WriteJournal(transaction);
                _activationProbe?.Invoke("commit");
                _progress?.Invoke(DeploymentWorkStage.DeploymentCommitted, preparedTracks.Count, preparedTracks.Count);
                CleanupTransaction(committed: true);
                _progress?.Invoke(DeploymentWorkStage.CleanupComplete, preparedTracks.Count, preparedTracks.Count);
        }
        catch (SimulatedProcessTerminationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (committed)
                throw new DeploymentException("New deployment verified, but transaction cleanup failed. Recovery is required.",
                    ex, DeploymentFailureState.RecoveryRequired);
            try
            {
                RestorePrevious(transaction);
                CleanupTransaction(committed: false);
                throw new DeploymentException("Transfer failed; the pre-transfer file state was verified restored.",
                    ex, DeploymentFailureState.PreviousRestored);
            }
            catch (DeploymentException) { throw; }
            catch (Exception recoveryError)
            {
                throw new DeploymentException("Transfer and recovery failed; manual attention is required: " +
                    recoveryError.Message, new AggregateException(ex, recoveryError), DeploymentFailureState.RecoveryRequired);
            }
        }

        void Publish((string Source, string Relative) item)
        {
            _activationProbe?.Invoke("before-publish:" + item.Relative);
            TransactionEntry entry = transaction.Entries.Single(record => record.RelativePath == item.Relative);
            VerifyCurrentVersion(entry);
            string target = ResolveManagedPath(item.Relative);
            CheckDeploymentPath(Path.GetDirectoryName(target)!, "create target directory");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string publishDirectory = Path.Combine(_paths.DeploymentTransactionDirectory, "publish");
            CheckDeploymentPath(publishDirectory, "create publish directory");
            Directory.CreateDirectory(publishDirectory);
            if ((File.GetAttributes(publishDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Transaction publish directory is redirected.");
            string temporary = Path.Combine(publishDirectory,
                transaction.Entries.IndexOf(entry).ToString("D4") + ".tmp");
            CopyDurably(item.Source, temporary);
            CheckDeploymentPath(temporary, "publish source");
            CheckDeploymentPath(target, "publish target");
            File.Move(temporary, target, true);
            _activationProbe?.Invoke("published:" + item.Relative);
        }
    }
    private DeploymentManifest? LoadManifest()
    {
        string path = ResolveManagedPath("iPod/deployment.bin");
        return File.Exists(path) ? DeploymentManifestSerializer.Read(File.ReadAllBytes(path)) : null;
    }

    private string JournalPath => CheckDeploymentPath(
        Path.Combine(_paths.DeploymentTransactionDirectory, JournalName), "transaction journal");
    private string BackupPath(int index) => CheckDeploymentPath(Path.Combine(_paths.DeploymentTransactionDirectory,
        "backups", index.ToString("D4") + ".bak"), "transaction backup");

    private DeploymentTransaction PrepareTransaction(DeploymentManifest? previousManifest,
        ManagedDeploymentInventory previousInventory, IReadOnlyList<(string Source, string Relative)> targets,
        IReadOnlyList<string> stale, List<Guid> newSourceIds, List<string> newPaths, ulong generation)
    {
        string directory = _paths.DeploymentTransactionDirectory;
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new InvalidDataException("An unfinished deployment transaction already exists.");
        CheckDeploymentPath(Path.Combine(directory, "backups"), "create transaction backups");
        Directory.CreateDirectory(Path.Combine(directory, "backups"));
        var entries = new List<TransactionEntry>();
        try
        {
            foreach (var item in targets)
                entries.Add(CreateEntry(item.Relative, item.Source));
            foreach (string relative in stale)
                entries.Add(CreateEntry(relative, null));

            for (int i = 0; i < entries.Count; i++)
            {
                TransactionEntry entry = entries[i];
                if (!entry.PreviousExisted) continue;
                CopyDurably(ResolveManagedPath(entry.RelativePath), BackupPath(i));
                if (!MatchesFile(BackupPath(i), entry.PreviousSize, entry.PreviousSha256!))
                    throw new IOException("Transaction backup did not verify: " + entry.RelativePath);
            }

            var previousPaths = previousInventory.Files.Select(file => file.RelativePath)
                .Append("iPod/deployment-managed-files.json").ToList();
            var transaction = new DeploymentTransaction(2, "Publishing", previousManifest?.Generation ?? 0,
                generation, previousManifest?.Records.Select(record => record.SourceId).ToList() ?? [],
                newSourceIds, previousPaths, newPaths, entries);
            WriteJournal(transaction);
            _ = ReadAndValidateTransaction();
            return transaction;
        }
        catch
        {
            // No live file is changed before a complete journal exists.
            if (!File.Exists(JournalPath))
            {
                try { DeleteDeploymentDirectory(directory); }
                catch (Exception ex) { AppLog.Warn($"Unpublished transaction cleanup blocked or failed: {ex}"); }
            }
            throw;
        }

        TransactionEntry CreateEntry(string relative, string? source)
        {
            string path = ResolveManagedPath(relative);
        bool existed = File.Exists(path);
        ulong priorSize = existed ? (ulong)new FileInfo(path).Length : 0;
        string? priorHash = existed ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
        if (existed && relative != "iPod/deployment-managed-files.json" &&
            !previousInventory.Files.Any(file => file.RelativePath == relative &&
                file.Size == priorSize && file.Sha256 == priorHash))
            throw new InvalidDataException("Unowned or externally modified target: " + relative);
            ulong nextSize = source == null ? 0 : (ulong)new FileInfo(source).Length;
            string? nextHash = source == null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            return new(relative, existed, priorSize, priorHash, nextSize, nextHash);
        }
    }

    private DeploymentTransaction ReadAndValidateTransaction()
    {
        string directory = _paths.DeploymentTransactionDirectory;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(JournalPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Deployment transaction contains a redirected path.");
        DeploymentTransaction transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<DeploymentTransaction>(File.ReadAllBytes(JournalPath))
                ?? throw new InvalidDataException("Empty deployment transaction journal.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed deployment transaction journal.", ex);
        }
        if (transaction.Phase == "Publishing")
        {
            string backupsDirectory = Path.Combine(directory, "backups");
            CheckDeploymentPath(backupsDirectory, "read transaction backups");
            if (!Directory.Exists(backupsDirectory) ||
                (File.GetAttributes(backupsDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Transaction backup directory is missing or redirected.");
        }
        ValidateTransaction(transaction);
        return transaction;
    }

    private void ValidateTransaction(DeploymentTransaction transaction)
    {
        if (transaction.Version != 2 || transaction.Phase is not ("Publishing" or "Committed") ||
            transaction.PreviousGeneration == ulong.MaxValue ||
            transaction.NewGeneration != transaction.PreviousGeneration + 1 ||
            transaction.PreviousSourceIds == null || transaction.NewSourceIds == null ||
            transaction.PreviousPaths == null || transaction.NewPaths == null || transaction.Entries == null ||
            transaction.PreviousSourceIds.Count > DeploymentContract.MaxNonDefaultTracks ||
            transaction.PreviousSourceIds.Any(id => id == Guid.Empty) ||
            transaction.PreviousSourceIds.Distinct().Count() != transaction.PreviousSourceIds.Count ||
            transaction.PreviousSourceIds.Select(ArtifactNaming.RuntimeId).Distinct().Count() != transaction.PreviousSourceIds.Count ||
            (transaction.PreviousGeneration == 0 && transaction.PreviousSourceIds.Count != 0) ||
            transaction.NewSourceIds.Count > DeploymentContract.MaxNonDefaultTracks ||
            transaction.NewSourceIds.Any(id => id == Guid.Empty) ||
            transaction.NewSourceIds.Distinct().Count() != transaction.NewSourceIds.Count ||
            transaction.NewSourceIds.Select(ArtifactNaming.RuntimeId).Distinct().Count() != transaction.NewSourceIds.Count)
            throw new InvalidDataException("Invalid deployment transaction header.");

        var byPath = new Dictionary<string, TransactionEntry>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < transaction.Entries.Count; i++)
        {
            TransactionEntry entry = transaction.Entries[i];
            if (entry == null || entry.RelativePath == null ||
                !byPath.TryAdd(entry.RelativePath, entry) ||
                !ValidHash(entry.PreviousSha256, entry.PreviousExisted) ||
                !ValidHash(entry.NewSha256, entry.NewSha256 != null) ||
                (entry.PreviousExisted && entry.PreviousSize == 0) ||
                (!entry.PreviousExisted && entry.PreviousSize != 0) ||
                (entry.NewSha256 == null && entry.NewSize != 0) ||
                (entry.NewSha256 != null && entry.NewSize == 0))
                throw new InvalidDataException("Invalid deployment transaction entry.");
            ResolveManagedPath(entry.RelativePath);
            string backup = BackupPath(i);
            if (transaction.Phase == "Publishing" && entry.PreviousExisted &&
                (!File.Exists(backup) || (File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0 ||
                 !MatchesFile(backup, entry.PreviousSize, entry.PreviousSha256!)))
                throw new InvalidDataException("Missing or corrupt transaction backup: " + entry.RelativePath);
            if (transaction.Phase == "Publishing" && !entry.PreviousExisted && File.Exists(backup))
                throw new InvalidDataException("Unexpected transaction backup: " + entry.RelativePath);
        }

        if (!byPath.TryGetValue("iPod/deployment.bin", out TransactionEntry? oldManifestEntry) ||
            !byPath.TryGetValue("iPod/deployment-managed-files.json", out TransactionEntry? oldInventoryEntry) ||
            oldManifestEntry.PreviousExisted != oldInventoryEntry.PreviousExisted)
            throw new InvalidDataException("Transaction metadata entries are incomplete.");
        if (oldManifestEntry.PreviousExisted != (transaction.PreviousGeneration != 0))
            throw new InvalidDataException("Transaction previous generation is inconsistent.");

        var previousPaths = transaction.PreviousPaths.ToHashSet(StringComparer.Ordinal);
        var newPaths = transaction.NewPaths.ToHashSet(StringComparer.Ordinal);
        if (previousPaths.Count != transaction.PreviousPaths.Count || newPaths.Count != transaction.NewPaths.Count ||
            !previousPaths.Contains("iPod/deployment-managed-files.json") ||
            !newPaths.Contains("iPod/deployment.bin") || !newPaths.Contains("iPod/deployment-managed-files.json"))
            throw new InvalidDataException("Invalid deployment transaction path inventory.");
        var allPaths = new HashSet<string>(previousPaths, StringComparer.Ordinal);
        allPaths.UnionWith(newPaths);
        if (byPath.Count != allPaths.Count || allPaths.Any(path => !byPath.ContainsKey(path)) ||
            transaction.Entries.Any(entry => newPaths.Contains(entry.RelativePath) != (entry.NewSha256 != null) ||
                (!previousPaths.Contains(entry.RelativePath) && entry.PreviousExisted)))
            throw new InvalidDataException("Transaction claims a path outside its old or intended deployment.");

        if (transaction.Phase == "Committed")
        {
            return;
        }

        DeploymentManifest? oldManifest = null;
        ManagedDeploymentInventory? oldInventory = null;
        if (oldManifestEntry.PreviousExisted)
        {
            int manifestIndex = transaction.Entries.IndexOf(oldManifestEntry);
            int inventoryIndex = transaction.Entries.IndexOf(oldInventoryEntry);
            oldManifest = DeploymentManifestSerializer.Read(File.ReadAllBytes(BackupPath(manifestIndex)));
            try
            {
                oldInventory = JsonSerializer.Deserialize<ManagedDeploymentInventory>(File.ReadAllBytes(BackupPath(inventoryIndex)))
                    ?? throw new InvalidDataException("Invalid previous inventory backup.");
            }
            catch (JsonException ex) { throw new InvalidDataException("Invalid previous inventory backup.", ex); }
            ValidateInventoryAgainstManifest(oldInventory, oldManifest);
            if (oldManifest.Generation != transaction.PreviousGeneration)
                throw new InvalidDataException("Transaction previous generation does not match its backup.");
            if (!oldManifest.Records.Select(record => record.SourceId).ToHashSet().SetEquals(transaction.PreviousSourceIds))
                throw new InvalidDataException("Transaction previous identities do not match its backup.");
        }
        var oldPaths = oldInventory?.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (TransactionEntry entry in transaction.Entries)
        {
            bool isNew = newPaths.Contains(entry.RelativePath);
            if (isNew != (entry.NewSha256 != null) ||
                (entry.PreviousExisted && entry.RelativePath != "iPod/deployment-managed-files.json" &&
                 (!oldPaths.Contains(entry.RelativePath) ||
                  oldInventory!.Files.Single(file => file.RelativePath == entry.RelativePath).Sha256 != entry.PreviousSha256 ||
                  oldInventory.Files.Single(file => file.RelativePath == entry.RelativePath).Size != entry.PreviousSize)))
                throw new InvalidDataException("Transaction entry does not match deployment ownership: " + entry.RelativePath);
        }
    }

    private static bool ValidHash(string? value, bool required) =>
        required ? value is { Length: 64 } && value.All(char.IsAsciiHexDigitUpper) : value == null;

    private bool MatchesFile(string path, ulong size, string hash)
    {
        CheckDeploymentPath(path, "verify owned file");
        if (!File.Exists(path)) return false;
        using FileStream stream = File.OpenRead(path);
        return (ulong)stream.Length == size && Convert.ToHexString(SHA256.HashData(stream)) == hash;
    }

    private void CopyDurably(string source, string destination)
    {
        CheckDeploymentPath(source, "copy source");
        using FileStream input = File.OpenRead(source);
        CheckDeploymentPath(destination, "copy destination");
        using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private void WriteJournal(DeploymentTransaction transaction)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(transaction);
        string temporary = JournalPath + ".tmp";
        CheckDeploymentPath(temporary, "write journal");
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(json);
            stream.Flush(flushToDisk: true);
        }
        CheckDeploymentPath(temporary, "publish journal source");
        File.Move(temporary, JournalPath, true);
    }

    internal void RecoverPendingTransaction()
    {
        using FileStream transferLock = OpenTransferLock();
        RecoverPendingTransactionCore();
    }

    private FileStream OpenTransferLock()
    {
        try
        {
            return new FileStream(CheckDeploymentPath(Path.Combine(_paths.IpodRoot, ".deployment-transfer.lock"), "acquire deployment lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            throw new DeploymentException("Another transfer or recovery operation is already running.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new DeploymentException("Could not acquire the deployment lock: " + ex.Message, ex,
                userMessage: ex is InvalidDataException ?
                    "The deployment folder is redirected. Remove redirected folders and try again. Check iPodManager.log for details." : null);
        }
    }

    private void RecoverPendingTransactionCore()
    {
        string directory = _paths.DeploymentTransactionDirectory;
        try
        {
            CheckDeploymentPath(directory, "recover transaction");
            if (!Directory.Exists(directory)) return;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Deployment transaction directory is redirected.");
            if (!File.Exists(JournalPath))
            {
                // No live mutation starts before the journal exists; cleanup also removes it last.
                string backups = Path.Combine(directory, "backups");
                CheckDeploymentPath(backups, "recover journal-less backups");
                CheckDeploymentPath(JournalPath + ".tmp", "recover journal temporary");
                if (Directory.EnumerateFileSystemEntries(directory).Any(path =>
                    path != backups && path != JournalPath + ".tmp") ||
                    (Directory.Exists(backups) && ((File.GetAttributes(backups) & FileAttributes.ReparsePoint) != 0 ||
                     Directory.EnumerateFileSystemEntries(backups).Any(path =>
                         !Path.GetFileName(path).EndsWith(".bak", StringComparison.Ordinal) ||
                         !int.TryParse(Path.GetFileNameWithoutExtension(path), out _)))))
                    throw new InvalidDataException("Unrecognized files remain in a journal-less transaction directory.");
                DeleteDeploymentDirectory(directory);
                return;
            }
            DeploymentTransaction transaction = ReadAndValidateTransaction();
            if (transaction.Phase == "Committed")
            {
                if (!IsNewDeploymentComplete(transaction))
                    throw new InvalidDataException("Committed transaction no longer matches installed files.");
                CleanupTransaction(committed: true);
            }
            else if (IsNewDeploymentComplete(transaction))
            {
                transaction = transaction with { Phase = "Committed" };
                WriteJournal(transaction);
                CleanupTransaction(committed: true);
            }
            else
            {
                RestorePrevious(transaction);
                CleanupTransaction(committed: false);
            }
        }
        catch (Exception ex) when (ex is not DeploymentException)
        {
            throw new DeploymentException("Deployment recovery requires manual attention: " + ex.Message,
                ex, DeploymentFailureState.RecoveryRequired);
        }
    }

    private bool IsNewDeploymentComplete(DeploymentTransaction transaction)
    {
        try
        {
            DeploymentManifest? manifest = LoadManifest();
            if (manifest == null || manifest.Generation != transaction.NewGeneration ||
                !manifest.Records.Select(record => record.SourceId).ToHashSet().SetEquals(transaction.NewSourceIds))
                return false;
            _ = LoadAndValidateInventory(manifest);
            foreach (TransactionEntry entry in transaction.Entries)
            {
                string path = ResolveManagedPath(entry.RelativePath);
                if (entry.NewSha256 == null)
                {
                    if (File.Exists(path) || Directory.Exists(path)) return false;
                }
                else if (!MatchesFile(path, entry.NewSize, entry.NewSha256)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or OverflowException or JsonException)
        {
            return false;
        }
    }

    private void RestorePrevious(DeploymentTransaction transaction)
    {
        // Validate every backup before touching a live file. A corrupt journal is evidence, not a repair instruction.
        ValidateTransaction(transaction);
        var failures = new List<string>();
        for (int i = transaction.Entries.Count - 1; i >= 0; i--)
        {
            TransactionEntry entry = transaction.Entries[i];
            string path = ResolveManagedPath(entry.RelativePath);
            try
            {
                _activationProbe?.Invoke("restore:" + entry.RelativePath);
                if (Directory.Exists(path))
                    throw new IOException("A directory occupies the managed file path.");
                bool isOld = entry.PreviousExisted && MatchesFile(path, entry.PreviousSize, entry.PreviousSha256!);
                bool isNew = entry.NewSha256 != null && MatchesFile(path, entry.NewSize, entry.NewSha256);
                if (File.Exists(path) && !isOld && !isNew)
                    throw new IOException("The file changed outside this transaction.");
                if (entry.PreviousExisted && !isOld)
                {
                    CheckDeploymentPath(Path.GetDirectoryName(path)!, "create restore target directory");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    string restoreDirectory = Path.Combine(_paths.DeploymentTransactionDirectory, "restore");
                    CheckDeploymentPath(restoreDirectory, "create restore directory");
                    Directory.CreateDirectory(restoreDirectory);
                    if ((File.GetAttributes(restoreDirectory) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Transaction restore directory is redirected.");
                    string temporary = Path.Combine(restoreDirectory, i.ToString("D4") + ".tmp");
                    CopyDurably(BackupPath(i), temporary);
                    CheckDeploymentPath(temporary, "restore source");
                    CheckDeploymentPath(path, "restore target");
                    File.Move(temporary, path, true);
                }
                else if (!entry.PreviousExisted && File.Exists(path))
                {
                    CheckDeploymentPath(path, "rollback delete");
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                failures.Add(entry.RelativePath + ": " + ex.Message);
            }
        }
        foreach (TransactionEntry entry in transaction.Entries)
        {
            string path = ResolveManagedPath(entry.RelativePath);
            try
            {
                if (entry.PreviousExisted ? !MatchesFile(path, entry.PreviousSize, entry.PreviousSha256!) :
                    File.Exists(path) || Directory.Exists(path))
                    failures.Add(entry.RelativePath + ": previous contents did not verify");
            }
            catch (Exception ex) { failures.Add(entry.RelativePath + ": " + ex.Message); }
        }
        if (failures.Count != 0)
            throw new IOException("Incomplete deployment recovery: " + string.Join("; ", failures));
    }

    private void VerifyCurrentVersion(TransactionEntry entry)
    {
        string path = ResolveManagedPath(entry.RelativePath);
        if (Directory.Exists(path) ||
            (entry.PreviousExisted ? !MatchesFile(path, entry.PreviousSize, entry.PreviousSha256!) : File.Exists(path)))
            throw new InvalidDataException("Deployment target changed during publication: " + entry.RelativePath);
    }

    private void CleanupTransaction(bool committed)
    {
        ValidateDeploymentTree(_paths.DeploymentTransactionDirectory);
        foreach (string name in new[] { "publish", "restore" })
        {
            string temporaryDirectory = Path.Combine(_paths.DeploymentTransactionDirectory, name);
            if (Directory.Exists(temporaryDirectory))
            {
                CheckTransactionFiles(temporaryDirectory, ".tmp");
                DeleteDeploymentDirectory(temporaryDirectory);
            }
        }
        if (committed)
        {
            string backups = Path.Combine(_paths.DeploymentTransactionDirectory, "backups");
            if (Directory.Exists(backups))
            {
                CheckTransactionFiles(backups, ".bak");
                DeleteDeploymentDirectory(backups);
            }
        }
        File.Delete(JournalPath);
        DeleteDeploymentDirectory(_paths.DeploymentTransactionDirectory);
    }

    private static void CheckTransactionFiles(string directory, string extension)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Transaction directory is redirected: " + directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(path);
            if (Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !name.EndsWith(extension, StringComparison.Ordinal) ||
                !int.TryParse(name[..^extension.Length], out int index) || index < 0)
                throw new InvalidDataException("Unrecognized transaction file: " + path);
        }
    }

    private ManagedDeploymentInventory LoadAndValidateInventory(DeploymentManifest? manifest)
    {
        ResolveManagedPath("iPod/deployment-managed-files.json");
        bool inventoryExists = File.Exists(_paths.ManagedDeploymentInventory);
        if ((manifest is null) == inventoryExists)
            throw new InvalidDataException("Deployment manifest and managed inventory must both exist or both be absent.");
        if (manifest == null) return new();

        ManagedDeploymentInventory inventory = ManagedDeploymentInventory.Load(_paths.ManagedDeploymentInventory);
        ValidateInventoryAgainstManifest(inventory, manifest);
        return inventory;
    }

    private void ValidateInventoryAgainstManifest(ManagedDeploymentInventory inventory, DeploymentManifest manifest)
    {
        if (inventory.Version != 1 || inventory.Files == null)
            throw new InvalidDataException("Invalid managed inventory version or file list.");
        if (inventory.Generation != manifest.Generation)
            throw new InvalidDataException("Manifest/inventory generation mismatch.");

        var expected = new Dictionary<string, (string Hash, ulong Size)>(StringComparer.Ordinal);
        foreach (DeploymentManifestRecord record in manifest.Records)
        {
            expected.Add(ArtifactNaming.DbmRequest(record.RuntimeId),
                (Convert.ToHexString(record.DbmSha256), record.DbmSize));
            string bankPath = "common/bank/default/" + record.BankName;
            var bank = (Convert.ToHexString(record.BankSha256), record.BankSize);
            if (expected.TryGetValue(bankPath, out var existingBank) && existingBank != bank)
                throw new InvalidDataException("Manifest records disagree about the shared BANK.");
            expected[bankPath] = bank;
        }
        expected.Add("iPod/deployment.bin", (string.Empty, 0));
        if (inventory.Files.Count != expected.Count)
            throw new InvalidDataException("Managed inventory does not match the deployment manifest.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedDeploymentFile file in inventory.Files)
        {
            if (file == null || file.RelativePath == null || file.Sha256 == null ||
                !seen.Add(file.RelativePath) || !expected.TryGetValue(file.RelativePath, out var entry) ||
                file.Size == 0 || file.Sha256.Length != 64 ||
                file.Sha256.Any(c => !char.IsAsciiHexDigitUpper(c)) ||
                (file.RelativePath != "iPod/deployment.bin" &&
                 (file.Size != entry.Size || file.Sha256 != entry.Hash)))
                throw new InvalidDataException("Invalid managed inventory entry or unowned path.");
            ResolveManagedPath(file.RelativePath);
        }
    }

    private void PreflightOwnership(ManagedDeploymentInventory previous,
        IReadOnlyList<(string Source, string Relative)> targets)
    {
        var owned = previous.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (ManagedDeploymentFile file in previous.Files)
            VerifyOwnedFile(file);

        foreach (var item in targets)
        {
            string path = ResolveManagedPath(item.Relative);
            if (Directory.Exists(path))
                throw new InvalidDataException("Deployment target is a directory: " + item.Relative);
            if (!File.Exists(path)) continue;
            if (item.Relative == "iPod/deployment-managed-files.json")
            {
                if (previous.Version != 1 || previous.Files.Count == 0)
                    throw new InvalidDataException("Unowned managed inventory target collision.");
                continue; // The validated inventory is the ownership root; it cannot hash itself.
            }
            if (!owned.ContainsKey(item.Relative))
                throw new InvalidDataException("Unowned deployment target collision: " + item.Relative);
        }
    }

    private void VerifyOwnedFile(ManagedDeploymentFile file)
    {
        string path = ResolveManagedPath(file.RelativePath);
        if (Directory.Exists(path))
            throw new InvalidDataException("Application-managed file was replaced by a directory: " + file.RelativePath);
        if (!File.Exists(path)) return;
        using FileStream stream = File.OpenRead(path);
        if ((ulong)stream.Length != file.Size ||
            !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Application-managed file was externally modified: " + file.RelativePath);
    }

    private string ResolveDbmTemplate()
    {
        string templatePath = Path.Combine(_paths.Tools, "ipod-dbm-template.dbm");
        if (!File.Exists(templatePath))
            throw new DeploymentException("Compatible PC DBM donor is missing. Install ipod-dbm-template.dbm in iPod/tools.");
        return templatePath;
    }

    private string ResolveManagedPath(string relative, string operation = "resolve managed artifact")
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid managed path.");
        bool bank = relative.StartsWith("common/bank/default/", StringComparison.Ordinal);
        bool dbm = relative.StartsWith("common/dbm/", StringComparison.Ordinal);
        string name = bank ? relative["common/bank/default/".Length..] :
            dbm ? relative["common/dbm/".Length..] : string.Empty;
        string extension = bank ? ".bank" : ".dbm";
        bool generatedArtifact = bank && name == DeploymentContract.ProgrammerBankName ||
            (bank || dbm) && name.EndsWith(extension, StringComparison.Ordinal) &&
            ArtifactNaming.IsGeneratedRuntimeId(name[..^extension.Length]);
        if (!generatedArtifact && relative is not ("iPod/deployment.bin" or "iPod/deployment-managed-files.json"))
            throw new InvalidDataException("Path is outside the managed-file boundary.");
        string full = Path.GetFullPath(Path.Combine(_paths.Mgs4Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_paths.Mgs4Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Managed path escapes game root.");
        return CheckDeploymentPath(full, operation);
    }

    // Installation ancestors may be Steam junctions. Descendants of the pinned game root may not redirect.
    private string CheckDeploymentPath(string path, string operation)
    {
        string full = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(_paths.Mgs4Root);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Deployment path escapes game root: " + full);
        string resolvedRoot = ResolveDirectoryRoot(root);
        _resolvedGameRoot ??= resolvedRoot;
        if (!resolvedRoot.Equals(_resolvedGameRoot, StringComparison.OrdinalIgnoreCase))
            Block(root, "trusted root changed");
        string current = root;
        foreach (string component in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            if (component == ".") continue;
            current = Path.Combine(current, component);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                Block(current, "ReparsePoint attributes=" + attributes);
        }
        return full;

        void Block(string component, string reason)
        {
            string detail = $"Blocked deployment operation='{operation}' path='{full}' component='{component}' " +
                $"reason='{reason}' trusted_root='{_resolvedGameRoot}'";
            AppLog.Warn(detail);
            throw new InvalidDataException(detail);
        }
    }

    private void DeleteDeploymentDirectory(string directory)
    {
        ValidateDeploymentTree(directory);
        CheckDeploymentPath(directory, "delete deployment directory");
        Directory.Delete(directory, true);
    }

    private void ValidateDeploymentTree(string path)
    {
        CheckDeploymentPath(path, "validate recursive cleanup");
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            CheckDeploymentPath(child, "validate recursive cleanup child");
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) ValidateDeploymentTree(child);
        }
    }

    private static string ResolveDirectoryRoot(string root)
    {
        string extended = root.StartsWith("\\\\?\\", StringComparison.Ordinal) ? root :
            root.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + root[2..] : "\\\\?\\" + root;
        // Metadata-only directory handle: share read/write/delete, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS.
        using SafeFileHandle handle = CreateFileW(extended, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot resolve deployment root: " + root,
            new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        // The resolved NT path also identifies volumes mounted without a drive letter; it is only compared/logged.
        const uint volumeNameNt = 2;
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, volumeNameNt);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, volumeNameNt);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("Cannot resolve deployment root: " + root,
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString().TrimEnd(Path.DirectorySeparatorChar);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

internal enum DeploymentWorkStage
{
    Validating,
    Converting,
    Building,
    Installing,
    TransactionPrepared,
    FilesApplied,
    StatePublished,
    DeploymentCommitted,
    CleanupComplete
}
