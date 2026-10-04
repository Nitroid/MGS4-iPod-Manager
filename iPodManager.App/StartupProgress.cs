using System.Threading;

namespace iPodManager;

internal sealed class StartupProgress(Action<int> changed)
{
    internal const int SetupEnd = 5;
    internal const int DeploymentEnd = 45;
    internal const int DiscoveryEnd = 90;

    private int _value;
    private readonly object _reportLock = new();
    private int _reportedValue;

    internal int Value => Volatile.Read(ref _value);

    internal void SetupComplete() => Advance(SetupEnd);

    internal void Deployment(int completed, int total) =>
        Advance(Map(completed, total, SetupEnd, DeploymentEnd));

    internal void Discovery(int completed, int total) =>
        Advance(Map(completed, total, DeploymentEnd, DiscoveryEnd));

    internal void CategoriesBuilt() => Advance(92);
    internal void InitialBindingComplete() => Advance(96);
    internal void FinalLayoutPending() => Advance(99);
    internal void Ready() => Advance(100);

    private void Advance(int value)
    {
        value = Math.Clamp(value, 0, 100);
        int current = Volatile.Read(ref _value);
        while (value > current)
        {
            int observed = Interlocked.CompareExchange(ref _value, value, current);
            if (observed == current)
            {
                lock (_reportLock)
                {
                    if (value > _reportedValue)
                    {
                        _reportedValue = value;
                        changed(value);
                    }
                }
                return;
            }
            current = observed;
        }
    }

    private static int Map(int completed, int total, int start, int end)
    {
        if (total <= 0)
            return end;

        double fraction = Math.Clamp(completed, 0, total) / (double)total;
        return start + (int)Math.Floor(fraction * (end - start));
    }
}
