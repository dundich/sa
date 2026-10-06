using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Sa.Data.PostgreSql;

/// <summary>
/// Валидирует <see cref="PgDataSourceOptions"/> для конвейера options.
/// </summary>
/// <remarks>
/// Контейнерно-осознанный валидатор, потому что «пустая строка подключения» здесь — это
/// легитимный фолбэк, а не ошибка: <see cref="PgDataSource"/> строится из зарегистрированного
/// <see cref="NpgsqlDataSource"/>. Поэтому проверка на пустоту не бросает исключение, если такой
/// сервис в коллекции есть.
/// <para>
/// Намеренно <c>IValidateOptions</c>, а не <c>ValidateDataAnnotations()</c>: последний помечен
/// <c>RequiresUnreferencedCode</c> (IL2026) и ломает Native AOT, ради которого эта сборка и
/// существует. Валидация срабатывает и при чтении <c>IOptions&lt;T&gt;.Value</c> (не только при
/// <c>ValidateOnStart()</c>), поэтому «пусто и нет <see cref="NpgsqlDataSource"/>» всплывает как
/// <see cref="OptionsValidationException"/> уже на первом resolve data source.
/// </para>
/// </remarks>
internal sealed class PgDataSourceOptionsValidator : IValidateOptions<PgDataSourceOptions>
{
    private readonly IServiceProvider _serviceProvider;

    public PgDataSourceOptionsValidator(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public ValidateOptionsResult Validate(string? name, PgDataSourceOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Skip;
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            // IsService проверяет *наличие* регистрации любого lifetime, не создавая экземпляр и
            // не открывая scope, поэтому сработает и для scoped-регистрации.
            if (_serviceProvider.GetService<IServiceProviderIsService>()?.IsService(typeof(NpgsqlDataSource)) == true)
            {
                return ValidateOptionsResult.Success;
            }

            return ValidateOptionsResult.Fail(
                "PgDataSourceOptions:ConnectionString cannot be empty unless an NpgsqlDataSource is " +
                "registered in the service collection.");
        }

        // NpgsqlDataSource.Create() разбирает строку тем же NpgsqlConnectionStringBuilder, поэтому
        // синтаксическая проверка здесь не даст ложных срабатываний, а опечатку в «Host=» всплывёт
        // ранним OptionsValidationException, а не голым FormatException посреди первого запроса.
        try
        {
            _ = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        }
        catch (Exception ex)
        {
            return ValidateOptionsResult.Fail(
                $"PgDataSourceOptions:ConnectionString is not a valid connection string: {ex.Message}");
        }

        return ValidateOptionsResult.Success;
    }
}
