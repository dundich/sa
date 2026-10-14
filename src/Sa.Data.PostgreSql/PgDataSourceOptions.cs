namespace Sa.Data.PostgreSql;

/// <summary>
/// Настройки PostgreSQL data source, обслуживаемые стандартным конвейером
/// <c>Microsoft.Extensions.Options</c>: <c>Configure</c> → <c>PostConfigure</c> (нормализация) →
/// валидация.
/// </summary>
/// <remarks>
/// Один плоский тип, как у <c>Sa.HybridFileStorage.FileSystem</c> / <c>Sa.Data.S3</c>, — отдельной
/// «настроечной» копии нет. <see cref="PgDataSource"/> собирается прямо из этого экземпляра.
/// <para>
/// Единственная настройка — <see cref="ConnectionString"/>. Пулинг, таймауты и <c>Search Path</c>
/// задаются внутри самой строки. Если строка пуста, data source строится из уже зарегистрированного в
/// DI <see cref="NpgsqlDataSource"/> — то есть переиспользует его pool, а не создаёт второй.
/// </para>
/// </remarks>
public class PgDataSourceOptions
{
    /// <summary>
    /// Строка подключения PostgreSQL. Пустая строка — фолбэк на зарегистрированный
    /// <see cref="NpgsqlDataSource"/>.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Приводит значение к каноническому виду. Конвейер вызывает это до валидации, поэтому
    /// <see cref="PgDataSourceOptionsValidator"/> и фабрика видят уже обрезанную строку.
    /// </summary>
    public virtual void Normalize()
    {
        ConnectionString = ConnectionString?.Trim() ?? string.Empty;
    }
}
