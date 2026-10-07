using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Регистрация очереди через <see cref="Setup.AddSaWorkQueue{TInput}"/>: конвейер опций,
/// приоритет конфигурации, валидация и поведение при повторных вызовах.
/// </summary>
/// <remarks>
/// Тесты не выполняют работу в очереди дольше одного элемента — проверяется только то, как
/// собирается очередь. Функциональные сценарии живут в остальных WorkQueue*Tests.
/// </remarks>
public sealed class SetupRegistrationTests
{
    static string OptionsName<TInput>() => typeof(TInput).FullName!;

    static SaWorkQueueSettings Settings<TInput>(IServiceProvider provider)
        => provider.GetRequiredService<IOptionsMonitor<SaWorkQueueSettings>>().Get(OptionsName<TInput>());

    static IConfiguration Configuration(params (string Key, string? Value)[] entries)
    {
        var data = new Dictionary<string, string?>();
        foreach (var (key, value) in entries)
        {
            data[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    // ---------- null / повторная регистрация ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaWorkQueue<int>());
    }

    [Fact]
    public void Register_RejectsASecondConfiguringCall()
    {
        // Второй вызов, несущий конфигурацию, добавит ещё один Configure-колбэк к тому же
        // именованному SaWorkQueueSettings, и оба Configure применились бы — настройки
        // молча склеились бы.
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask));

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask)));

        Assert.Contains("already been configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_RejectsASecondCall_WithOnlyAConfigSection()
    {
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask));

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddSaWorkQueue<int>(b => b.FromConfiguration("WorkQueue")));

        Assert.Contains("already been configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_AllowsRepeatedBareCalls()
    {
        // «Голый» вызов без аргументов — способ повторной регистрации очереди того же типа:
        // TryAdd держит первую фабрику, а дескрипторы конвейера не копируются.
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>();
        services.AddSaWorkQueue<int>();
        services.AddSaWorkQueue<int>();

        Assert.Single(services, d => d.ServiceType == typeof(ISaWorkQueue<int>));
        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<SaWorkQueueSettings>));
    }

    [Fact]
    public void Register_BareCallAfterAConfiguredCall_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask));

        services.AddSaWorkQueue<int>();

        Assert.Single(services, d => d.ServiceType == typeof(ISaWorkQueue<int>));
    }

    [Fact]
    public void Register_ReturnsTheCollection_SoChainingWorks()
    {
        var services = new ServiceCollection();

        var returned = services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask));

        Assert.Same(services, returned);
    }

    [Fact]
    public void Register_AddsTheValidatorOnce()
    {
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask));

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<SaWorkQueueSettings>));
    }

    // ---------- биндинг секции ----------

    [Fact]
    public void Register_BindsAConfigurationSection()
    {
        // Именованные опции: секция биндится в экземпляр, названный typeof(TInput).FullName.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkQueue:ConcurrencyLimit"] = "4",
                ["WorkQueue:QueueCapacity"] = "100",
                ["WorkQueue:MaxConcurrency"] = "16",
                ["WorkQueue:SingleWriter"] = "true",
                ["WorkQueue:EnqueueStrategy"] = "Skip",
                ["WorkQueue:ReaderCancelMode"] = "Soft",
                ["WorkQueue:ReaderCancellationOrder"] = "RoundRobin",
                ["WorkQueue:ShutdownTimeout"] = "00:00:05",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask).FromConfiguration("WorkQueue"));

        using var provider = services.BuildServiceProvider();
        var settings = Settings<int>(provider);

        Assert.Equal(4, settings.ConcurrencyLimit);
        Assert.Equal(100, settings.QueueCapacity);
        Assert.Equal(16, settings.MaxConcurrency);
        Assert.True(settings.SingleWriter);
        Assert.Equal(SaEnqueueStrategy.Skip, settings.EnqueueStrategy);
        Assert.Equal(SaReaderCancelMode.Soft, settings.ReaderCancelMode);
        Assert.Equal(SaReaderCancellationOrder.RoundRobin, settings.ReaderCancellationOrder);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.ShutdownTimeout);
    }

    [Fact]
    public void Register_ConfigurationWinsOverTheCodeDefaults()
    {
        // Configure кода зарегистрирован до BindConfiguration, а биндер перезаписывает только
        // присутствующие ключи: ConcurrencyLimit из конфигурации побеждает код, а
        // QueueCapacity, о котором конфигурация молчит, остаётся из кода.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkQueue:ConcurrencyLimit"] = "4",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaWorkQueue<int>(b => b
            .WithProcess((_, _) => Task.CompletedTask)
            .WithConcurrencyLimit(8)
            .WithQueueCapacity(50)
            .FromConfiguration("WorkQueue"));

        using var provider = services.BuildServiceProvider();
        var settings = Settings<int>(provider);

        Assert.Equal(4, settings.ConcurrencyLimit);
        Assert.Equal(50, settings.QueueCapacity);
    }

    [Fact]
    public void Register_OptionsCallback_BeatsConfiguration()
    {
        // Actions из Options(...) реплеятся последними — после кодовых дефолтов и биндинга
        // секции, поэтому Configure внутри них перебивает конфигурацию.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkQueue:ConcurrencyLimit"] = "4",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaWorkQueue<int>(b => b
            .WithProcess((_, _) => Task.CompletedTask)
            .Options(o => o.Configure(x => x.ConcurrencyLimit = 2))
            .FromConfiguration("WorkQueue"));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(2, Settings<int>(provider).ConcurrencyLimit);
    }

    [Fact]
    public void Register_OptionsCallback_Validate_AddsToTheBuiltInChecks()
    {
        // Validate из Options(...) — часть того же канала и работает как обычная
        // IValidateOptions: OptionsValidationException на первом resolve.
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(
            b => b
                .WithProcess((_, _) => Task.CompletedTask)
                .Options(o => o.Validate(
                    x => x.ConcurrencyLimit != 3,
                    "ConcurrencyLimit must not be 3.")));

        using var provider = services.BuildServiceProvider();

        // Настройка из кода...
        var ok = Settings<int>(provider);
        Assert.Null(ok.ConcurrencyLimit);

        // ...а три — как раз то, что запретил Validate.
        var services3 = new ServiceCollection();
        services3.AddSaWorkQueue<int>(
            b => b
                .WithProcess((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(3)
                .Options(o => o.Validate(
                    x => x.ConcurrencyLimit != 3,
                    "ConcurrencyLimit must not be 3.")));

        using var provider3 = services3.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(
            () => provider3.GetRequiredService<ISaWorkQueue<int>>());
        Assert.Contains("ConcurrencyLimit must not be 3.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_OptionsCallback_KeepsTheChainFluent()
    {
        // Options(...) возвращает билдер — pipeline-вызов не обрывает цепочку.
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(b => b
            .Options(o => o.Configure(x => x.QueueCapacity = 32))
            .WithProcess((_, _) => Task.CompletedTask)
            .WithStatusCallback((_, _, _) => { }));

        using var provider = services.BuildServiceProvider();
        using var queue = provider.GetRequiredService<ISaWorkQueue<int>>();

        Assert.Equal(32, queue.QueueCapacity);
    }

    [Fact]
    public void Register_WithoutASection_WorksWithoutConfiguration()
    {
        // BindConfiguration вызывается только когда задан путь, поэтому контейнер вовсе
        // без IConfiguration обязан работать.
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask));

        using var provider = services.BuildServiceProvider();
        using var queue = provider.GetRequiredService<ISaWorkQueue<int>>();

        Assert.NotNull(queue);
        Assert.Equal(SaEnqueueStrategy.Wait, Settings<int>(provider).EnqueueStrategy);
    }

    [Fact]
    public void Register_TwoItemTypes_BindFromSeparateSections()
    {
        // Опции названы по типу элемента, поэтому две очереди разных типов в одном
        // контейнере не делят ни секцию, ни маркер guard'а.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntQueue:ConcurrencyLimit"] = "3",
                ["StringQueue:ConcurrencyLimit"] = "7",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask).FromConfiguration("IntQueue"));
        services.AddSaWorkQueue<string>(b => b.WithProcess((_, _) => Task.CompletedTask).FromConfiguration("StringQueue"));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(3, Settings<int>(provider).ConcurrencyLimit);
        Assert.Equal(7, Settings<string>(provider).ConcurrencyLimit);
    }

    // ---------- валидация ----------

    [Fact]
    public void Register_FailsValidation_OnResolve_NotOnRegistration()
    {
        // Неверная настройка всплывает как OptionsValidationException на первом resolve
        // очереди, а не как ArgumentOutOfRangeException изнутри очереди посреди работы.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkQueue:QueueCapacity"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask).FromConfiguration("WorkQueue"));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<ISaWorkQueue<int>>());

        Assert.Contains("QueueCapacity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_NegativeConcurrencyLimit_FailsValidation()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkQueue:ConcurrencyLimit"] = "-1",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask).FromConfiguration("WorkQueue"));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<ISaWorkQueue<int>>());

        Assert.Contains("ConcurrencyLimit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_ZeroShutdownTimeout_FailsValidation()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkQueue:ShutdownTimeout"] = "00:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaWorkQueue<int>(b => b.WithProcess((_, _) => Task.CompletedTask).FromConfiguration("WorkQueue"));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<ISaWorkQueue<int>>());

        Assert.Contains("ShutdownTimeout", ex.Message, StringComparison.Ordinal);
    }

    // ---------- процессор ----------

    [Fact]
    public void Register_WithoutAnyProcessor_FailsWithAGuideOnResolve()
    {
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ISaWorkQueue<int>>());

        Assert.Contains("No processor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UseProcessor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_FallsBackToARegisteredISaWork()
    {
        // Голая регистрация очереди + процессор, зарегистрированный вручную: builder не
        // обязателен, когда ISaWork<TInput> уже есть в контейнере.
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddSingleton<ISaWork<int>>(new DelegateWork((_, _) =>
        {
            processed.SetResult();
            return Task.CompletedTask;
        }));
        services.AddSaWorkQueue<int>();

        using var provider = services.BuildServiceProvider();
        using var queue = provider.GetRequiredService<ISaWorkQueue<int>>();

        await queue.Enqueue(42, TestContext.Current.CancellationToken);
        await processed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    // ---------- сборка опций: settings → очередь ----------

    [Fact]
    public void Register_CodeDefaults_ReachTheQueue()
    {
        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(b => b
            .WithProcess((_, _) => Task.CompletedTask)
            .WithConcurrencyLimit(2)
            .WithQueueCapacity(64)
            .WithEnqueueStrategy(SaEnqueueStrategy.Throw));

        using var provider = services.BuildServiceProvider();
        using var queue = provider.GetRequiredService<ISaWorkQueue<int>>();

        Assert.Equal(2, queue.ConcurrencyLimit);
        Assert.Equal(64, queue.QueueCapacity);
    }

    [Fact]
    public void Register_EscapeHatch_RunsAfterConfiguration()
    {
        // Configure оперирует готовыми опциями на resolve — последний рубеж после
        // конфигурации и Options(...)-действий.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkQueue:QueueCapacity"] = "10",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaWorkQueue<int>(b => b
            .WithProcess((_, _) => Task.CompletedTask)
            .Configure((_, opts) => opts.WithQueueCapacity(99))
            .FromConfiguration("WorkQueue"));

        using var provider = services.BuildServiceProvider();
        using var queue = provider.GetRequiredService<ISaWorkQueue<int>>();

        Assert.Equal(99, queue.QueueCapacity);
    }

    [Fact]
    public async Task Register_BuilderCallbacks_ReachTheQueue()
    {
        var statuses = new List<SaWorkStatus>();
        using var gate = new SemaphoreSlim(0);

        var services = new ServiceCollection();
        services.AddSaWorkQueue<int>(b => b
            .WithConcurrencyLimit(1)
            .WithProcess((_, _) => Task.CompletedTask)
            .WithStatusCallback((_, status, _) =>
            {
                lock (statuses) statuses.Add(status);
                if (status == SaWorkStatus.Completed) gate.Release();
            }));

        using var provider = services.BuildServiceProvider();
        using var queue = provider.GetRequiredService<ISaWorkQueue<int>>();

        await queue.Enqueue(1, TestContext.Current.CancellationToken);
        await gate.WaitAsync(TestContext.Current.CancellationToken);

        lock (statuses)
        {
            Assert.Contains(SaWorkStatus.Running, statuses);
            Assert.Contains(SaWorkStatus.Completed, statuses);
        }
    }

    private sealed class DelegateWork(Func<int, CancellationToken, Task> process) : ISaWork<int>
    {
        public Task Execute(int input, CancellationToken cancellationToken)
            => process(input, cancellationToken);
    }
}
