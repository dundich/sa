using System.Globalization;

namespace Sa.Data.TempFolder.Naming;

/// <summary>
/// Date-based naming: <c>{FolderPrefix}{current local time shaped by TempFolderOptions.FolderNameFormat}</c> —
/// e.g. <c>up_2026-10-08</c>, or <c>up_2026/10/08/14</c> with the format <c>yyyy/MM/dd/HH</c>.
/// Selected by <see cref="TempFolderNamingKind.Date"/> or a code override.
/// </summary>
/// <remarks>
/// The clock is the container's <see cref="TimeProvider"/> and the culture is invariant, so a name
/// never depends on the machine's locale or time zone setup — tests virtualise the date with a
/// manual clock. The prefix lands on the <b>first</b> segment of the date path, which keeps the
/// age-based cleanup's prefix filter matching the hierarchy this strategy created.
/// <para>
/// Every call inside one format bucket returns the same path (<c>CreateDirectory</c> is
/// idempotent): date folders are meant to be shared by everything landing in that bucket, and the
/// cleanup then sheds them level by level once they grow old.
/// </para>
/// </remarks>
public sealed class DateFolderNameStrategy : IFolderNameStrategy
{
    private readonly TimeProvider _timeProvider;

    /// <param name="timeProvider">
    /// The clock the date is read from — usually the container's. Optional; when omitted or
    /// <see langword="null"/> it falls back to <see cref="TimeProvider.System"/>.
    /// </param>
    public DateFolderNameStrategy(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The format and prefix produced an unusable name — defensive; options validation rejects
    /// such combinations before the first call.
    /// </exception>
    public string CreateFolderName(TempFolderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var utc = _timeProvider.GetUtcNow().UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, _timeProvider.LocalTimeZone);
        var stamp = local.ToString(options.FolderNameFormat, CultureInfo.InvariantCulture);

        var name = $"{options.FolderPrefix}{stamp}";

        if (name.Length == 0 || name is "." || PathGuard.HasUnsafeCharacters(name) || Path.IsPathRooted(name))
        {
            throw new InvalidOperationException(
                $"The Date naming strategy produced an unusable folder name '{name}' " +
                $"(prefix '{options.FolderPrefix}', format '{options.FolderNameFormat}').");
        }

        return name;
    }
}
