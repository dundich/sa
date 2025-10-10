namespace Sa.Schedule.Settings;

internal sealed class NullJobServices : IServiceProvider
{
    public object? GetService(Type serviceType) => null;

    public static IServiceProvider Instance { get; } = new NullJobServices();
}
