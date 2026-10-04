using iPodManager;

internal static class SingleInstanceGuardTests
{
    public static void Run()
    {
        string mutexName = $@"Local\iPodManager.Tests.{Guid.NewGuid():N}";

        using (SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(mutexName))
        {
            Check(first is not null, "single-instance guard permits the first acquisition");

            using SingleInstanceGuard? duplicate = SingleInstanceGuard.TryAcquire(mutexName);
            Check(duplicate is null, "single-instance guard rejects a duplicate acquisition");
        }

        using SingleInstanceGuard? afterRelease = SingleInstanceGuard.TryAcquire(mutexName);
        Check(afterRelease is not null, "single-instance guard permits acquisition after release");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("FAIL " + message);
        }

        Console.WriteLine("PASS " + message);
    }
}
