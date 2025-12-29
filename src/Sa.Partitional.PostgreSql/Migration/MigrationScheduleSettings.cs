namespace Sa.Partitional.PostgreSql;

public sealed class MigrationScheduleSettings
{
    public int ForwardDays { get; set; } = 2;

    public bool AsBackgroundJob { get; set; } = false;

    public string? MigrationJobName { get; set; }

    public TimeSpan ExecutionInterval { get; set; } = TimeSpan
        .FromHours(4)
        .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

    public TimeSpan WaitMigrationTimeout { get; set; } = TimeSpan.FromSeconds(3);
}
