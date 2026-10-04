using System.IO;

namespace iPodManager;

public sealed class Mgs4IpodService
{
    private readonly GamePaths _paths;

    public Mgs4IpodService(GamePaths paths) => _paths = paths;

    public async Task<TransferResult> TransferAsync(
        IReadOnlyCollection<TrackItem> selectedTracks,
        ApplicationPreferences settings,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedTracks);
        ArgumentNullException.ThrowIfNull(settings);
        progress?.Report(new(TransferStage.Validating, 0, selectedTracks.Count));

        try
        {
            (DeploymentTrackIntent[] custom, byte[] stock) = PrepareDeploymentRequest(selectedTracks);
            var pipeline = new DeploymentService(_paths, null, (stage, current, total) =>
                progress?.Report(new(MapStage(stage), current, total)));
            DeploymentResult result = await pipeline.BuildAndDeployAsync(
                custom, stock, Math.Clamp(settings.ConversionQuality, 1, 100), token: cancellationToken);
            progress?.Report(new(TransferStage.Complete, result.Tracks.Count, result.Tracks.Count));
            return new(result.Generation, result.Tracks.Count,
                result.Plan.Items.Count(x => x.Action == DeploymentPlanAction.Add),
                result.Plan.Items.Count(x => x.Action == DeploymentPlanAction.Update),
                result.Plan.Items.Count(x => x.Action == DeploymentPlanAction.Remove));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DeploymentException ex)
        {
            TransferFailureState state = ex.FailureState switch
            {
                DeploymentFailureState.PreviousRestored => TransferFailureState.PreviousRestored,
                DeploymentFailureState.RecoveryRequired => TransferFailureState.RecoveryRequired,
                _ => TransferFailureState.BeforeMutation
            };
            throw new TransferException(ex.Message, ex, state, ex.UserMessage);
        }
        catch (Exception ex) when (ex is not TransferException)
        {
            throw new TransferException("The transfer failed before deployment files were changed: " + ex.Message,
                ex, TransferFailureState.BeforeMutation);
        }
    }

    private (DeploymentTrackIntent[] Custom, byte[] Stock) PrepareDeploymentRequest(IReadOnlyCollection<TrackItem> selected)
    {
        var stock = new byte[DeploymentContract.StockCount];
        var custom = new List<DeploymentTrackIntent>();
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stableIds = new HashSet<Guid>();
        Dictionary<string, Guid> deployedIds = LoadDeployedTrackIds();
        foreach (TrackItem track in selected)
        {
            if (track.Category == LibraryCategory.Default)
            {
                if (track.StockIndex is not int index || index < 0 || index >= stock.Length)
                    throw new TransferException($"Stock track '{track.Name}' has no valid catalog index.");
                stock[index] = 1;
                continue;
            }

            if (track.Category is not (LibraryCategory.Custom or LibraryCategory.Podcast) ||
                string.IsNullOrWhiteSpace(track.FilePath) || string.IsNullOrWhiteSpace(track.RelativeSourcePath))
                throw new TransferException($"Track '{track.Name}' has incomplete source information.");
            string fullPath = Path.GetFullPath(track.FilePath);
            if (!sourcePaths.Add(fullPath))
                throw new TransferException($"The source '{track.FilePath}' was selected more than once.");
            string relative = DeploymentContract.NormalizeRelative(track.RelativeSourcePath);
            TrackClassification classification = track.Category == LibraryCategory.Podcast ? TrackClassification.Podcast : TrackClassification.Music;
            Guid stableId = track.SourceId ?? (deployedIds.TryGetValue(SourceIdentityKey(classification, relative), out Guid existing) ? existing : Guid.NewGuid());
            if (!stableIds.Add(stableId))
                throw new TransferException($"Track identity {stableId:D} was selected more than once.");
            custom.Add(new(stableId, track.Category, fullPath,
                relative,
                track.Name.Trim(), track.ArtistText.Trim(), track.Album.Trim(), true));
        }
        return (custom.ToArray(), stock);
    }

    private Dictionary<string, Guid> LoadDeployedTrackIds()
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_paths.DeploymentManifest)) return result;
        foreach (DeploymentManifestRecord record in DeploymentManifestSerializer.Read(File.ReadAllBytes(_paths.DeploymentManifest)).Records)
            result[SourceIdentityKey(record.Classification, record.SourcePath)] = record.SourceId;
        return result;
    }
    private static string SourceIdentityKey(TrackClassification classification, string path) =>
        $"{(byte)classification}:{DeploymentContract.NormalizeRelative(path)}";
    private static TransferStage MapStage(DeploymentWorkStage stage) => stage switch
    {
        DeploymentWorkStage.Validating => TransferStage.Validating,
        DeploymentWorkStage.Converting => TransferStage.Converting,
        DeploymentWorkStage.Building => TransferStage.Building,
        DeploymentWorkStage.Installing => TransferStage.Installing,
        DeploymentWorkStage.TransactionPrepared => TransferStage.TransactionPrepared,
        DeploymentWorkStage.FilesApplied => TransferStage.FilesApplied,
        DeploymentWorkStage.StatePublished => TransferStage.StatePublished,
        DeploymentWorkStage.DeploymentCommitted => TransferStage.DeploymentCommitted,
        DeploymentWorkStage.CleanupComplete => TransferStage.CleanupComplete,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown backend transfer stage.")
    };

    public enum TransferStage
    {
        Validating,
        Converting,
        Building,
        Installing,
        TransactionPrepared,
        FilesApplied,
        StatePublished,
        DeploymentCommitted,
        CleanupComplete,
        Complete
    }
    public enum TransferFailureState { BeforeMutation, PreviousRestored, RecoveryRequired }
    public sealed record TransferProgress(TransferStage Stage, int Current, int Total);
    public sealed record TransferResult(ulong Generation, int TrackCount, int Added, int Updated, int Removed);
    public sealed class TransferException(string message, Exception? inner = null,
        TransferFailureState state = TransferFailureState.BeforeMutation,
        string? userMessage = null) : Exception(message, inner)
    {
        public TransferFailureState FailureState { get; } = state;
        public string? UserMessage { get; } = userMessage;
    }
}
