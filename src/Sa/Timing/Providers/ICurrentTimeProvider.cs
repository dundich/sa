namespace Sa.Timing.Providers;

public interface ICurrentTimeProvider
{
    DateTimeOffset GetUtcNow();
}
