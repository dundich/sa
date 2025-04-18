
namespace Sa.Schedule.Settings;

internal interface IInterceptorSettings
{
    IReadOnlyCollection<JobInterceptorSettings> Interceptors { get; }
}