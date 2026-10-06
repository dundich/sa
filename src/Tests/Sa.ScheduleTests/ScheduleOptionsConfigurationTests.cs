using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Schedule;

namespace Sa.ScheduleTests;

/// <summary>
/// The 0.16.0 configuration overlay: <c>AddSaSchedule</c> binds <see cref="ScheduleOptions"/>
/// from a configuration section and applies it on top of the code-registered jobs —
/// configuration wins (both directions for the boolean flags <c>Disabled</c>, <c>Immediate</c>,
/// <c>IsRunOnce</c>), unknown keys are ignored,
/// and the validator fails fast on malformed values.
/// </summary>
public sealed class ScheduleOptionsConfigurationTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

    private sealed class Job1 : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class Job2 : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static IServiceCollection NewServices(
        Action<IScheduleBuilder>? configure = null,
        IDictionary<string, string?>? entries = null,
        Action<OptionsBuilder<ScheduleOptions>>? configureOptions = null,
        string? configSectionPath = "Schedule")
    {
        var services = new ServiceCollection();

        if (entries is not null)
        {
            services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(entries).Build());
        }

        services.AddSaSchedule(configure, configureOptions, configSectionPath);

        return services;
    }

    private static IScheduleSettings BuildSettings(
        Action<IScheduleBuilder>? configure = null,
        IDictionary<string, string?>? entries = null,
        Action<OptionsBuilder<ScheduleOptions>>? configureOptions = null,
        string? configSectionPath = "Schedule")
        => NewServices(configure, entries, configureOptions, configSectionPath)
            .BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>();

    private static IJobSettings GetJob(IScheduleSettings settings, string name)
        => settings.GetJobSettings().Single(c => c.Properties.JobName == name);

    private static DateTimeOffset? Next(IJobSettings job)
        => job.Properties.Timing?.GetNextOccurrence(From);

    [Fact]
    public void Section_WinsOverCode_ForTimingAndInitialDelay()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>()
                .WithName("J1")
                .EveryTime(TimeSpan.FromSeconds(10))
                .WithInitialDelay(TimeSpan.FromSeconds(99)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Every"] = "00:00:30",
                ["Schedule:Jobs:J1:InitialDelay"] = "00:01:00",
            });

        var job = GetJob(settings, "J1");

        Assert.Equal(From.AddSeconds(30), Next(job));
        Assert.Equal(TimeSpan.FromSeconds(60), job.Properties.InitialDelay);
    }

    [Fact]
    public void DisabledTrue_ExcludesJob()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("J1").EveryTime(TimeSpan.FromSeconds(1)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Disabled"] = "true",
            });

        Assert.Empty(settings.GetJobSettings());
    }

    [Fact]
    public void DisabledFalse_ReEnablesCodeDisabledJob()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("J1").Disabled().EveryTime(TimeSpan.FromSeconds(1)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Disabled"] = "false",
            });

        var job = GetJob(settings, "J1");

        Assert.False(job.Properties.Disabled);
        Assert.Equal(From.AddSeconds(1), Next(job));
    }

    [Fact]
    public void JobWithoutName_IsMatchedByTypeFullName()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job2>().EveryTime(TimeSpan.FromSeconds(1)),
            entries: new Dictionary<string, string?>
            {
                [$"Schedule:Jobs:{typeof(Job2).FullName}:Every"] = "00:00:05",
            });

        var job = settings.GetJobSettings().Single();

        Assert.Equal(From.Add(TimeSpan.FromSeconds(5)), Next(job));
    }

    [Fact]
    public void UnknownJobKey_IsIgnored()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("J1").EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:NotRegistered:Every"] = "00:05:00",
            });

        Assert.Equal(From.AddSeconds(10), Next(GetJob(settings, "J1")));
    }

    [Fact]
    public void UnknownProperty_IsIgnored()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("J1").EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:EveryTypo"] = "00:05:00",
            });

        Assert.Equal(From.AddSeconds(10), Next(GetJob(settings, "J1")));
    }

    [Fact]
    public void JobNameWithSpaces_Binds()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("Cleanup job").EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:Cleanup job:Every"] = "00:00:05",
            });

        Assert.Equal(From.AddSeconds(5), Next(GetJob(settings, "Cleanup job")));
    }

    [Fact]
    public void Cron_ReplacesCodeTiming()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("J1").EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Cron"] = "0 3 * * *",
            });

        var job = GetJob(settings, "J1");

        Assert.Equal("cron", job.Properties.Timing?.TimingName);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero), Next(job));
    }

    [Fact]
    public void UserConfigure_HasLastWordOverSection()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("J1").EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Every"] = "00:30:00",
            },
            configureOptions: o => o.Configure(opts => opts.Jobs["J1"].Every = TimeSpan.FromMinutes(1)));

        Assert.Equal(From.AddMinutes(1), Next(GetJob(settings, "J1")));
    }

    [Fact]
    public void Validator_CronAndEveryTogether_FailsOnResolve()
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => NewServices(entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Cron"] = "0 3 * * *",
                ["Schedule:Jobs:J1:Every"] = "00:05:00",
            })
            .BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>());

        Assert.Contains("mutually exclusive", ex.Message);
    }

    [Fact]
    public void Validator_MalformedCron_FailsOnResolve()
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => NewServices(entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Cron"] = "definitely not cron",
            })
            .BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>());

        Assert.Contains("not a valid", ex.Message);
    }

    [Fact]
    public void Validator_NonPositiveEvery_FailsOnResolve()
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => NewServices(entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Every"] = "00:00:00",
            })
            .BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>());

        Assert.Contains("greater than zero", ex.Message);
    }

    [Fact]
    public void Validator_NegativeInitialDelay_FailsOnResolve()
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => NewServices(entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:InitialDelay"] = "-00:00:05",
            })
            .BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>());

        Assert.Contains("must not be negative", ex.Message);
    }

    [Fact]
    public void TimeSpanConstantFormatOnly_NonConstantForm_FailsOnResolve()
    {
        // The binder parses TimeSpan in the constant form ([d.]hh:mm:ss) only — "5m" is not it.
        // An unparseable value surfaces as an InvalidOperationException wrapping a
        // FormatException when the options are first read, i.e. at host startup.
        var ex = Assert.Throws<InvalidOperationException>(
            () => NewServices(entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Every"] = "5m",
            })
            .BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>());

        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void SecondConfiguringCall_Throws()
    {
        var services = NewServices(configSectionPath: "Schedule");

        Assert.Throws<InvalidOperationException>(
            () => services.AddSaSchedule(configSectionPath: "Schedule"));

        Assert.Throws<InvalidOperationException>(
            () => services.AddSaSchedule(configureOptions: o => o.Configure(_ => { })));
    }

    [Fact]
    public void
        RepeatedCallsWithBuilderButNoOptionsConfiguration_StayRepeatable()
    {
        // The shape downstream libraries use (AddSaPartitional, AddSaOutboxUsingPostgreSql):
        // a configure callback, no options parameters — must not trip the guard.
        // No IConfiguration is registered and no section path is requested, so the options
        // pipeline never pulls one in — and a second such call must not trip the
        // configuring-call guard.
        var services = new ServiceCollection();

        services.AddSaSchedule(b =>
        {
            b.AddJob<Job1>().WithName("J1").EveryTime(TimeSpan.FromSeconds(1));
        });

        services.AddSaSchedule(b =>
        {
            b.AddJob<Job2>().WithName("J2").EveryTime(TimeSpan.FromSeconds(2));
        });

        var settings = services.BuildServiceProvider().GetRequiredService<IScheduleSettings>();

        Assert.Equal(2, settings.GetJobSettings().Count());
    }

    [Fact]
    public void
        BareCallAfterConfiguringCall_DoesNotTripTheGuard()
    {
        // The real downstream shape: the app configures the options (a section here),
        // then a library contributes jobs with a bare AddSaSchedule. The marker is
        // already present — a bare call must not trip it, and the job must still
        // resolve with its code settings.
        var services = NewServices(
            entries: new Dictionary<string, string?>(),
            configSectionPath: "Schedule");

        var exception = Record.Exception(
            () => services.AddSaSchedule(b =>
            {
                b.AddJob<Job2>().WithName("J2").EveryTime(TimeSpan.FromSeconds(7));
            }));

        Assert.Null(exception);

        var job = services.BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>()
            .GetJobSettings()
            .Single();

        Assert.Equal(From.AddSeconds(7), Next(job));
    }

    [Fact]
    public void
        BareCallWithoutConfigurationSection_LeavesCodeSettingsUntouched()
    {
        // No IConfiguration registered at all and no section path — the options pipeline is
        // inert (default options, empty Jobs) and the code settings pass through unchanged.
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>()
                .WithName("J1")
                .EveryTime(TimeSpan.FromSeconds(10))
                .WithInitialDelay(TimeSpan.FromSeconds(99)),
            configSectionPath: null);

        var job = GetJob(settings, "J1");

        Assert.Equal(From.AddSeconds(10), Next(job));
        Assert.Equal(TimeSpan.FromSeconds(99), job.Properties.InitialDelay);
    }

    [Fact]
    public void ImmediateTrue_FromConfig()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("J1").EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Immediate"] = "true",
            });

        Assert.True(GetJob(settings, "J1").Properties.Immediate);
    }

    [Fact]
    public void ImmediateFalse_RevertsCodeStartImmediate()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>()
                .WithName("J1")
                .StartImmediate()
                .EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:Immediate"] = "false",
            });

        Assert.False(GetJob(settings, "J1").Properties.Immediate);
    }

    [Fact]
    public void IsRunOnceTrue_FromConfig()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>().WithName("J1").EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:IsRunOnce"] = "true",
            });

        Assert.True(GetJob(settings, "J1").Properties.IsRunOnce);
    }

    [Fact]
    public void IsRunOnceFalse_RevertsCodeRunOnce()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>()
                .WithName("J1")
                .RunOnce()
                .EveryTime(TimeSpan.FromSeconds(10)),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:IsRunOnce"] = "false",
            });

        Assert.False(GetJob(settings, "J1").Properties.IsRunOnce);
    }

    [Fact]
    public void ConcurrencyOptions_FromConfig_BeatCode()
    {
        var settings = BuildSettings(
            configure: b => b.AddJob<Job1>()
                .WithName("J1")
                .EveryTime(TimeSpan.FromSeconds(10))
                .WithConcurrencyLimit(1)
                .WithMaxConcurrency(1),
            entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:ConcurrencyLimit"] = "3",
                ["Schedule:Jobs:J1:MaxConcurrency"] = "5",
            });

        var job = GetJob(settings, "J1");

        Assert.Equal(3, job.Properties.ConcurrencyLimit);
        Assert.Equal(5, job.Properties.MaxConcurrency);
    }

    [Fact]
    public void Validator_NegativeConcurrencyLimit_FailsOnResolve()
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => NewServices(entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:ConcurrencyLimit"] = "-1",
            })
            .BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>());

        Assert.Contains("must not be negative", ex.Message);
    }

    [Fact]
    public void Validator_ZeroMaxConcurrency_FailsOnResolve()
    {
        var ex = Assert.Throws<OptionsValidationException>(
            () => NewServices(entries: new Dictionary<string, string?>
            {
                ["Schedule:Jobs:J1:MaxConcurrency"] = "0",
            })
            .BuildServiceProvider()
            .GetRequiredService<IScheduleSettings>());

        Assert.Contains("at least 1", ex.Message);
    }
}
