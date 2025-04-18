namespace Sa.Schedule.Settings;

internal class InterceptorSettings : IInterceptorSettings
{
    private readonly List<JobInterceptorSettings> _interceptors = [];

    public IReadOnlyCollection<JobInterceptorSettings> Interceptors => _interceptors;

    public InterceptorSettings(IEnumerable<JobInterceptorSettings> items)
    {
        foreach (JobInterceptorSettings item in items)
        {
            if (!_interceptors.Contains(item))
            {
                _interceptors.Add(item);
            }
        }
    }
}
