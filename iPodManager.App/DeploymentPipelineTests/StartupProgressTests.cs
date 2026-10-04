using iPodManager;

internal static class StartupProgressTests
{
    internal static void Run()
    {
        var values = new List<int>();
        var progress = new StartupProgress(values.Add);

        Check(progress.Value == 0, "startup progress begins at zero");
        progress.SetupComplete();
        progress.Deployment(5, 10);
        Check(progress.Value == 25, "startup deployment work maps into its stage");
        progress.Deployment(3, 10);
        Check(progress.Value == 25, "startup progress ignores out-of-order callbacks");
        progress.Deployment(10, 10);
        progress.Discovery(5, 10);
        Check(progress.Value == 67, "startup source work maps into its stage");
        progress.Discovery(10, 10);
        progress.CategoriesBuilt();
        progress.InitialBindingComplete();
        progress.FinalLayoutPending();
        progress.Ready();
        Check(progress.Value == 100, "startup completion reaches 100 percent");
        Check(values.SequenceEqual(values.Order()) && values.All(value => value is >= 0 and <= 100),
            "startup progress is monotonic and bounded");

        var empty = new StartupProgress(_ => { });
        empty.SetupComplete();
        empty.Deployment(0, 0);
        Check(empty.Value == StartupProgress.DeploymentEnd,
            "zero deployment records complete the deployment stage");
        empty.Discovery(0, 0);
        Check(empty.Value == StartupProgress.DiscoveryEnd,
            "zero source files complete the discovery stage");

        var cacheHeavy = new StartupProgress(_ => { });
        cacheHeavy.SetupComplete();
        for (int completed = 1; completed <= 951; completed++)
            cacheHeavy.Deployment(completed, 951);
        for (int completed = 1; completed <= 980; completed++)
            cacheHeavy.Discovery(completed, 980);
        Check(cacheHeavy.Value == StartupProgress.DiscoveryEnd,
            "cache hits count as completed startup work");

        var parallelValues = new List<int>();
        var parallelLock = new object();
        var parallel = new StartupProgress(value =>
        {
            lock (parallelLock) parallelValues.Add(value);
        });
        Parallel.For(0, 1000, completed => parallel.Deployment(completed, 999));
        Check(parallelValues.SequenceEqual(parallelValues.Order()),
            "parallel startup callbacks are emitted monotonically");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }
}
