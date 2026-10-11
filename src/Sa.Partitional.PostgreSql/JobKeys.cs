using System.Security.Cryptography;
using System.Text;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Deterministic per-module identities for the background jobs of a keyed partitional module.
/// </summary>
/// <remarks>
/// Explicit <c>jobId</c>s must stay unique across the whole container (two schedules sharing a
/// <c>jobId</c> would share the keyed job registration — the Sa.Schedule rule). The ids derived
/// here are stable per (module key, role): the same key produces the same id in every process and
/// on every repeated registration, so a repeated <c>AddSaPartitional(key, ...)</c> call cannot
/// spawn a duplicate job, and two different module keys can never collide. They also never clash
/// with the fixed ids of the default (unkeyed) module
/// (<see cref="Migration.MigrationJobConstance.MigrationJobId"/> and the cleanup job id).
/// </remarks>
internal static class JobKeys
{
    public static Guid MigrationJobId(string moduleKey) => Create(moduleKey, "migration");

    public static Guid CleanupJobId(string moduleKey) => Create(moduleKey, "cleanup");

    public static string MigrationJobName(string moduleKey)
        => $"{Migration.MigrationJobConstance.MigrationDefaultJobName} [{moduleKey}]";

    public static string CleanupJobName(string moduleKey) => $"Cleanup job [{moduleKey}]";

    private static Guid Create(string moduleKey, string role)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"Sa.Partitional.PostgreSql:{role}:{moduleKey}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
