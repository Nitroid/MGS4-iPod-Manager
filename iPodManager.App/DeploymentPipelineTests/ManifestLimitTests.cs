using System.Text;
using iPodManager;

internal static class ManifestLimitTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }

    private static void Reject(Action action, string detail)
    {
        try { action(); }
        catch (InvalidDataException ex)
        {
            Check(ex.Message.Contains(detail, StringComparison.Ordinal), "manifest limit diagnostic: " + detail);
            return;
        }
        throw new Exception("FAIL expected manifest limit rejection");
    }

    internal static async Task Run(GamePaths paths, DeploymentManifestRecord seed)
    {
        var stock = Enumerable.Repeat((byte)1, 73).ToArray();
        foreach (string path in new[]
        {
            "custom/" + new string('a', 1016),
            "custom/" + new string('a', 1017),
            "custom/" + new string('\u65e5', 339),
            "custom/" + new string('\u00e9', 508) + "a",
            "custom/" + string.Concat(Enumerable.Repeat("\U0001f600", 254)) + "a",
            DeploymentContract.NormalizeRelative("custom\\folder/" + new string('a', 1010))
        })
        {
            var record = seed with { SourcePath = path };
            byte[] bytes = DeploymentManifestSerializer.Write(new(1, stock, [record]));
            Check(DeploymentManifestSerializer.Read(bytes).Records.Single().SourcePath == path,
                $"source path {Encoding.UTF8.GetByteCount(path)} bytes accepted without truncation");
        }
        foreach (string path in new[] { "custom/" + new string('a', 1018), "custom/" + new string('\u65e5', 340) })
            Reject(() => DeploymentManifestSerializer.Write(new(1, stock, [seed with { SourcePath = path }])),
                Encoding.UTF8.GetByteCount(path) + " UTF-8 bytes");

        var ids = Enumerable.Range(1, 951).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
        var controls = ControlIdAllocator.Allocate(ids.Select(id => (id, (uint?)null)));
        var records = ids.Select(id =>
        {
            string runtime = ArtifactNaming.RuntimeId(id);
            return seed with
            {
                SourceId = id, RuntimeId = runtime, ControlId = controls[id], SourcePath = "custom/a.flac",
                DbmPath = ArtifactNaming.DbmRequest(runtime), DbmRequestPath = ArtifactNaming.DbmRequest(runtime),
                DescriptorEventPath = ArtifactNaming.EventPath(runtime)
            };
        }).ToArray();
        byte[] ordinary = DeploymentManifestSerializer.Write(new(1, stock, records));
        Check(DeploymentManifestSerializer.Read(ordinary).Records.Count + 73 == 1024,
            "ordinary 1024-record catalog remains accepted below the manifest ceiling");

        int remaining = DeploymentManifestSerializer.MaxManifestBytes - ordinary.Length;
        for (int i = 0; i < records.Length && remaining > 0; i++)
        {
            int extra = Math.Min(remaining, DeploymentManifestSerializer.MaxSourcePathBytes - records[i].SourcePath.Length - 1);
            records[i] = records[i] with { SourcePath = records[i].SourcePath + new string('a', extra) };
            remaining -= extra;
        }
        Check(remaining == 0, "fixture reaches the exact serialized manifest boundary");
        byte[] exact = DeploymentManifestSerializer.Write(new(1, stock, records));
        Check(exact.Length == 1048576 && DeploymentManifestSerializer.Read(exact).Records.Count == 951,
            "exact 1 MiB complete manifest accepted");
        records[^1] = records[^1] with { SourcePath = records[^1].SourcePath[..^1] };
        Check(DeploymentManifestSerializer.Write(new(1, stock, records)).Length == 1048575,
            "manifest one byte below maximum accepted");
        records[^1] = records[^1] with { SourcePath = records[^1].SourcePath + "aa" };
        Reject(() => DeploymentManifestSerializer.Write(new(1, stock, records)), "1048577 bytes");
        Reject(() => DeploymentManifestSerializer.Read(new byte[1048577]), "1048577 bytes");

        DeploymentTrackIntent[] ToIntents() => records.Select(r => new DeploymentTrackIntent(
            r.SourceId, LibraryCategory.Custom, "not-read.flac", r.SourcePath, r.Title, r.Artist, r.Album)).ToArray();
        var oversized = ToIntents();
        await RejectBeforeMutation(oversized, "too much deployment data");
        await RejectBeforeMutation([oversized[0] with { RelativeSourcePath = "custom\\" + new string('a', 1018) }],
            "source path is too long");
        records[^1] = records[^1] with { SourcePath = records[^1].SourcePath[..^1] };
        Check(DeploymentManifestSerializer.GetDirectStreamSize(ToIntents()) == exact.Length,
            "preflight and final serializer compute identical sizes at the boundary");

        async Task RejectBeforeMutation(DeploymentTrackIntent[] intents, string userText)
        {
            var before = Directory.GetFiles(paths.Mgs4Root, "*", SearchOption.AllDirectories)
                .ToDictionary(p => p, File.ReadAllBytes);
            bool activated = false;
            try
            {
                await new DeploymentService(paths, _ => activated = true)
                    .BuildAndDeployAsync(intents, stock, 75);
                throw new Exception("FAIL expected preflight rejection");
            }
            catch (DeploymentException ex)
            {
                Check(ex.FailureState == DeploymentFailureState.BeforeMutation &&
                    ex.UserMessage?.Contains(userText, StringComparison.Ordinal) == true,
                    "limit failure is actionable and BeforeMutation");
            }
            Check(!activated && !Directory.Exists(Path.Combine(paths.IpodRoot, ".deployment-staging")) &&
                before.Count == Directory.GetFiles(paths.Mgs4Root, "*", SearchOption.AllDirectories).Length &&
                before.All(entry => File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value)),
                "limit rejection precedes staging, recovery, inventory and deployment mutation");
        }
    }
}
