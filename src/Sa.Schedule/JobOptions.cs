namespace Sa.Schedule;

/// <summary>
/// Параметры одной задачи из <see cref="ScheduleOptions.Jobs"/>.
/// Все свойства nullable: «свойства нет в конфигурации» — значит «не переопределяется»,
/// задача остаётся в той конфигурации, в какой она описана в коде.
/// </summary>
public class JobOptions
{
    /// <summary>
    /// Включить или отключить задачу. Переопределяет <c>Disable()</c> из кода в обе
    /// стороны: <c>false</c> включает задачу, отключённую в коде.
    /// </summary>
    public bool? Disabled { get; set; }

    /// <summary>
    /// Запустить задачу сразу, на первом тике планировщика, не дожидаясь тайминга.
    /// Переопределяет <c>StartImmediate()</c> из кода в обе стороны: <c>false</c> отменяет
    /// immediate-запуск, включённый в коде.
    /// </summary>
    public bool? Immediate { get; set; }

    /// <summary>
    /// Выполнить задачу ровно один раз: после первого прогона планировщик её больше
    /// не запускает. Переопределяет <c>RunOnce()</c> из кода в обе стороны: <c>false</c>
    /// возвращает задачу к периодическому расписанию.
    /// </summary>
    public bool? IsRunOnce { get; set; }

    /// <summary>
    /// Cron-выражение (стандартные 5 полей) — заменяет любой тайминг из кода.
    /// Взаимоисключается с <see cref="Every"/>: заданы оба — ошибка валидации.
    /// Пробелы по краям допустимы — парсер тримит выражение.
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>
    /// Интервал выполнения — заменяет любой тайминг из кода. Формат <c>TimeSpan</c>
    /// константной формы, например <c>00:05:00</c>. Должен быть больше нуля.
    /// </summary>
    public TimeSpan? Every { get; set; }

    /// <summary>
    /// Часовой пояс, в котором читается <see cref="Cron"/>: cron-выражение приобретает смысл
    /// «стенных часов» этой зоны ("0 9 * * *" — это 9:00 там, где вы живёте), а не UTC.
    /// Заменяет <c>WithTimeZone()</c> из кода; если у задачи нет своего <c>TimeZone</c>,
    /// применяется <see cref="ScheduleOptions.TimeZone"/> — общий для всего планировщика.
    /// Id — IANA ("Europe/Moscow") или Windows ("Russian Standard Time"); .NET 8+ понимает
    /// оба. Интервалы (<see cref="Every"/>) смена часового пояса не касается.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Задержка перед первым запуском. Формат <c>TimeSpan</c> константной формы, например
    /// <c>00:00:30</c>. Не может быть отрицательной.
    /// </summary>
    public TimeSpan? InitialDelay { get; set; }

    /// <summary>
    /// Сколько слотов задачи активно работают в любой момент. Не может быть отрицательным.
    /// Если значение больше <see cref="MaxConcurrency"/>, планировщик обрезает его до
    /// <see cref="MaxConcurrency"/>.
    /// </summary>
    public int? ConcurrencyLimit { get; set; }

    /// <summary>
    /// Абсолютный максимум зарезервированных слотов задачи. Должен быть не меньше 1.
    /// </summary>
    public int? MaxConcurrency { get; set; }
}
