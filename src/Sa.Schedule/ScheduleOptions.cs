namespace Sa.Schedule;

/// <summary>
/// Настройки планировщика, обслуживаемые стандартным конвейером
/// <c>Microsoft.Extensions.Options</c>: привязка из конфигурационного раздела
/// (<see cref="IScheduleBuilder.FromConfiguration"/> в
/// <see cref="Setup.AddSaSchedule"/>) → валидация.
/// </summary>
/// <remarks>
/// Единственное содержимое — словарь <see cref="Jobs"/>: ключ — имя задачи, значение — её
/// параметры. Секции, которым не соответствует ни одна зарегистрированная задача, молча
/// игнорируются; задача, у которой нет секции в конфигурации, остаётся в том виде, в каком
/// она описана в коде.
/// <para>
/// Задача сопоставляется со секцией <c>Jobs:&lt;ключ&gt;</c> по имени —
/// <c>WithName(...)</c>, а при его отсутствии — по <c>typeof(Job).FullName</c>.
/// Переименование задачи в коде молча «отклеивает» её секцию из конфигурации.
/// </para>
/// <para>
/// Приоритет: конфигурация важнее кода — в том числе <c>Disabled: false</c> включает
/// задачу, отключённую в коде.
/// </para>
/// </remarks>
public class ScheduleOptions
{
    /// <summary>
    /// Часовой пояс по умолчанию для <see cref="JobOptions.TimeZone"/> всех задач, у которых
    /// он не задан ни в коде (<c>WithTimeZone()</c>), ни в собственном разделе
    /// <c>Jobs:&lt;имя&gt;:TimeZone</c>. Cron-выражения таких задач читаются по «стенным
    /// часам» этой зоны ("0 9 * * *" — 9:00 локально), а не UTC.
    /// Id — IANA ("Europe/Moscow") или Windows ("Russian Standard Time"); .NET 8+ понимает оба.
    /// <c>null</c> = UTC.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Параметры задач, ключевые по имени задачи. Привязывается из под-раздела <c>Jobs</c>
    /// (например, <c>Schedule:Jobs:&lt;имя задачи&gt;:&lt;свойство&gt;</c>).
    /// </summary>
    public Dictionary<string, JobOptions> Jobs { get; set; } = [];
}
