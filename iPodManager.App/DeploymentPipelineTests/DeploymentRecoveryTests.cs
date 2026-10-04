using System.Security.Cryptography;
using System.Text.Json.Nodes;
using iPodManager;

internal static class DeploymentRecoveryTests
{
    public static async Task Run(GamePaths paths, DeploymentTrackIntent a, DeploymentTrackIntent c, byte[] stock)
    {
        string transactionDirectory = paths.DeploymentTransactionDirectory;
        string journal = Path.Combine(transactionDirectory, "deployment-transaction.json");

        async Task Establish(params DeploymentTrackIntent[] tracks)
        {
            await new DeploymentService(paths).BuildAndDeployAsync(tracks, stock, 75);
            Require(!Directory.Exists(transactionDirectory), "successful transfer removes transaction backups");
        }

        void CheckCoherent(params Guid[] ids)
        {
            DeploymentManifest manifest = DeploymentManifestSerializer.Read(File.ReadAllBytes(paths.DeploymentManifest));
            ManagedDeploymentInventory inventory = ManagedDeploymentInventory.Load(paths.ManagedDeploymentInventory);
            Require(manifest.Records.Select(x => x.SourceId).ToHashSet().SetEquals(ids), "recovered manifest has one complete track set");
            Require(inventory.Generation == manifest.Generation && inventory.Files.Count == ids.Length + (ids.Length > 0 ? 2 : 1),
                "recovered inventory agrees with manifest generation and artifact count");
            foreach (ManagedDeploymentFile file in inventory.Files)
            {
                string path = Path.Combine(paths.Mgs4Root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Require(File.Exists(path) && (ulong)new FileInfo(path).Length == file.Size &&
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == file.Sha256,
                    "recovered file matches owned hash: " + file.RelativePath);
            }
            foreach (DeploymentManifestRecord record in manifest.Records)
            {
                Require(record.RuntimeId == ArtifactNaming.RuntimeId(record.SourceId), "recovered runtime ID is deterministic");
                Require(inventory.Files.Any(x => x.RelativePath == "common/dbm/" + record.RuntimeId + ".dbm") &&
                    inventory.Files.Any(x => x.RelativePath == "common/bank/default/" + DeploymentContract.ProgrammerBankName),
                    "recovered BANK and DBM are owned");
            }
            string[] liveArtifacts = Directory.GetFiles(paths.DefaultBanks, DeploymentContract.ProgrammerBankName)
                .Concat(Directory.GetFiles(paths.CommonDbm, "M4IPOD_*.dbm")).ToArray();
            Require(liveArtifacts.Length == ids.Length + (ids.Length > 0 ? 1 : 0), "recovery leaves no mixed-generation artifacts");
            Require(!Directory.Exists(transactionDirectory), "recovery removes journal and backups");
        }

        async Task Interrupt(string name, Func<string, bool> at, bool expectNew)
        {
            await Establish(a);
            bool reached = false;
            var interrupted = new DeploymentService(paths, point =>
            {
                if (!reached && at(point))
                {
                    reached = true;
                    throw new SimulatedProcessTerminationException();
                }
            });
            await ExpectTermination(() => interrupted.BuildAndDeployAsync([a, c], stock, 75));
            Require(reached && File.Exists(journal), name + " leaves a durable journal");
            new DeploymentService(paths).RecoverPendingTransaction();
            CheckCoherent(expectNew ? [a.StableId, c.StableId] : [a.StableId]);
            Console.WriteLine("PASS recovery at " + name);
        }

        await Interrupt("journal creation", x => x == "journal-created", false);
        for (int artifact = 1; artifact <= 3; artifact++)
        {
            int ordinal = artifact, seen = 0;
            await Interrupt("generated artifact " + ordinal,
                x => x.StartsWith("published:common/", StringComparison.Ordinal) && ++seen == ordinal, false);
        }
        await Interrupt("before manifest", x => x == "before-publish:iPod/deployment.bin", false);
        await Interrupt("after manifest", x => x == "published:iPod/deployment.bin", false);
        await Interrupt("before inventory", x => x == "before-publish:iPod/deployment-managed-files.json", false);
        await Interrupt("after inventory", x => x == "published:iPod/deployment-managed-files.json", true);
        await Interrupt("before transaction cleanup", x => x == "commit", true);

        await Establish(a);
        var nextOperationInterruption = new DeploymentService(paths, x =>
        {
            if (x == "published:iPod/deployment.bin") throw new SimulatedProcessTerminationException();
        });
        await ExpectTermination(() => nextOperationInterruption.BuildAndDeployAsync([a, c], stock, 75));
        await new DeploymentService(paths).BuildAndDeployAsync([a], stock, 75);
        CheckCoherent(a.StableId);
        Console.WriteLine("PASS next deployment recovers before starting new work");

        await Establish(a);
        var cleanupInterruption = new DeploymentService(paths, x =>
        {
            if (x == "commit") throw new SimulatedProcessTerminationException();
        });
        await ExpectTermination(() => cleanupInterruption.BuildAndDeployAsync([a, c], stock, 75));
        File.Delete(Path.Combine(transactionDirectory, "backups", "0000.bak"));
        new DeploymentService(paths).RecoverPendingTransaction();
        CheckCoherent(a.StableId, c.StableId);
        Console.WriteLine("PASS committed transaction finalizes after partial backup cleanup");

        foreach (string point in new[] { "before-remove:common/", "removed:common/" })
        {
            await Establish(a, c);
            var interrupted = new DeploymentService(paths, x =>
            {
                if (x.StartsWith(point, StringComparison.Ordinal))
                    throw new SimulatedProcessTerminationException();
            });
            await ExpectTermination(() => interrupted.BuildAndDeployAsync([a], stock, 75));
            new DeploymentService(paths).RecoverPendingTransaction();
            CheckCoherent(a.StableId, c.StableId);
            Console.WriteLine("PASS recovery during stale removal: " + point);
        }

        async Task CorruptJournal(string name, Action<JsonNode>? change, string? raw = null)
        {
            await Establish(a);
            var interrupted = new DeploymentService(paths, x =>
            {
                if (x == "journal-created") throw new SimulatedProcessTerminationException();
            });
            await ExpectTermination(() => interrupted.BuildAndDeployAsync([a, c], stock, 75));
            byte[] original = File.ReadAllBytes(journal);
            var before = Snapshot(paths);
            if (raw != null) File.WriteAllText(journal, raw);
            else
            {
                JsonNode node = JsonNode.Parse(original)!;
                change!(node);
                File.WriteAllText(journal, node.ToJsonString());
            }
            try
            {
                ExpectRecoveryRequired(() => new DeploymentService(paths).RecoverPendingTransaction());
                try
                {
                    await new Mgs4IpodService(paths).TransferAsync([], new ApplicationPreferences());
                    throw new Exception("FAIL application accepted corrupt recovery journal");
                }
                catch (Mgs4IpodService.TransferException ex)
                {
                    Require(ex.FailureState == Mgs4IpodService.TransferFailureState.RecoveryRequired,
                        "application reports recovery required");
                }
                Require(SameSnapshot(before, Snapshot(paths)) && File.Exists(journal),
                    name + " fails closed without mutating game files");
            }
            finally { File.WriteAllBytes(journal, original); }
            new DeploymentService(paths).RecoverPendingTransaction();
            CheckCoherent(a.StableId);
            Console.WriteLine("PASS corrupt journal: " + name);
        }

        await CorruptJournal("malformed JSON", null, "{");
        await CorruptJournal("unsupported version", node => node["Version"] = 99);
        await CorruptJournal("invalid phase", node => node["Phase"] = "Unknown");
        await CorruptJournal("path traversal", node => node["Entries"]!.AsArray()[0]!["RelativePath"] = "common/dbm/../Arsenal_Guts.bank");
        await CorruptJournal("duplicate target", node => node["Entries"]!.AsArray()[1]!["RelativePath"] =
            node["Entries"]!.AsArray()[0]!["RelativePath"]!.GetValue<string>());
        await CorruptJournal("invalid hash", node => node["Entries"]!.AsArray()[0]!["PreviousSha256"] = "BAD");
        await CorruptJournal("generation mismatch", node => node["NewGeneration"] = 999UL);
        await CorruptJournal("stock donor claim", node => node["Entries"]!.AsArray()[0]!["RelativePath"] =
            "common/bank/default/Arsenal_Guts.bank");

        async Task DamagedBackup(string name, bool remove)
        {
            await Establish(a);
            var interrupted = new DeploymentService(paths, x =>
            {
                if (x.StartsWith("published:common/", StringComparison.Ordinal))
                    throw new SimulatedProcessTerminationException();
            });
            await ExpectTermination(() => interrupted.BuildAndDeployAsync([a, c], stock, 75));
            string backup = Path.Combine(transactionDirectory, "backups", "0000.bak");
            byte[] original = File.ReadAllBytes(backup);
            var before = Snapshot(paths);
            if (remove) File.Delete(backup);
            else File.WriteAllText(backup, "corrupt backup");
            try
            {
                ExpectRecoveryRequired(() => new DeploymentService(paths).RecoverPendingTransaction());
                Require(SameSnapshot(before, Snapshot(paths)) && File.Exists(journal),
                    name + " retains evidence without mutation");
            }
            finally { File.WriteAllBytes(backup, original); }
            new DeploymentService(paths).RecoverPendingTransaction();
            CheckCoherent(a.StableId);
            Console.WriteLine("PASS damaged backup: " + name);
        }
        await DamagedBackup("missing backup", true);
        await DamagedBackup("corrupt backup", false);

        async Task RollbackFailure(string name, string failurePoint, Exception restoreError)
        {
            await Establish(a);
            bool interrupted = false;
            var failing = new DeploymentService(paths, point =>
            {
                if (point == failurePoint && !interrupted)
                {
                    interrupted = true;
                    throw new IOException("injected publication failure");
                }
                if (interrupted && point.StartsWith("restore:", StringComparison.Ordinal) &&
                    point.EndsWith(name == "delete" ? ArtifactNaming.DbmName(ArtifactNaming.RuntimeId(c.StableId)) :
                        ArtifactNaming.DbmName(ArtifactNaming.RuntimeId(a.StableId)), StringComparison.Ordinal))
                    throw restoreError;
            });
            try
            {
                await failing.BuildAndDeployAsync([a with { Title = "Changed for rollback" }, c], stock, 75);
                throw new Exception("FAIL rollback failure was not reported");
            }
            catch (DeploymentException ex)
            {
                Require(ex.FailureState == DeploymentFailureState.RecoveryRequired &&
                    ex.Message.Contains("manual attention", StringComparison.Ordinal) && File.Exists(journal),
                    name + " reports incomplete recovery and retains journal");
            }
            new DeploymentService(paths).RecoverPendingTransaction();
            CheckCoherent(a.StableId);
            Console.WriteLine("PASS rollback failure: " + name);
        }
        await RollbackFailure("copy", "published:common/dbm/" + ArtifactNaming.DbmName(ArtifactNaming.RuntimeId(a.StableId)),
            new IOException("injected backup copy failure"));
        await RollbackFailure("delete", "published:common/dbm/" + ArtifactNaming.DbmName(ArtifactNaming.RuntimeId(c.StableId)),
            new IOException("injected delete failure"));
        await RollbackFailure("permission", "published:common/dbm/" + ArtifactNaming.DbmName(ArtifactNaming.RuntimeId(a.StableId)),
            new UnauthorizedAccessException("injected access failure"));

        await Establish(a);
        CheckCoherent(a.StableId);

        Directory.CreateDirectory(Path.Combine(transactionDirectory, "backups"));
        File.WriteAllText(journal + ".tmp", "partial journal before publication");
        new DeploymentService(paths).RecoverPendingTransaction();
        Require(!Directory.Exists(transactionDirectory), "pre-journal preparation is cleaned safely");
        Directory.CreateDirectory(transactionDirectory);
        string unknown = Path.Combine(transactionDirectory, "unknown-file");
        File.WriteAllText(unknown, "unknown");
        var finalSnapshot = Snapshot(paths);
        ExpectRecoveryRequired(() => new DeploymentService(paths).RecoverPendingTransaction());
        Require(SameSnapshot(finalSnapshot, Snapshot(paths)) && File.Exists(unknown),
            "unknown transaction contents fail closed");
        File.Delete(unknown);
        Directory.Delete(transactionDirectory);
        Console.WriteLine("PASS journal-less transaction directory handling");

        using (FileStream heldLock = new(Path.Combine(paths.IpodRoot, ".deployment-transfer.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                await new DeploymentService(paths).BuildAndDeployAsync([a], stock, 75);
                throw new Exception("FAIL concurrent transfer lock");
            }
            catch (DeploymentException ex)
            {
                Require(ex.FailureState == DeploymentFailureState.BeforeMutation &&
                    ex.Message.Contains("already running", StringComparison.Ordinal),
                    "second transfer is rejected before publication");
            }
        }
        CheckCoherent(a.StableId);
        Console.WriteLine("PASS concurrent transfer lock");
    }

    private static Dictionary<string, string> Snapshot(GamePaths paths)
    {
        IEnumerable<string> files = Directory.GetFiles(paths.DefaultBanks, DeploymentContract.ProgrammerBankName)
            .Concat(Directory.GetFiles(paths.CommonDbm, "M4IPOD_*.dbm"))
            .Concat(new[] { paths.DeploymentManifest, paths.ManagedDeploymentInventory });
        return files.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static bool SameSnapshot(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);

    private static async Task ExpectTermination(Func<Task> work)
    {
        try { await work(); throw new Exception("FAIL simulated process termination was not reached"); }
        catch (SimulatedProcessTerminationException) { }
    }

    private static void ExpectRecoveryRequired(Action work)
    {
        try { work(); throw new Exception("FAIL recovery accepted corrupt evidence"); }
        catch (DeploymentException ex)
        {
            Require(ex.FailureState == DeploymentFailureState.RecoveryRequired,
                "recovery failure requires manual attention");
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
    }
}
