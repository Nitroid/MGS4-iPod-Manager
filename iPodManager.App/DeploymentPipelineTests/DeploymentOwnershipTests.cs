using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using iPodManager;

internal static class DeploymentOwnershipTests
{
    internal static async Task Run(DeploymentTrackIntent a, DeploymentTrackIntent b, byte[] stock)
    {
        string root = Path.Combine(Path.GetTempPath(), "mgs4-ownership-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "MGS4");
        string ipod = Path.Combine(game, "iPod");
        Directory.CreateDirectory(Path.Combine(game, "common", "bank", "default"));
        Directory.CreateDirectory(Path.Combine(game, "scripts"));
        Directory.CreateDirectory(Path.Combine(ipod, "tools"));
        File.WriteAllBytes(Path.Combine(game, "mgs4.exe"), []);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "tools", "ipod-dbm-template.dbm"),
            Path.Combine(ipod, "tools", "ipod-dbm-template.dbm"));
        GamePaths paths = GamePaths.Resolve(ipod);
        string external = Path.Combine(root, "UnrelatedData");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "external data");
        try
        {
            await Establish();
            Require(Directory.Exists(paths.CommonDbm), "first sync safely creates missing DBM deployment directory");
            Console.WriteLine("PASS fresh-install sync creates DBM destination through existing checked deployment path");
            Console.WriteLine("PASS ordinary nested staging and deployment ownership");

            string alias = Path.Combine(root, "SteamLibraryJunction");
            Junction(alias, game);
            try
            {
                await new DeploymentService(GamePaths.Resolve(Path.Combine(alias, "iPod")))
                    .BuildAndDeployAsync([a, b], stock, 75);
                Console.WriteLine("PASS junction-style installation root supports normal deployment");
            }
            finally { Directory.Delete(alias); }

            string longGame = Path.Combine(root, new string('a', 80), new string('b', 80), new string('c', 80), "MGS4");
            string longIpod = Path.Combine(longGame, "iPod");
            Directory.CreateDirectory(Path.Combine(longGame, "common", "bank", "default"));
            Directory.CreateDirectory(Path.Combine(longGame, "common", "dbm"));
            Directory.CreateDirectory(Path.Combine(longGame, "scripts"));
            Directory.CreateDirectory(Path.Combine(longIpod, "tools"));
            File.WriteAllBytes(Path.Combine(longGame, "mgs4.exe"), []);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "tools", "ipod-dbm-template.dbm"),
                Path.Combine(longIpod, "tools", "ipod-dbm-template.dbm"));
            Require(longGame.Length > 260, "fixture uses a long installation path");
            await new DeploymentService(GamePaths.Resolve(longIpod)).BuildAndDeployAsync([a], stock, 75);
            Console.WriteLine("PASS long installation paths retain normal transaction and staging behavior");

            await Redirect(paths.CommonDbm, async () =>
            {
                await Blocked(() => new DeploymentService(paths).BuildAndDeployAsync([a], stock, 75));
            });
            Console.WriteLine("PASS parent junction blocks deployment replacement and inventory-owned stale removal");

            string staging = Path.Combine(ipod, ".deployment-staging");
            await Redirect(staging, async () =>
                await Blocked(() => new DeploymentService(paths).BuildAndDeployAsync([a], stock, 75)));
            Console.WriteLine("PASS redirected staging cannot write outside the game root");

            string target = Path.Combine(paths.CommonDbm, ArtifactNaming.DbmName(ArtifactNaming.RuntimeId(a.StableId)));
            string saved = target + ".saved";
            File.Move(target, saved);
            Junction(target, external);
            try { await Blocked(() => new DeploymentService(paths).BuildAndDeployAsync([a], stock, 75)); }
            finally { Directory.Delete(target); File.Move(saved, target); }
            Console.WriteLine("PASS final managed target reparse point rejected");

            File.Move(target, saved);
            bool fileLink = CreateSymbolicLinkW(target, Path.Combine(external, "sentinel.txt"), 2);
            try
            {
                if (fileLink)
                {
                    await Blocked(() => new DeploymentService(paths).BuildAndDeployAsync([a], stock, 75));
                    Require(File.ReadAllText(Path.Combine(external, "sentinel.txt")) == "external data", "file symlink target unchanged");
                    Console.WriteLine("PASS final file symlink rejected");
                }
                else if (Marshal.GetLastWin32Error() == 1314)
                    Console.WriteLine("SKIP file symlink creation requires privilege; final target junction case passed");
                else throw new Exception("File symlink creation failed: " + Marshal.GetLastWin32Error());
            }
            finally { if (fileLink) File.Delete(target); File.Move(saved, target); }

            await Establish();
            string parked = paths.CommonDbm + ".parked";
            var externalBefore = Snapshot(external);
            bool redirected = false;
            try
            {
                await Blocked(() => new DeploymentService(paths, point =>
                {
                    if (!redirected && point.StartsWith("before-remove:common/dbm/", StringComparison.Ordinal))
                    {
                        Directory.Move(paths.CommonDbm, parked);
                        Junction(paths.CommonDbm, external);
                        redirected = true;
                    }
                }).BuildAndDeployAsync([a], stock, 75), recoveryRequired: true);
                Require(redirected && Same(externalBefore, Snapshot(external)), "stale-removal boundary preserves external data");
            }
            finally { if (redirected) { Directory.Delete(paths.CommonDbm); Directory.Move(parked, paths.CommonDbm); } }
            new DeploymentService(paths).RecoverPendingTransaction();
            Console.WriteLine("PASS redirect introduced after preflight blocks stale deletion and preserves recovery evidence");

            await Establish();
            redirected = false;
            try
            {
                await Blocked(() => new DeploymentService(paths, point =>
                {
                    if (!redirected && point.StartsWith("published:common/dbm/", StringComparison.Ordinal))
                    {
                        Directory.Move(paths.CommonDbm, parked);
                        Junction(paths.CommonDbm, external);
                        redirected = true;
                        throw new IOException("Force rollback after installing a redirected parent.");
                    }
                }).BuildAndDeployAsync([b], stock, 75), recoveryRequired: true);
                Require(redirected && Same(externalBefore, Snapshot(external)), "rollback cannot copy/delete external data");
            }
            finally { if (redirected) { Directory.Delete(paths.CommonDbm); Directory.Move(parked, paths.CommonDbm); } }
            new DeploymentService(paths).RecoverPendingTransaction();
            Console.WriteLine("PASS rollback refuses redirected restore/delete targets; normal recovery works after removal");

            await Establish();
            try
            {
                await new DeploymentService(paths, point =>
                {
                    if (point == "journal-created") throw new SimulatedProcessTerminationException();
                }).BuildAndDeployAsync([b], stock, 75);
                throw new Exception("Expected interrupted transaction");
            }
            catch (SimulatedProcessTerminationException) { }
            await Redirect(Path.Combine(paths.DeploymentTransactionDirectory, "backups"), async () =>
            {
                await Blocked(() => { new DeploymentService(paths).RecoverPendingTransaction(); return Task.CompletedTask; }, true);
            });
            new DeploymentService(paths).RecoverPendingTransaction();
            Console.WriteLine("PASS recovery refuses redirected backup directory");

            string nested = Path.Combine(paths.DeploymentTransactionDirectory, "publish", "nested");
            try
            {
                await Blocked(() => new DeploymentService(paths, point =>
                {
                    if (point == "commit") Junction(nested, external);
                }).BuildAndDeployAsync([b], stock, 75), true);
                Require(File.Exists(Path.Combine(paths.DeploymentTransactionDirectory, "deployment-transaction.json")) &&
                    Same(externalBefore, Snapshot(external)), "unsafe cleanup retains journal and external data");
            }
            finally { if (Directory.Exists(nested)) Directory.Delete(nested); }
            new DeploymentService(paths).RecoverPendingTransaction();
            Console.WriteLine("PASS deeper nested reparse point blocks committed transaction cleanup before journal deletion");

            Directory.CreateDirectory(paths.DeploymentTransactionDirectory);
            Junction(Path.Combine(paths.DeploymentTransactionDirectory, "backups"), external);
            try
            {
                await Blocked(() => { new DeploymentService(paths).RecoverPendingTransaction(); return Task.CompletedTask; }, true);
                Require(Same(externalBefore, Snapshot(external)), "journal-less cleanup cannot touch external data");
            }
            finally
            {
                Directory.Delete(Path.Combine(paths.DeploymentTransactionDirectory, "backups"));
                Directory.Delete(paths.DeploymentTransactionDirectory);
            }
            Console.WriteLine("PASS journal-less recovery refuses redirected cleanup");

            // Check the pinned-root guard without exposing a production test accessor.
            alias = Path.Combine(root, "PinnedRoot");
            Junction(alias, game);
            var service = new DeploymentService(GamePaths.Resolve(Path.Combine(alias, "iPod")));
            MethodInfo check = typeof(DeploymentService).GetMethod("CheckDeploymentPath", BindingFlags.NonPublic | BindingFlags.Instance)!;
            check.Invoke(service, [Path.Combine(alias, "iPod", "nested", "normal.tmp"), "test root pin"]);
            Directory.Delete(alias);
            Junction(alias, external);
            try
            {
                try { check.Invoke(service, [Path.Combine(alias, "iPod", "nested", "normal.tmp"), "test retarget"]); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
                { Console.WriteLine("PASS trusted installation root cannot be retargeted during an operation"); return; }
                throw new Exception("Expected pinned-root rejection");
            }
            finally { Directory.Delete(alias); }
        }
        finally
        {
            // Every test-created link is removed non-recursively in its owning finally block.
            Directory.Delete(root, true);
        }

        async Task Establish() => await new DeploymentService(paths).BuildAndDeployAsync([a, b], stock, 75);
        async Task Redirect(string directory, Func<Task> action)
        {
            string parked = directory + ".parked";
            Directory.Move(directory, parked);
            Junction(directory, external);
            var before = Snapshot(external);
            try { await action(); Require(Same(before, Snapshot(external)), "redirected operation leaves external files unchanged"); }
            finally { Directory.Delete(directory); Directory.Move(parked, directory); }
        }
    }

    private static async Task Blocked(Func<Task> action, bool recoveryRequired = false)
    {
        try { await action(); }
        catch (DeploymentException ex)
        {
            Require(ex.ToString().Contains("Blocked deployment operation", StringComparison.Ordinal), "failure identifies the ownership check");
            Require(ex.FailureState == (recoveryRequired ? DeploymentFailureState.RecoveryRequired : DeploymentFailureState.BeforeMutation),
                "unsafe path failure state");
            return;
        }
        catch (InvalidDataException ex) when (!recoveryRequired)
        {
            Require(ex.Message.Contains("Blocked deployment operation", StringComparison.Ordinal), "failure identifies the ownership check");
            return;
        }
        throw new Exception("Expected redirected-path rejection");
    }

    private static Dictionary<string, byte[]> Snapshot(string path) =>
        Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
    private static bool Same(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out byte[]? data) && e.Value.SequenceEqual(data));
    private static void Require(bool value, string message) { if (!value) throw new Exception("FAIL " + message); }
    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Require(process.ExitCode == 0, "create junction: " + output);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);
}
