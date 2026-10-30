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
            // IsService/IsKeyedService проверяют *наличие* регистрации любого lifetime, не
            // создавая экземпляр и не открывая scope, поэтому сработают и для scoped-регистрации.
            // (GetKeyedService нельзя: он *создал бы* NpgsqlDataSource и открыл pool ради проверки.)
            //
            // For the default (unnamed) instance the fallback is an unkeyed NpgsqlDataSource; for a
            // named/keyed instance it is a keyed NpgsqlDataSource registered with the same key. The
            // `name` argument carries the key (null/empty = default), so the check follows it.
            var isDefault = string.IsNullOrEmpty(name);

            var hasBorrowed = isDefault
                ? _serviceProvider.GetService<IServiceProviderIsService>()?.IsService(typeof(NpgsqlDataSource)) == true
                : _serviceProvider.GetService<IServiceProviderIsKeyedService>()?.IsKeyedService(typeof(NpgsqlDataSource), name) == true;

            if (hasBorrowed)
            {
                return ValidateOptionsResult.Success;
            }

            return ValidateOptionsResult.Fail(isDefault
                ? "PgDataSourceOptions:ConnectionString cannot be empty unless an NpgsqlDataSource is " +
                    "registered in the service collection."
                : $"PgDataSourceOptions[{name}]:ConnectionString cannot be empty unless an NpgsqlDataSource is " +
                    $"registered with key \"{name}\" in the service collection.");
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
