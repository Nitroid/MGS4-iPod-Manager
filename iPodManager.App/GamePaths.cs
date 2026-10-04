using System;
using System.IO;
using System.Security;

namespace iPodManager;

public sealed class GamePaths
{
    public string Mgs4Root { get; }
    public string IpodRoot { get; }
    public string PodcastContent { get; }
    public string CustomContent { get; }
    public string Cache { get; }
    public string SourceHashCache { get; }
    public string DeploymentArtifactHashCache { get; }
    public string Preferences { get; }
    public string DefaultBanks { get; }
    public string Tools { get; }
    public string CommonDbm { get; }
    public string Scripts { get; }
    public string DeploymentManifest { get; }
    public string ManagedDeploymentInventory { get; }
    public string DeploymentTransactionDirectory { get; }

    private GamePaths(string mgs4Root)
    {
        Mgs4Root = Path.GetFullPath(mgs4Root);
        IpodRoot = Path.Combine(Mgs4Root, "iPod");
        PodcastContent = Path.Combine(IpodRoot, "content", "podcasts");
        CustomContent = Path.Combine(IpodRoot, "content", "custom");
        Cache = Path.Combine(IpodRoot, "cache");
        SourceHashCache = Path.Combine(Cache, "source-hashes.json");
        DeploymentArtifactHashCache = Path.Combine(Cache, "deployment-artifact-hashes.json");
        Preferences = Path.Combine(IpodRoot, "preferences.json");
        DefaultBanks = Path.Combine(Mgs4Root, "common", "bank", "default");
        Tools = Path.Combine(IpodRoot, "tools");
        CommonDbm = Path.Combine(Mgs4Root, "common", "dbm");
        Scripts = Path.Combine(Mgs4Root, "scripts");
        DeploymentManifest = Path.Combine(IpodRoot, "deployment.bin");
        ManagedDeploymentInventory = Path.Combine(IpodRoot, "deployment-managed-files.json");
        DeploymentTransactionDirectory = Path.Combine(IpodRoot, ".deployment-transaction");
    }

    public static GamePaths Resolve(string executableDirectory, Action<string>? diagnostic = null)
    {
        StartupLocationResult result = ValidateStartup(executableDirectory, diagnostic);
        return result.Paths ?? throw (result.Error ?? new DirectoryNotFoundException(result.Message));
    }

    internal static StartupLocationResult ValidateStartup(string executableDirectory, Action<string>? diagnostic = null)
    {
        try
        {
            return ResolveStartup(executableDirectory, diagnostic);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or SecurityException)
        {
            diagnostic?.Invoke($"startup.location failure={StartupLocationFailure.VerificationError} input='{executableDirectory}' error='{exception}'");
            return new(null, StartupLocationFailure.VerificationError, exception);
        }
    }

    private static StartupLocationResult ResolveStartup(string executableDirectory, Action<string>? diagnostic)
    {
        diagnostic?.Invoke($"startup.location input_directory='{executableDirectory}'");
        string start = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        diagnostic?.Invoke($"startup.location resolved_executable_directory='{start}' normalization=Path.GetFullPath/TrimEndingDirectorySeparator final_path=not_used");

        // Installed layout: the executable is directly beneath MGS4\iPod.
        bool installedLayout = string.Equals(Path.GetFileName(start), "iPod", StringComparison.OrdinalIgnoreCase);
        diagnostic?.Invoke($"startup.location check=installed_directory_name comparison=OrdinalIgnoreCase result={(installedLayout ? "PASS" : "FAIL")}");
        if (installedLayout)
            return Validate(new GamePaths(Directory.GetParent(start)!.FullName), diagnostic);

        // Deliberate development layout: MGS4\iPod\dev\<project>[\bin\...].
        var current = new DirectoryInfo(start);
        for (int depth = 0; current != null && depth <= 8; depth++, current = current.Parent)
        {
            if (string.Equals(current.Name, "dev", StringComparison.OrdinalIgnoreCase) &&
                current.Parent != null &&
                string.Equals(current.Parent.Name, "iPod", StringComparison.OrdinalIgnoreCase) &&
                current.Parent.Parent != null)
            {
                diagnostic?.Invoke($"startup.location check=development_layout result=PASS depth={depth}");
                return Validate(new GamePaths(current.Parent.Parent.FullName), diagnostic);
            }
        }

        diagnostic?.Invoke("startup.location check=development_layout result=FAIL candidate_mgs4_root=unresolved; no installed or development layout matched");
        diagnostic?.Invoke($"startup.location failure={StartupLocationFailure.WrongDirectory}");
        return new(null, StartupLocationFailure.WrongDirectory);
    }

    private static StartupLocationResult Validate(GamePaths paths, Action<string>? diagnostic)
    {
        diagnostic?.Invoke($"startup.location candidate_mgs4_root='{paths.Mgs4Root}' expected_ipod_directory='{paths.IpodRoot}' final_path=not_used");
        Exception? verificationError = null;
        bool game = Check("mgs4.exe", Path.Combine(paths.Mgs4Root, "mgs4.exe"), file: true);
        bool ipod = Check("iPod", paths.IpodRoot);
        bool banks = Check("default_banks", paths.DefaultBanks);
        // Generated DBMs are installed here by sync; a fresh game need not contain this directory.
        diagnostic?.Invoke($"startup.location deployment_destination=common_dbm input='{paths.CommonDbm}' exists={Directory.Exists(paths.CommonDbm)} required_at_startup=false");
        bool scripts = Check("scripts", paths.Scripts);
        StartupLocationFailure failure = game && ipod && banks && scripts ? StartupLocationFailure.Valid :
            verificationError != null ? StartupLocationFailure.VerificationError :
            !game ? StartupLocationFailure.MissingGameExecutable :
            !ipod ? StartupLocationFailure.WrongDirectory :
            !banks ? StartupLocationFailure.MissingGameData : StartupLocationFailure.MissingScriptsDirectory;
        if (failure == StartupLocationFailure.Valid)
        {
            diagnostic?.Invoke("startup.location result=PASS");
            return new(paths, failure);
        }
        diagnostic?.Invoke($"startup.location failure={failure}");
        return new(null, failure,
            new DirectoryNotFoundException($"The resolved MGS4 layout is incomplete: {paths.Mgs4Root}; failure={failure}", verificationError));

        bool Check(string name, string path, bool file = false)
        {
            bool exists = file ? File.Exists(path) : Directory.Exists(path);
            diagnostic?.Invoke($"startup.location check={name} api={(file ? "File.Exists" : "Directory.Exists")} input='{path}' result={(exists ? "PASS" : "FAIL")}");
            if (diagnostic != null || !exists)
            {
                // Exists suppresses filesystem errors. Diagnose a failure without changing the acceptance rule.
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    diagnostic?.Invoke($"startup.location attributes input='{path}' value={attributes}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    ArgumentException or SecurityException)
                {
                    diagnostic?.Invoke($"startup.location api=File.GetAttributes input='{path}' HRESULT=0x{exception.HResult:X8} Win32_error={((exception.HResult & 0xFFFF0000) == 0x80070000 ? (exception.HResult & 0xFFFF).ToString() : "not_available")} error='{exception.Message}'");
                    if (!exists && exception is not (FileNotFoundException or DirectoryNotFoundException))
                        verificationError ??= exception;
                }
            }
            return exists;
        }
    }
}

internal enum StartupLocationFailure
{
    Valid,
    WrongDirectory,
    MissingGameExecutable,
    MissingGameData,
    MissingScriptsDirectory,
    VerificationError
}

internal readonly record struct StartupLocationResult(
    GamePaths? Paths, StartupLocationFailure Failure, Exception? Error = null)
{
    internal string Message => Failure switch
    {
        StartupLocationFailure.WrongDirectory =>
            "iPod Manager must be run from the game's iPod folder.\n\nExpected location:\n...\\METAL GEAR SOLID 4\\MGS4\\iPod",
        StartupLocationFailure.MissingGameExecutable =>
            "iPod Manager could not find the MGS4 game executable.\n\nMake sure iPod Manager is installed inside the correct MGS4 installation.",
        StartupLocationFailure.MissingGameData =>
            "The MGS4 installation appears to be incomplete or unsupported.\n\nOne or more required game directories could not be found.",
        StartupLocationFailure.MissingScriptsDirectory =>
            "The MGS4 scripts directory could not be found.\n\nMake sure the mod was extracted into the MGS4 installation correctly.",
        StartupLocationFailure.VerificationError =>
            "iPod Manager could not verify the MGS4 installation.\n\nCheck iPodManager.log for diagnostic details.",
        _ => string.Empty
    };
}
