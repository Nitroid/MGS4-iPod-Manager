using iPodManager;

internal static class LoggingTests
{
    internal static void Run(string root)
    {
        string directory = Path.Combine(root, "logging");
        Directory.CreateDirectory(directory);
        string log = Path.Combine(directory, "iPodManager.log");
        string previous = Path.Combine(directory, "iPodManager.previous.log");
        AppLog.Start(log);
        if (!File.Exists(log) || File.Exists(previous) || File.ReadAllText(log).Length != 0)
            throw new Exception("FAIL logger first launch");
        Console.WriteLine("PASS logger first launch");

        AppLog.Info("Logger test started.");
        AppLog.Warn("Logger warning.");
        try { throw new InvalidOperationException("outer failure", new IOException("inner failure")); }
        catch (InvalidOperationException ex) { AppLog.Error("Logger exception.", ex); }
        Parallel.For(0, 64, i => AppLog.Info($"Concurrent entry {i:D2}."));

        string contents = File.ReadAllText(log);
        if (!contents.Contains("[INFO] Logger test started.") ||
            !contents.Contains("[WARN] Logger warning.") ||
            !contents.Contains("[ERROR] Logger exception.") ||
            !contents.Contains("System.InvalidOperationException: outer failure") ||
            !contents.Contains("System.IO.IOException: inner failure") ||
            !contents.Contains("at LoggingTests.Run"))
            throw new Exception("FAIL logger levels and exception chain");
        for (int i = 0; i < 64; i++)
            if (!contents.Contains($"[INFO] Concurrent entry {i:D2}.{Environment.NewLine}"))
                throw new Exception("FAIL logger concurrent write " + i);
        Console.WriteLine("PASS logger levels, exception chain, and concurrent writes");

        AppLog.Start(log);
        if (File.ReadAllText(previous) != contents || File.ReadAllText(log).Length != 0)
            throw new Exception("FAIL logger second launch");
        Console.WriteLine("PASS logger second launch preserves previous session");

        AppLog.Info("Second session.");
        AppLog.Start(log);
        if (!File.ReadAllText(previous).Contains("[INFO] Second session.") ||
            File.ReadAllText(previous).Contains("Logger test started.") ||
            File.ReadAllText(log).Length != 0 ||
            Directory.GetFiles(directory).Length != 2)
            throw new Exception("FAIL logger third launch");
        Console.WriteLine("PASS logger third launch replaces previous session");

        string orphanDirectory = Path.Combine(root, "orphan-log");
        Directory.CreateDirectory(orphanDirectory);
        string orphanLog = Path.Combine(orphanDirectory, "iPodManager.log");
        string orphanPrevious = Path.Combine(orphanDirectory, "iPodManager.previous.log");
        File.WriteAllText(orphanPrevious, "keep this previous session");
        AppLog.Start(orphanLog);
        if (File.ReadAllText(orphanPrevious) != "keep this previous session" ||
            !File.Exists(orphanLog) || File.ReadAllText(orphanLog).Length != 0)
            throw new Exception("FAIL logger previous without current");
        Console.WriteLine("PASS logger previous without current");

        string failureDirectory = Path.Combine(root, "rotation-failure");
        Directory.CreateDirectory(failureDirectory);
        string failureLog = Path.Combine(failureDirectory, "iPodManager.log");
        string failurePrevious = Path.Combine(failureDirectory, "iPodManager.previous.log");
        File.WriteAllText(failureLog, "current diagnostic survives");
        File.WriteAllText(failurePrevious, "older diagnostic survives");
        using (FileStream lockedPrevious = new(failurePrevious, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AppLog.Start(failureLog);
            AppLog.Info("Logging continues after rotation failure.");
        }
        if (!File.ReadAllText(failureLog).Contains("current diagnostic survives") ||
            !File.ReadAllText(failureLog).Contains("Logging continues after rotation failure.") ||
            File.ReadAllText(failurePrevious) != "older diagnostic survives")
            throw new Exception("FAIL logger rotation failure preservation");
        Console.WriteLine("PASS logger rotation failure preserves both logs");

        Directory.CreateDirectory(Path.Combine(root, "common"));
        AppLog.Start(Path.Combine(root, "common")); // An existing directory cannot be opened as the log file.
        AppLog.Info("This write must fail safely.");
        AppLog.Error("This error write must fail safely.", new IOException("logging unavailable"));
        AppLog.Start(log);
        AppLog.Info("Logging resumed.");
        string resumed = File.ReadAllText(log);
        if (!resumed.Contains("[INFO] Logging resumed.") || resumed.Contains("Logger test started."))
            throw new Exception("FAIL logger recovery after write failure");
        Console.WriteLine("PASS logger write failure and recovery");
    }
}
