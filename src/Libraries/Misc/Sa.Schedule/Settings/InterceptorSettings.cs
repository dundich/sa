namespace Sa.Schedule.Settings;

internal sealed class InterceptorSettings : IInterceptorSettings
{
    private readonly List<JobInterceptorSettings> _interceptors = [];

    public IReadOnlyCollection<JobInterceptorSettings> Interceptors => _interceptors;

    public InterceptorSettings(IEnumerable<JobInterceptorSettings> items)
    {
        foreach (JobInterceptorSettings item in items.Where(c => !_interceptors.Contains(c)))
        {
            _interceptors.Add(item);
        }
    }
}
