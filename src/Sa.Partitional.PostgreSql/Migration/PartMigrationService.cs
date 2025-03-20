﻿using Sa.Extensions;
using Sa.Timing.Providers;

namespace Sa.Partitional.PostgreSql.Migration;

internal sealed class PartMigrationService(
    IPartRepository repository
    , ICurrentTimeProvider timeProvider
    , PartMigrationScheduleSettings settings
)
    : IPartMigrationService, IDisposable
{
    private int s_triggered = 0;
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken OutboxMigrated => _cts.Token;

    public void Dispose()
    {
        _cts.Dispose();
    }

    public Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
       => repository.Migrate(dates, cancellationToken);

    public async Task<int> Migrate(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref s_triggered, 1, 0) == 0)
        {
            try
            {
                DateTimeOffset now = timeProvider.GetUtcNow().StartOfDay();
                DateTimeOffset[] dates = [.. Enumerable
                    .Range(0, settings.ForwardDays)
                    .Select(i => now.AddDays(i))];

                int result = await repository.Migrate(dates, cancellationToken);
                await _cts.CancelAsync();
                return result;
            }
            finally
            {
                Interlocked.CompareExchange(ref s_triggered, 0, 1);
            }
        }
        else
        {
            do
            {
                Console.WriteLine("waiting");
                await Task.Delay(settings.WaitMigrationTimeout, cancellationToken);
            }
            while (s_triggered != 0);
        }

        return -1;
    }
}
