namespace Sa.Schedule.Settings;


internal class JobBuilder(JobSettings settings) : IJobBuilder
{
    public IJobBuilder ConfigureErrorHandling(Action<IJobErrorHandlingBuilder> configure)
    {
        configure.Invoke(settings.ErrorHandling);
        return this;
    }

    public IJobBuilder EveryTime(TimeSpan timeSpan, string? name = null)
    {
        settings.Properties.EveryTime(timeSpan, name);
        return this;
    }

    public IJobBuilder Merge(IJobProperties props)
    {
        settings.Properties.Merge(props);
        return this;
    }

    public IJobBuilder RunOnce()
    {
        settings.Properties.RunOnce();
        return this;
    }

    public IJobBuilder StartImmediate()
    {
        settings.Properties.StartImmediate();
        return this;
    }

    public IJobBuilder WithInitialDelay(TimeSpan delay)
    {
        settings.Properties.WithInitialDelay(delay);
        return this;
    }

    public IJobBuilder WithName(string name)
    {
        settings.Properties.WithName(name);
        return this;
    }

    public IJobBuilder WithTag(object tag)
    {
        settings.Properties.WithTag(tag);
        return this;
    }

    public IJobBuilder WithTiming(IJobTiming timing)
    {
        settings.Properties.WithTiming(timing);
        return this;
    }

    public IJobBuilder WithContextStackSize(int size)
    {
        settings.Properties.WithContextStackSize(size);
        return this;
    }

    public IJobBuilder Disabled()
    {
        settings.Properties.SetDisabled();
        return this;
    }
}
