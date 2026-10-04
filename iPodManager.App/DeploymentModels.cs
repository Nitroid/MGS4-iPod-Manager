using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace iPodManager;

public enum TrackClassification : byte { Music = 0, Podcast = 1 }

internal static class DeploymentContract
{
    public const int StockCount = 73;
    public const int NativeCatalogCapacity = 1024;
    public const int MaxNonDefaultTracks = NativeCatalogCapacity - StockCount;
    public const uint DirectStreamProfile = 2;
    public const string ProgrammerBankName = "MGS4iPodStreaming.bank";
    public const string ProgrammerInstrumentName = "MGS4_STREAM_TEST";
    public static readonly Guid ProgrammerEventGuid = new("67a17324-5038-4de0-9d66-b9d276ab8aa5");
    public static bool SupportsNonDefaultTrackCount(int count) => count >= 0 && count <= MaxNonDefaultTracks;
    public static string NormalizeRelative(string path) => path.Replace('\\', '/');
}

internal sealed record DeploymentTrackIntent(Guid StableId, LibraryCategory Category, string SourcePath,
    string RelativeSourcePath, string Title, string Artist, string Album, bool Enabled = true);

internal sealed record DeploymentIdentity(Guid StableId, uint ControlId, string RuntimeId);

internal static class ControlIdAllocator
{
    public static readonly (uint First, uint Last)[] Bands =
        [(9000, 9299), (9600, 9899), (10200, 10499), (10800, 10850)];
    public static bool IsControl(uint value) => Bands.Any(x => value >= x.First && value <= x.Last);
    public static Dictionary<Guid, uint> Allocate(IEnumerable<(Guid StableId, uint? Existing)> source)
    {
        var rows = source.ToArray();
        if (rows.Any(x => x.StableId == Guid.Empty) || rows.Select(x => x.StableId).Distinct().Count() != rows.Length)
            throw new InvalidDataException("Duplicate or empty stable track identity.");
        var result = new Dictionary<Guid, uint>();
        var used = new HashSet<uint>();
        foreach (var row in rows.OrderBy(x => x.StableId))
        {
            if (row.Existing is uint id)
            {
                if (!IsControl(id) || !used.Add(id))
                    throw new InvalidDataException("Invalid or duplicate retained control ID.");
                result[row.StableId] = id;
            }
        }
        using var available = Bands
            .SelectMany(x => Enumerable.Range((int)x.First, checked((int)(x.Last - x.First + 1))).Select(y => (uint)y))
            .Where(x => !used.Contains(x))
            .GetEnumerator();
        foreach (var row in rows.Where(x => !result.ContainsKey(x.StableId)).OrderBy(x => x.StableId))
        {
            if (!available.MoveNext())
                throw new InvalidOperationException("Custom control-ID space exhausted (951).");
            result[row.StableId] = available.Current;
        }
        return result;
    }
}

internal sealed record SourceAudioArtifact(byte[] SourceSha256, ulong DurationSeconds);

internal sealed record BuiltTrack(DeploymentTrackIntent Intent, DeploymentIdentity Identity,
    SourceAudioArtifact Audio, string DbmPath, byte[] DbmSha256, ulong DbmSize,
    string BankPath, byte[] BankSha256, ulong BankSize, Guid EventGuid,
    string DbmRequestPath, string EventPath);

internal enum DeploymentPlanAction { Add, Update, Remove }
internal sealed record DeploymentPlanItem(Guid StableId, string Title, DeploymentPlanAction Action, uint? ControlId);
internal sealed record DeploymentPlan(IReadOnlyList<DeploymentPlanItem> Items, int FinalRecordCount, string StagingDirectory);
internal sealed record DeploymentResult(DeploymentPlan Plan, string ManifestPath, ulong Generation, IReadOnlyList<BuiltTrack> Tracks);

internal sealed record ManagedDeploymentFile(string RelativePath, string Sha256, ulong Size);
internal sealed class ManagedDeploymentInventory
{
    public int Version { get; set; }
    public ulong Generation { get; set; }
    public List<ManagedDeploymentFile> Files { get; set; } = [];
    public static ManagedDeploymentInventory Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<ManagedDeploymentInventory>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Invalid managed inventory.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid managed inventory.", ex);
        }
    }

    public void Save(string path)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
