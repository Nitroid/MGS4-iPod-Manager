namespace iPodManager;

public enum LibraryCategory { Default, Podcast, Custom }
public enum SourceState { Available, Changed, Missing }
public enum DeploymentHealth { NotDeployed, Healthy, SourceChanged, SourceMissing, ArtifactMissing, ArtifactCorrupt, ManifestInvalid }

public sealed record DiscoveredSource(
    LibraryCategory Category, string RelativePath, string FullPath, byte[] Sha256,
    long Size, string Title, string Artist, string Album, string Comment,
    uint TrackNumber = 0, uint DiscNumber = 0);

public sealed record DeployedTrack(
    TrackClassification Classification, Guid SourceId, byte[] SourceSha256, ulong BankSize,
    string SourcePath, string Title, string Artist, string Album, DeploymentHealth ArtifactHealth);

public sealed record ReconciledSource(DiscoveredSource? Source, DeployedTrack? Deployed, SourceState SourceState, DeploymentHealth Health)
{
    public string StableKey => Deployed != null
        ? $"source:{Deployed.SourceId:D}"
        : $"new:{Source!.Category}:{Source.RelativePath}";
}

public static class LibraryReconciler
{
    public static IReadOnlyList<ReconciledSource> Reconcile(
        IReadOnlyList<DiscoveredSource> sources, IReadOnlyList<DeployedTrack> deployed)
    {
        var result = new List<ReconciledSource>();
        var unusedSources = new HashSet<DiscoveredSource>(sources);
        var unusedDeployed = new HashSet<DeployedTrack>(deployed);

        foreach (DeployedTrack entry in deployed)
        {
            LibraryCategory category = entry.Classification == TrackClassification.Podcast ? LibraryCategory.Podcast : LibraryCategory.Custom;
            DiscoveredSource? source = unusedSources.FirstOrDefault(s => s.Category == category &&
                string.Equals(s.RelativePath, DeploymentContract.NormalizeRelative(entry.SourcePath), StringComparison.OrdinalIgnoreCase));
            if (source == null) continue;
            AddMatch(result, source, entry);
            unusedSources.Remove(source); unusedDeployed.Remove(entry);
        }

        foreach (DiscoveredSource source in unusedSources.ToArray())
        {
            DeployedTrack[] candidates = unusedDeployed.Where(d =>
                CategoryOf(d) == source.Category && d.SourceSha256.SequenceEqual(source.Sha256)).ToArray();
            if (candidates.Length != 1) continue;
            AddMatch(result, source, candidates[0]);
            unusedSources.Remove(source); unusedDeployed.Remove(candidates[0]);
        }

        result.AddRange(unusedSources.Select(source => new ReconciledSource(source, null, SourceState.Available, DeploymentHealth.NotDeployed)));
        result.AddRange(unusedDeployed.Select(entry => new ReconciledSource(null, entry, SourceState.Missing,
            entry.ArtifactHealth == DeploymentHealth.Healthy ? DeploymentHealth.SourceMissing : entry.ArtifactHealth)));
        return result.OrderBy(item => item.Source?.Category ?? CategoryOf(item.Deployed!)).ThenBy(item => item.Source?.RelativePath ?? item.Deployed!.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddMatch(List<ReconciledSource> result, DiscoveredSource source, DeployedTrack entry)
    {
        bool changed = !source.Sha256.SequenceEqual(entry.SourceSha256);
        DeploymentHealth health = entry.ArtifactHealth == DeploymentHealth.Healthy && changed
            ? DeploymentHealth.SourceChanged : entry.ArtifactHealth;
        result.Add(new(source, entry, changed ? SourceState.Changed : SourceState.Available, health));
    }

    private static LibraryCategory CategoryOf(DeployedTrack track) =>
        track.Classification == TrackClassification.Podcast ? LibraryCategory.Podcast : LibraryCategory.Custom;
}
