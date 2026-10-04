using System.Diagnostics;
using iPodManager;

internal static class GamePathsTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "mgs4-ipod-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            string game = Path.Combine(root, "METAL GEAR SOLID 4", "MGS4");
            string ipod = CreateValidLayout(game);
            Check(GamePaths.Resolve(ipod).Mgs4Root == Path.GetFullPath(game),
                "valid MGS4 iPod install resolves from executable directory");

            foreach (string input in new[] { ipod.ToUpperInvariant(), ipod + Path.DirectorySeparatorChar,
                @"\\?\" + ipod })
                Check(GamePaths.Resolve(input).Mgs4Root.Equals(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase) ||
                    GamePaths.Resolve(input).Mgs4Root.Equals(@"\\?\" + Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase),
                    "casing, trailing separator and extended local path resolve");

            string alternate = Path.Combine(AppContext.BaseDirectory, "alternate Steam Library " + Guid.NewGuid().ToString("N"));
            try
            {
                string alternateGame = Path.Combine(alternate, "steamapps", "common", "METAL GEAR SOLID 4", "MGS4");
                Check(GamePaths.Resolve(CreateValidLayout(alternateGame)).Mgs4Root == alternateGame,
                    "custom Steam library path with spaces resolves independently of drive");
                Console.WriteLine($"PASS installation roots tested on {Path.GetPathRoot(root)} and {Path.GetPathRoot(alternate)}");
            }
            finally { if (Directory.Exists(alternate)) Directory.Delete(alternate, recursive: true); }

            string alias = Path.Combine(root, "Steam library junction");
            Junction(alias, Path.Combine(root, "METAL GEAR SOLID 4"));
            try
            {
                Check(GamePaths.Resolve(Path.Combine(alias, "MGS4", "iPod")).Mgs4Root == Path.Combine(alias, "MGS4"),
                    "legitimate redirected Steam-library ancestor remains supported");
            }
            finally { Directory.Delete(alias); }

            foreach (string required in new[] { "common/bank/default", "scripts" })
            {
                string path = Path.Combine(game, required.Replace('/', Path.DirectorySeparatorChar));
                string saved = path + ".saved";
                Directory.Move(path, saved);
                try
                {
                    var diagnostics = new List<string>();
                    try { GamePaths.Resolve(ipod, diagnostics.Add); throw new Exception("FAIL missing directory accepted"); }
                    catch (DirectoryNotFoundException) { }
                    Check(diagnostics.Any(line => line.Contains($"input='{path}' result=FAIL")) &&
                        diagnostics.Any(line => line.Contains("api=File.GetAttributes") && (line.Contains("Win32_error=2") || line.Contains("Win32_error=3"))),
                        "missing structural directory is diagnosed without relaxing validation: " + required);
                    StartupLocationFailure expectedFailure = required == "scripts" ?
                        StartupLocationFailure.MissingScriptsDirectory : StartupLocationFailure.MissingGameData;
                    StartupLocationResult result = GamePaths.ValidateStartup(ipod);
                    Check(result.Failure == expectedFailure && result.Paths == null &&
                        diagnostics.Any(line => line.Contains("failure=" + expectedFailure)),
                        "startup categorizes missing " + required + " and logs its final reason");
                    Check(diagnostics.Count(line => line.Contains("startup.location check=")) == 5,
                        "all checks are logged even after an earlier check fails");
                }
                finally { Directory.Move(saved, path); }
            }

            string freshDbm = Path.Combine(game, "common", "dbm");
            Directory.Delete(freshDbm);
            try
            {
                var diagnostics = new List<string>();
                Check(GamePaths.ValidateStartup(ipod, diagnostics.Add).Failure == StartupLocationFailure.Valid &&
                    GamePaths.Resolve(ipod).CommonDbm == freshDbm,
                    "fresh installation without generated DBM directory is accepted");
                Check(!Directory.Exists(freshDbm) && diagnostics.Any(line =>
                    line.Contains("deployment_destination=common_dbm") && line.Contains("required_at_startup=false")),
                    "startup diagnoses optional DBM destination without creating application state");
            }
            finally { Directory.CreateDirectory(freshDbm); }

            string external = Path.Combine(root, "redirect target");
            Directory.CreateDirectory(external);
            string redirectedDirectory = Path.Combine(game, "common", "bank", "default");
            Directory.Move(redirectedDirectory, redirectedDirectory + ".saved");
            Junction(redirectedDirectory, external);
            try
            {
                var diagnostics = new List<string>();
                _ = GamePaths.Resolve(ipod, diagnostics.Add);
                Check(diagnostics.Any(line => line.Contains($"input='{redirectedDirectory}' value=") && line.Contains("ReparsePoint")),
                    "descendant junction is diagnosed; existing deployment ownership tests enforce rejection before mutation");
            }
            finally { Directory.Delete(redirectedDirectory); Directory.Move(redirectedDirectory + ".saved", redirectedDirectory); }

            string startupLog = Path.Combine(root, "startup-diagnostics", "iPodManager.log");
            AppLog.Start(startupLog);
            _ = GamePaths.Resolve(ipod, AppLog.Info);
            Check(File.ReadAllText(startupLog).Contains("startup.location result=PASS"),
                "existing logger records location checks before application state exists");

            Check(GamePaths.ValidateStartup(ipod).Failure == StartupLocationFailure.Valid &&
                GamePaths.ValidateStartup(ipod).Paths != null,
                "startup result is valid for a complete installation");
            Check(GamePaths.ValidateStartup(Path.Combine(root, "wrong location")).Failure == StartupLocationFailure.WrongDirectory,
                "wrong application directory is categorized without throwing");
            string missingIpod = ipod + ".saved";
            Directory.Move(ipod, missingIpod);
            try
            {
                Check(GamePaths.ValidateStartup(ipod).Failure == StartupLocationFailure.WrongDirectory,
                    "original iPod directory existence check remains enforced");
            }
            finally { Directory.Move(missingIpod, ipod); }
            string development = Path.Combine(ipod, "dev", "project", "bin", "Release");
            Directory.CreateDirectory(development);
            Check(GamePaths.ValidateStartup(development).Failure == StartupLocationFailure.Valid,
                "existing deliberate development layout remains supported");

            var verificationDiagnostics = new List<string>();
            StartupLocationResult verification = GamePaths.ValidateStartup("invalid\0path", verificationDiagnostics.Add);
            Check(verification.Failure == StartupLocationFailure.VerificationError && verification.Error is ArgumentException &&
                verificationDiagnostics.Any(line => line.Contains("failure=VerificationError")),
                "path normalization error is categorized and preserves full diagnostics");

            var messages = new Dictionary<StartupLocationFailure, string>
            {
                [StartupLocationFailure.WrongDirectory] = "iPod Manager must be run from the game's iPod folder.\n\nExpected location:\n...\\METAL GEAR SOLID 4\\MGS4\\iPod",
                [StartupLocationFailure.MissingGameExecutable] = "iPod Manager could not find the MGS4 game executable.\n\nMake sure iPod Manager is installed inside the correct MGS4 installation.",
                [StartupLocationFailure.MissingGameData] = "The MGS4 installation appears to be incomplete or unsupported.\n\nOne or more required game directories could not be found.",
                [StartupLocationFailure.MissingScriptsDirectory] = "The MGS4 scripts directory could not be found.\n\nMake sure the mod was extracted into the MGS4 installation correctly.",
                [StartupLocationFailure.VerificationError] = "iPod Manager could not verify the MGS4 installation.\n\nCheck iPodManager.log for diagnostic details."
            };
            foreach (var expected in messages)
            {
                var result = new StartupLocationResult(null, expected.Key, new IOException("technical source path/ID must not be displayed"));
                Check(result.Message == expected.Value && !result.Message.Contains("technical source"),
                    "startup UI maps " + expected.Key + " to its fixed actionable message");
            }

            string originalWorkingDirectory = Environment.CurrentDirectory;
            string otherWorkingDirectory = Path.Combine(root, "other-working-directory");
            Directory.CreateDirectory(otherWorkingDirectory);
            try
            {
                Environment.CurrentDirectory = otherWorkingDirectory;
                Check(GamePaths.Resolve(ipod).Mgs4Root == Path.GetFullPath(game),
                    "valid install ignores the process working directory");
            }
            finally
            {
                Environment.CurrentDirectory = originalWorkingDirectory;
            }

            string arbitrary = Path.Combine(root, "arbitrary");
            Directory.CreateDirectory(arbitrary);
            string[] before = Directory.GetFileSystemEntries(arbitrary, "*", SearchOption.AllDirectories);
            ExpectInvalid(arbitrary, "arbitrary executable directory is rejected");
            Check(before.SequenceEqual(Directory.GetFileSystemEntries(arbitrary, "*", SearchOption.AllDirectories)),
                "invalid resolution creates no deployment, cache, preference, or inventory state");

            string fakeGame = Path.Combine(root, "fake", "MGS4");
            string fakeIpod = Path.Combine(fakeGame, "iPod");
            Directory.CreateDirectory(fakeIpod);
            Directory.CreateDirectory(Path.Combine(fakeGame, "common", "bank", "default"));
            Directory.CreateDirectory(Path.Combine(fakeGame, "common", "dbm"));
            Directory.CreateDirectory(Path.Combine(fakeGame, "scripts"));
            var failedChecks = new List<string>();
            try { GamePaths.Resolve(fakeIpod, failedChecks.Add); throw new Exception("FAIL missing game accepted"); }
            catch (DirectoryNotFoundException exception) { AppLog.Error("startup.location FAILED", exception); }
            Check(failedChecks.Any(line => line.Contains("check=mgs4.exe") && line.Contains("result=FAIL")),
                "missing game executable has a specific diagnostic");
            Check(File.ReadAllText(startupLog).Contains("System.IO.DirectoryNotFoundException"),
                "startup failure keeps full exception diagnostics");
            Check(GamePaths.ValidateStartup(fakeIpod).Failure == StartupLocationFailure.MissingGameExecutable &&
                failedChecks.Any(line => line.Contains("failure=MissingGameExecutable")),
                "startup categorizes missing game executable and logs its final reason");
            Directory.Delete(Path.Combine(fakeGame, "scripts"));
            Check(GamePaths.ValidateStartup(fakeIpod).Failure == StartupLocationFailure.MissingGameExecutable,
                "multiple missing entries deterministically prioritize the game executable");
            Directory.CreateDirectory(Path.Combine(fakeGame, "scripts"));
            ExpectInvalid(fakeIpod, "similar-looking MGS4 iPod tree without the game executable is rejected");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0, "create junction without symlink privileges: " + output.Trim());
    }

    private static string CreateValidLayout(string game)
    {
        string ipod = Path.Combine(game, "iPod");
        Directory.CreateDirectory(ipod);
        Directory.CreateDirectory(Path.Combine(game, "common", "bank", "default"));
        Directory.CreateDirectory(Path.Combine(game, "common", "dbm"));
        Directory.CreateDirectory(Path.Combine(game, "scripts"));
        File.WriteAllBytes(Path.Combine(game, "mgs4.exe"), []);
        return ipod;
    }

    private static void ExpectInvalid(string directory, string message)
    {
        try
        {
            _ = GamePaths.Resolve(directory);
            throw new Exception("FAIL " + message);
        }
        catch (DirectoryNotFoundException)
        {
            Console.WriteLine("PASS " + message);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }
}
