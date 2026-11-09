namespace Sa.Schedule.Settings;

internal sealed record JobInterceptorSettings(Type HandlerType, object? Key = null);
