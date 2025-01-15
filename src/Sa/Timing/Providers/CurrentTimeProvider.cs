using System.Diagnostics;

namespace Sa.Timing.Providers;

public class CurrentTimeProvider : ICurrentTimeProvider
{
    [DebuggerStepThrough]
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

    public override string ToString() => $"current time: {GetUtcNow()}";
}
