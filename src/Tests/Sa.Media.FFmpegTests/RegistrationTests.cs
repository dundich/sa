using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Media.FFmpeg;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// Регистрация FFmpeg: что делает конвейер опций, в каком порядке и что достаёт до фабрики.
/// </summary>
public sealed class RegistrationTests : IDisposable
{
    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"ffmpeg_reg_{Path.GetRandomFileName()}");

    public RegistrationTests() => Directory.CreateDirectory(_testDir);

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { /* best effort */ }
    }

    static FFMpegOptions Options(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<FFMpegOptions>>().Value;

    // ---------- null / повторная регистрация ----------

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaFFMpeg());
    }

    [Fact]
    public void Register_RejectsASecondCall()
    {
        // Безымянный FFMpegOptions принадлежит регистрации: второй вызов добавил бы ещё один
        // IConfigureOptions в тот же экземпляр, и обе Configure-функции применились бы — настройки
        // молча слились бы.
        var services = new ServiceCollection();
        services.AddSaFFMpeg();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSaFFMpeg());

        Assert.Contains("already been registered", ex.Message, StringComparison.Ordinal);
        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<FFMpegOptions>));
    }

    [Fact]
    public void Register_ReturnsTheCollection_SoChainingWorks()
    {
        var services = new ServiceCollection();

        var returned = services.AddSaFFMpeg();

        Assert.Same(services, returned);
    }

    [Fact]
    public void Register_AddsTheValidatorOnce()
    {
        var services = new ServiceCollection();
        services.AddSaFFMpeg();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<FFMpegOptions>));
    }

    [Fact]
    public void Register_AddsTheExecutors()
    {
        var services = new ServiceCollection();
        services.AddSaFFMpeg();

        Assert.Contains(services, d => d.ServiceType == typeof(IFFMpegExecutor));
        Assert.Contains(services, d => d.ServiceType == typeof(IFFProbeExecutor));
        Assert.Contains(services, d => d.ServiceType == typeof(IFFMpegExecutorFactory));
        Assert.Contains(services, d => d.ServiceType == typeof(IPcmS16LeChannelManipulator));
        Assert.Contains(services, d => d.ServiceType == typeof(IProcessExecutor));
        Assert.Contains(services, d => d.ServiceType == typeof(IFFMpegLocator));
    }

    // ---------- нормализация (PostConfigure) ----------

    [Fact]
    public void Register_PostConfigures_ThePathsToFullPaths()
    {
        var services = new ServiceCollection();
        services.AddSaFFMpeg(o => o.Configure(x =>
        {
            x.WritableDirectory = _testDir;
            x.ExecutablePath = ".";
        }));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal(Path.GetFullPath(_testDir), options.WritableDirectory);
        Assert.Equal(Path.GetFullPath("."), options.ExecutablePath);
        Assert.True(Path.IsPathFullyQualified(options.ExecutablePath!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_BlankPath_BecomesUnset_NotADirectoryNamedAfterSpaces(string value)
    {
        // Path.GetFullPath("   ") на Unix успешно возвращает путь (пробелы — легальное имя файла),
        // так что без guard'а пустая опция превратилась бы в каталог "   ", а валидация прошла бы.
        var services = new ServiceCollection();
        services.AddSaFFMpeg(o => o.Configure(x =>
        {
            x.WritableDirectory = value;
            x.ExecutablePath = value;
        }));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Null(options.WritableDirectory);
        Assert.Null(options.ExecutablePath);
    }

    [Fact]
    public void Register_PostConfiguresBeforeValidating()
    {
        // Путь из одних пробелов после нормализации становится «не задан», а значит проходит
        // проверку на файл — доказательство, что валидация идёт после PostConfigure.
        var services = new ServiceCollection();
        services.AddSaFFMpeg(o => o.Configure(x => x.WritableDirectory = "   "));

        using var provider = services.BuildServiceProvider();

        Assert.Null(Options(provider).WritableDirectory);
    }

    // ---------- callback, который получает OptionsBuilder ----------

    [Fact]
    public void Register_CallbackCanAddAPostConfigure()
    {
        var services = new ServiceCollection();

        services.AddSaFFMpeg(o => o
            .Configure(x => x.TimeoutSeconds = 5)
            .PostConfigure(x => x.TimeoutSeconds = 120));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(120, Options(provider).TimeoutSeconds);
    }

    [Fact]
    public void Register_CallbackPostConfigure_SeesTheNormalisedValue()
    {
        string? seen = null;

        var services = new ServiceCollection();
        services.AddSaFFMpeg(o => o
            .Configure(x => x.WritableDirectory = _testDir)
            .PostConfigure(x => seen = x.WritableDirectory));

        using var provider = services.BuildServiceProvider();

        // Материализует опции именно чтение .Value — сам IOptions<T> ленив.
        _ = Options(provider);

        Assert.Equal(Path.GetFullPath(_testDir), seen);
    }

    [Fact]
    public void Register_CallbackCanAddValidation()
    {
        var services = new ServiceCollection();

        services.AddSaFFMpeg(o => o
            .Configure(x => x.TimeoutSeconds = 1)
            .Validate(x => x.TimeoutSeconds >= 60, "TimeoutSeconds must be at least 60."));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("at least 60", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_CallbackValidation_AddsToTheBuiltInChecks()
    {
        // Validate из callback не заменяет встроенную проверку: сообщаются обе ошибки.
        var services = new ServiceCollection();

        services.AddSaFFMpeg(o => o
            .Configure(x => { x.TimeoutSeconds = -1; x.ExecutablePath = _testDir; })
            .Validate(x => x.ExecutablePath is null, "ExecutablePath must be unset."));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("TimeoutSeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ExecutablePath must be unset", ex.Message, StringComparison.Ordinal);
    }

    // ---------- биндинг секции ----------

    [Fact]
    public void Register_BindsAConfigurationSection()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ffmpeg:ExecutablePath"] = "/usr/bin/ffmpeg",
                ["Ffmpeg:WritableDirectory"] = "/tmp/out",
                ["Ffmpeg:TimeoutSeconds"] = "300",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaFFMpeg(configSectionPath: "Ffmpeg");

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Equal(Path.GetFullPath("/usr/bin/ffmpeg"), options.ExecutablePath);
        Assert.Equal(Path.GetFullPath("/tmp/out"), options.WritableDirectory);
        Assert.Equal(300, options.TimeoutSeconds);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Timeout);
    }

    [Fact]
    public void Register_ConfigureCallback_OverridesTheBoundSection()
    {
        // Callback вызывается после BindConfiguration, поэтому его Configure — последнее слово.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ffmpeg:TimeoutSeconds"] = "300",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaFFMpeg(
            configSectionPath: "Ffmpeg",
            configure: o => o.Configure(x => x.TimeoutSeconds = 30));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(30, Options(provider).TimeoutSeconds);
    }

    [Fact]
    public void Register_WithoutASection_WorksWithoutConfiguration()
    {
        // BindConfiguration вызывается только когда задан путь, поэтому контейнер вовсе без
        // IConfiguration обязан работать и сохранять значения по умолчанию.
        var services = new ServiceCollection();
        services.AddSaFFMpeg();

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Null(options.ExecutablePath);
        Assert.Null(options.WritableDirectory);
        Assert.Null(options.TimeoutSeconds);
        Assert.Null(options.Timeout);
    }

    // ---------- валидация ----------

    [Fact]
    public void Register_FailsValidation_OnResolve_NotOnRegistration()
    {
        var services = new ServiceCollection();
        services.AddSaFFMpeg(o => o.Configure(x => x.TimeoutSeconds = -1));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => Options(provider));

        Assert.Contains("TimeoutSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_ZeroTimeout_IsAllowed_AndMeansTheDefault()
    {
        var services = new ServiceCollection();
        services.AddSaFFMpeg(o => o.Configure(x => x.TimeoutSeconds = 0));

        using var provider = services.BuildServiceProvider();

        var options = Options(provider);

        Assert.Null(options.Timeout);
    }

    [Fact]
    public void Validate_IsUsableOutsideDi()
    {
        // Собственный тип исключения у Validate() оставлен именно для этого сценария.
        var options = new FFMpegOptions { TimeoutSeconds = -1 };

        Assert.Throws<ValidationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_AcceptsTheDefaults()
    {
        var options = new FFMpegOptions();

        options.Validate();
    }
}