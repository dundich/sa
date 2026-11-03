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
    /// Имя конвейера устойчивости, под которым <c>AddStandardResilienceHandler()</c> регистрирует
    /// свои опции. Запоминается при регистрации, потому что <c>TotalRequestTimeout</c> задаётся
    /// собственным <see cref="IConfigureOptions{TOptions}"/>, которому нужно точное имя, а выводить
    /// это имя угадыванием («имя клиента + суффикс») — значит заложить невидимую связь с чужим
    /// соглашением об именовании. При нескольких регистрациях отражает последнюю (внутренние
    /// конфигураторы каждого клиента захватывают имя своего клиента локально).
    /// </summary>
    internal static string? ResiliencePipelineName { get; private set; }

    /// <summary>
    /// Регистрирует именованного клиента S3 через стандартный конвейер настроек — один вызов,
    /// один клиент под своим именем.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="clientName">
    /// Уникальное имя клиента. Этим именем связаны все части регистрации: именованный `HttpClient`,
    /// keyed `S3BucketSettings`, keyed `IS3BucketClient` и именованный экземпляр опций
    /// <see cref="S3BucketClientSetupOptions"/> — резолвить клиента нужно по этому же имени.
    /// </param>
    /// <param name="configure">
    /// Единственный канал конфигурации: секция через
    /// <see cref="IS3BucketClientBuilder.FromConfiguration"/> и стандартный конвейер
    /// (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) через
    /// <see cref="IS3BucketClientBuilder.Options"/> — в одном делегате. Вызывается один
    /// раз, сразу; его действия <c>Options(...)</c> воспроизводятся после собственных
    /// регистраций метода, поэтому <c>Configure</c> отрабатывает после биндинга секции,
    /// а <c>Validate</c> дополняет — а не заменяет — встроенные проверки.
    /// </param>
    /// <returns>Та же коллекция <see cref="IServiceCollection"/> с добавленными сервисами.</returns>
    /// <exception cref="ArgumentNullException">Выбрасывается, если <paramref name="services"/> — <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Выбрасывается, если <paramref name="clientName"/> пуст или состоит из пробелов.</exception>
    /// <exception cref="InvalidOperationException">
    /// Выбрасывается, если клиент с таким именем уже зарегистрирован в этой коллекции.
    /// </exception>
    /// <remarks>
    /// Регистрация аддитивная по именам: каждый вызов с новым именем даёт полностью независимого
    /// клиента — свой именованный <c>HttpClient</c> (пул соединений, resilience, время жизни
    /// handler'а), свои keyed-настройки и свой keyed-клиент, построенный из собственного
    /// именованного экземпляра опций. Два клиента никогда не делят учётные данные, таймауты и пул
    /// соединений, поэтому несколько бакетов/эндпоинтов в одном процессе регистрируются отдельными
    /// вызовами. Повторная регистрация того же имени бросает исключение: второй вызов положил бы
    /// второй keyed-клиент под тот же ключ, и резолв по имени стал бы неоднозначным.
    /// <para>
    /// Резолв — только keyed, unkeyed-псевдонимов нет:
    /// <c>GetRequiredKeyedService&lt;IS3BucketClient&gt;(clientName)</c> и
    /// <c>GetRequiredKeyedService&lt;S3BucketSettings&gt;(clientName)</c>.
    /// </para>
    /// <para>
    /// Конвейер опций выполняется в фиксированном порядке: привязка секции
    /// (<c>FromConfiguration</c>, сырые значения) и затем <c>Configure</c> из
    /// <c>Options(...)</c> → <c>PostConfigure</c> (нормализация этим методом) и затем
    /// <c>PostConfigure</c> из <c>Options(...)</c> → валидация. Поэтому валидация видит нормализованный
    /// <see cref="S3BucketClientSetupOptions.Endpoint"/>, а <c>ValidateOnStart()</c> превращает
    /// неверную конфигурацию в <see cref="OptionsValidationException"/> на старте хоста, а не в
    /// <see cref="UriFormatException"/> посреди первой загрузки.
    /// </para>
    /// <para>
    /// Настройки читаются один раз — когда клиент создаётся фабрикой <c>HttpClientFactory</c>. Они не
    /// перечитываются на каждый запрос и не участвуют в hot-reload: пересоздание клиента означало бы и
    /// пересоздание HTTP-пула.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaS3BucketClient(
        this IServiceCollection services,
        string clientName,
        Action<IS3BucketClientBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        EnsureUniqueClientName(services, clientName);

        // Имя клиента используется и как имя именованных опций: один набор значений от секции/
        // Configure до HTTP-трубы, и резолв клиента — ровно по этому имени.
        RegisterNamedClient(services, clientName, configure);

        return services;
    }

    /// <summary>
    /// Общая регистрация клиента: маркер имени, именованный конвейер опций (секция, нормализация,
    /// валидация, действия <c>Options(...)</c> колбэка) и HTTP-обвязка через
    /// <see cref="AddSaS3BucketClientCore"/>.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="clientName">
    /// Ключ keyed-настроек/клиента, имя именованного <c>HttpClient</c> и имя именованных опций
    /// <see cref="S3BucketClientSetupOptions"/> — все части одной регистрации связаны одним именем.
    /// </param>
    /// <param name="configure">Колбэк конфигурации (может быть <c>null</c>).</param>
    private static void RegisterNamedClient(
        IServiceCollection services,
        string clientName,
        Action<IS3BucketClientBuilder>? configure)
    {
        services.AddSingleton(new S3BucketClientRegistration(clientName));

        var optsBuilder = services.AddOptions<S3BucketClientSetupOptions>(clientName);

        S3BucketClientBuilder? builder = null;

        if (configure is not null)
        {
            builder = new S3BucketClientBuilder();
            configure(builder);
        }

        // Фиксированный слот: секция биндится после того, как колбэк её записал, но до
        // воспроизведения его действий Options(...) — где бы они ни стояли в колбэке.
        var sectionPath = builder?.ConfigSectionPath;

        if (sectionPath is not null)
        {
            optsBuilder.BindConfiguration(sectionPath);
        }

        // Нормализация выполняется до валидации, поэтому проверки видят канонический вид значений.
        optsBuilder.PostConfigure(static options => options.Normalize());

        optsBuilder.ValidateOnStart();

        // IValidateOptions вместо ValidateDataAnnotations(): последний помечен RequiresUnreferencedCode
        // (IL2026) и ломает Native AOT.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<S3BucketClientSetupOptions>, S3BucketClientSetupOptionsValidator>());

        // Воспроизводятся в этом слоте — после привязки секции и после нашей нормализации,
        // поэтому Configure пользователя перебивает секцию, а его Validate идёт после наших.
        if (builder is { SettingsActions.Count: > 0 })
        {
            foreach (var settingsAction in builder.SettingsActions)
            {
                settingsAction(optsBuilder);
            }
        }

        services.AddSaS3BucketClientCore(
            clientName,
            sp => sp.GetRequiredService<IOptionsMonitor<S3BucketClientSetupOptions>>().Get(clientName));
    }

    /// <summary>
    /// Падает, если клиент с таким именем уже зарегистрирован в коллекции. Имена — ключи keyed
    /// регистраций, поэтому дубль имени сделал бы резолв по ключу неоднозначным, а именованные
    /// опции — склеенными (оба Configure-колбэка применились бы к одному экземпляру).
    /// </summary>
    private static void EnsureUniqueClientName(IServiceCollection services, string clientName)
    {
        if (services.Any(d => d.ServiceType == typeof(S3BucketClientRegistration)
            && d.ImplementationInstance is S3BucketClientRegistration registration
            && string.Equals(registration.ClientName, clientName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"An S3 bucket client named '{clientName}' has already been registered in this service collection. " +
                "A second registration under the same name would add a second keyed client under the same key " +
                "and stack its Configure callbacks on the same named options instance, so resolution would fail " +
                "and the settings would silently merge. Register each client under its own name.");
        }
    }

    /// <summary>
    /// Регистрирует HTTP-обвязку клиента (<c>IS3BucketClient</c>, пул соединений, resilience,
    /// время жизни handler'а) под заданным именем поверх произвольной ленивой фабрики настроек.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="clientName">
    /// Уникальное имя клиента: именованный <c>HttpClient</c>, keyed <c>IS3BucketClient</c> и keyed
    /// <c>S3BucketSettings</c> регистрируются под ним. Два вызова с разными именами дают два
    /// независимых клиента — каждый со своим пулом соединений, resilience и своим экземпляром
    /// настроек.
    /// </param>
    /// <param name="settingsFactory">
    /// Фабрика настроек. Вызывается при создании клиента, а не при регистрации, поэтому настройки
    /// могут приходить из конвейера <c>Microsoft.Extensions.Options</c> — на момент регистрации их
    /// значения ещё не материализованы и прочитать их нельзя.
    /// </param>
    /// <returns>Та же коллекция <see cref="IServiceCollection"/> с добавленными сервисами.</returns>
    /// <remarks>
    /// Единственная точка, где собирается HTTP-обвязка: и <see cref="AddSaS3BucketClient"/>, и
    /// провайдер <c>Sa.HybridFileStorage.S3</c> (у него свой тип опций и по клиенту на регистрацию)
    /// идут через неё, поэтому таймауты и пул соединений настраиваются одинаково во всех случаях.
    /// Клиент создаётся вручную, а не через typed <c>AddHttpClient&lt;IS3BucketClient, ...&gt;</c>:
    /// typed-вариант резолвил бы <c>S3BucketSettings</c> из DI как общий singleton (first-wins),
    /// здесь же каждая регистрация читает собственный keyed-экземпляр настроек — именно это делает
    /// возможными N клиентов в одной коллекции.
    /// </remarks>
    internal static IServiceCollection AddSaS3BucketClientCore(
        this IServiceCollection services,
        string clientName,
        Func<IServiceProvider, S3BucketClientSetupOptions> settingsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientName);
        ArgumentNullException.ThrowIfNull(settingsFactory);

        // Keyed по имени клиента: каждый клиент читает настройки своей регистрации, а не общий
        // singleton. Опции сами наследуют S3BucketSettings, поэтому в конструктор S3BucketClient
        // уходит тот же объект — без проекции, которая могла бы потерять поле.
        services.AddKeyedSingleton<S3BucketSettings>(clientName, (sp, _) => settingsFactory(sp));

        var builder = services.AddHttpClient(clientName, (sp, client) =>
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
        // регистрации, значит наше значение перекрывает стандартное. Pipeline name у каждого клиента
        // свой, так что два клиента не пересекаются по настройкам.
        var resilienceBuilder = builder.AddStandardResilienceHandler();
        string pipelineName = resilienceBuilder.PipelineName;
        ResiliencePipelineName = pipelineName;

        services.AddSingleton<IConfigureOptions<HttpStandardResilienceOptions>>(
            sp => new ConfigureNamedOptions<HttpStandardResilienceOptions>(
                pipelineName,
                options => options.TotalRequestTimeout.Timeout = settingsFactory(sp).TotalRequestTimeout));

        // SetHandlerLifetime принимает только TimeSpan, а значение здесь вычисляется лениво и на момент
        // регистрации ещё неизвестно. Поэтому lifetime задаётся прямо в HttpClientFactoryOptions — та же
        // опция, только с IServiceProvider под рукой. Именованный конфигуратор (по имени своего клиента)
        // нужен, чтобы не затереть HandlerLifetime у других именованных клиентов приложения.
        services.AddSingleton<IConfigureOptions<HttpClientFactoryOptions>>(
            sp => new S3HandlerLifetimeOptions(clientName, settingsFactory(sp)));

        // Клиент строится вручную: HttpClient — из IHttpClientFactory по имени (пул + resilience уже
        // настроены выше), настройки — из своего keyed-экземпляра, TimeProvider — из DI с дефолтом
        // на TimeProvider.System, когда он не зарегистрирован.
        services.AddKeyedSingleton<IS3BucketClient>(
            clientName,
            (sp, _) => new S3BucketClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
                sp.GetRequiredKeyedService<S3BucketSettings>(clientName),
                sp.GetService<TimeProvider>()));

        return services;
    }
}

/// <summary>
/// Маркер того, что клиент S3 уже зарегистрирован в этой коллекции: хранит имя клиента (ключ
/// keyed-регистрации), чтобы при попытке зарегистрировать второй раз под тем же именем бросить
/// понятное исключение. Регистрируется по одному маркеру на имя.
/// </summary>
internal sealed class S3BucketClientRegistration(string clientName)
{
    /// <summary>Ключ (имя) этого клиента: всегда непустое.</summary>
    public string ClientName { get; } =
        string.IsNullOrWhiteSpace(clientName) ? throw new ArgumentException("Client name cannot be empty.", nameof(clientName)) : clientName;
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
