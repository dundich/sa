using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Sa.Data.S3;

public static class Setup
{
    /// <summary>
    /// Имя именованного клиента. Задано явно, а не оставлено на волю встроенному генератору имён
    /// typed-клиентов: <see cref="HttpClientFactoryOptions"/> — единственное место, где можно задать
    /// <c>HandlerLifetime</c> с IServiceProvider под рукой (см. <see cref="S3HandlerLifetimeOptions"/>),
    /// а фильтровать эту опцию можно только по имени клиента.
    /// </summary>
    internal const string ClientName = "Sa.Data.S3.IS3BucketClient";

    /// <summary>
    /// Имя конвейера устойчивости, под которым <c>AddStandardResilienceHandler()</c> регистрирует
    /// свои опции. Запоминается при регистрации, потому что <c>TotalRequestTimeout</c> задаётся
    /// собственным <see cref="IConfigureOptions{TOptions}"/>, которому нужно точное имя, а выводить
    /// это имя угадыванием («имя клиента + суффикс») — значит заложить невидимую связь с чужим
    /// соглашением об именовании.
    /// </summary>
    internal static string? ResiliencePipelineName { get; private set; }

    /// <summary>
    /// Регистрирует клиент S3 через стандартный конвейер настроек.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configure">
    /// Необязательный колбэк, получающий <see cref="OptionsBuilder{TOptions}"/> для этого клиента,
    /// чтобы конфигурация шла через стандартные <c>Configure</c> / <c>PostConfigure</c> /
    /// <c>Validate</c>, а не через отдельный перегруженный метод.
    /// </param>
    /// <param name="configSectionPath">
    /// Необязательная секция конфигурации, из которой биндятся настройки, например
    /// <c>"S3"</c>. Биндится первой, поэтому <c>Configure</c> из <paramref name="configure"/> имеет
    /// последнее слово.
    /// </param>
    /// <returns>Та же коллекция <see cref="IServiceCollection"/> с добавленными сервисами.</returns>
    /// <exception cref="ArgumentNullException">Выбрасывается, если <paramref name="services"/> — <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Выбрасывается, если клиент S3 уже зарегистрирован в этой коллекции.
    /// </exception>
    /// <remarks>
    /// Конвейер опций выполняется в фиксированном порядке: <c>Configure</c> (сырые значения) →
    /// <c>PostConfigure</c> (нормализация этим методом) → <c>PostConfigure</c> из
    /// <paramref name="configure"/> → валидация. Поэтому валидация видит нормализованный
    /// <see cref="S3BucketClientSetupOptions.Endpoint"/>, а <c>ValidateOnStart()</c> превращает
    /// неверную конфигурацию в <see cref="OptionsValidationException"/> на старте хоста, а не в
    /// <see cref="UriFormatException"/> посреди первой загрузки.
    /// <para>
    /// Колбэк вызывается последним, поэтому его <c>Configure</c> выполняется после биндинга секции,
    /// а его <c>Validate</c> дополняет — а не заменяет — встроенные проверки.
    /// </para>
    /// <para>
    /// Настройки читаются один раз — когда клиент создаётся фабрикой <c>HttpClientFactory</c>. Они не
    /// перечитываются на каждый запрос и не участвуют в hot-reload: пересоздание клиента означало бы и
    /// пересоздание HTTP-пула.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaS3BucketClient(
        this IServiceCollection services,
        Action<OptionsBuilder<S3BucketClientSetupOptions>>? configure = null,
        string? configSectionPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Клиент владеет единственным экземпляром S3BucketClientSetupOptions. Второй вызов добавил бы
        // поверх него ещё один IConfigureOptions, и оба Configure-колбэка применились бы к одному
        // объекту — молчаливо склеенные настройки вместо двух клиентов. Падаем здесь, где причина
        // видна сразу.
        if (services.Any(d => d.ServiceType == typeof(S3BucketClientRegistration)))
        {
            throw new InvalidOperationException(
                "AddSaS3BucketClient has already been registered in this service collection. " +
                "The second call would configure the same unnamed S3BucketClientSetupOptions instance twice, " +
                "and both Configure callbacks would apply, so the client would silently use merged settings. " +
                "Register only one S3 bucket client per service collection.");
        }

        services.AddSingleton(new S3BucketClientRegistration());

        var builder = services.AddOptions<S3BucketClientSetupOptions>();

        if (configSectionPath is not null)
        {
            builder.BindConfiguration(configSectionPath);
        }

        // Нормализация выполняется до валидации, поэтому проверки видят канонический вид значений.
        builder.PostConfigure(static options => options.Normalize());

        builder.ValidateOnStart();

        // IValidateOptions вместо ValidateDataAnnotations(): последний помечен RequiresUnreferencedCode
        // (IL2026) и ломает Native AOT.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<S3BucketClientSetupOptions>, S3BucketClientSetupOptionsValidator>());

        // Вызывается последним, чтобы Configure пользователя отработал после биндинга секции.
        configure?.Invoke(builder);

        services.AddSaS3BucketClientCore(sp => sp.GetRequiredService<IOptions<S3BucketClientSetupOptions>>().Value);

        return services;
    }

    /// <summary>
    /// Регистрирует HTTP-обвязку клиента (<c>IS3BucketClient</c>, пул соединений, resilience,
    /// время жизни handler'а) поверх произвольной ленивой фабрики настроек.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="settingsFactory">
    /// Фабрика настроек. Вызывается при создании клиента, а не при регистрации, поэтому настройки
    /// могут приходить из конвейера <c>Microsoft.Extensions.Options</c> — на момент регистрации их
    /// значения ещё не материализованы и прочитать их нельзя.
    /// </param>
    /// <returns>Та же коллекция <see cref="IServiceCollection"/> с добавленными сервисами.</returns>
    /// <remarks>
    /// Единственная точка, где собирается HTTP-обвязка: и <see cref="AddSaS3BucketClient"/>, и
    /// провайдер <c>Sa.HybridFileStorage.S3</c> (у него свой тип опций) идут через неё, поэтому
    /// таймауты и пул соединений настраиваются одинаково в обоих случаях.
    /// </remarks>
    internal static IServiceCollection AddSaS3BucketClientCore(
        this IServiceCollection services,
        Func<IServiceProvider, S3BucketClientSetupOptions> settingsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settingsFactory);

        // Опции сами наследуют S3BucketSettings, поэтому регистрируется тот же объект — без
        // проекции и без риска потерять при переносе какое-нибудь поле.
        services.TryAddSingleton<S3BucketSettings>(sp => settingsFactory(sp));

        var builder = services.AddHttpClient<IS3BucketClient, S3BucketClient>(ClientName, (sp, client) =>
        {
            var settings = settingsFactory(sp);

            // Фактически этим значением управляет не HttpClient.Timeout, а стратегия
            // TotalRequestTimeout из AddStandardResilienceHandler: та принудительно ставит
            // Timeout.InfiniteTimeSpan, чтобы дедлайн задавал Polly, а не рантайм. Общая таймаутная
            // настройка задаётся в обоих местах намеренно — иначе «куда смотреть» придётся по догадке.
            client.Timeout = settings.TotalRequestTimeout;
            client.BaseAddress = new Uri(settings.Endpoint);
        });

        builder.ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
        {
            PooledConnectionLifetime = settingsFactory(sp).ConnectionPoolLifetime
        });

        // Настройки здесь ленивые, а перегруженный AddStandardResilienceHandler(Action<...>) их
        // колбэку не передаёт, поэтому total-timeout задаётся собственным IConfigureOptions,
        // зарегистрированным ПОСЛЕ библиотечного: конвейер опций выполняет конфигураторы по порядку
        // регистрации, значит наше значение перекрывает стандартное.
        var resilienceBuilder = builder.AddStandardResilienceHandler();
        ResiliencePipelineName = resilienceBuilder.PipelineName;

        services.AddSingleton<IConfigureOptions<HttpStandardResilienceOptions>>(
            sp => new ConfigureNamedOptions<HttpStandardResilienceOptions>(
                ResiliencePipelineName,
                options => options.TotalRequestTimeout.Timeout = settingsFactory(sp).TotalRequestTimeout));

        // SetHandlerLifetime принимает только TimeSpan, а значение здесь вычисляется лениво и на момент
        // регистрации ещё неизвестно. Поэтому lifetime задаётся прямо в HttpClientFactoryOptions — та же
        // опция, только с IServiceProvider под рукой. Именованный конфигуратор нужен, чтобы не затереть
        // HandlerLifetime у других typed-клиентов приложения.
        services.AddSingleton<IConfigureOptions<HttpClientFactoryOptions>>(
            sp => new S3HandlerLifetimeOptions(ClientName, settingsFactory(sp)));

        return services;
    }
}

/// <summary>
/// Маркер того, что клиент S3 уже зарегистрирован в этой коллекции, чтобы второй вызов
/// <see cref="Setup.AddSaS3BucketClient"/> падал сразу, а не склеивал два набора настроек на одном
/// экземпляре опций.
/// </summary>
internal sealed class S3BucketClientRegistration
{
}

/// <summary>
/// Применяет <see cref="S3BucketClientSetupOptions.HandlerLifetime"/> к именованному клиенту.
/// </summary>
/// <remarks>
/// Нужен потому, что <c>SetHandlerLifetime</c> принимает только <see cref="TimeSpan"/>, а значение
/// приходит из конвейера опций и на момент регистрации неизвестно. Пишем в ту же опцию напрямую —
/// имя клиента отсекает все остальные типизированные клиенты приложения.
/// </remarks>
internal sealed class S3HandlerLifetimeOptions(string clientName, S3BucketClientSetupOptions settings)
    : ConfigureNamedOptions<HttpClientFactoryOptions>(clientName, options => options.HandlerLifetime = settings.HandlerLifetime)
{
}
