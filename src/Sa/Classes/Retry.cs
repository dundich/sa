using Sa.Extensions;
using System.Diagnostics;

namespace Sa.Classes;



public static class Retry
{
    /// <summary>
    /// For example: 500ms, 500ms, 500ms ...
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Constant<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int waitTime = 500,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateConstant(TimeSpan.FromMilliseconds(waitTime), retryCount, fastFirst: true)
            .WaitAndRetry(fun, input, next, cancellationToken: cancellationToken);
    }

    [DebuggerStepThrough]
    public static ValueTask<T> Constant<T>(
    Func<CancellationToken, ValueTask<T>> fun,
    int retryCount = 3,
    int waitTime = 500,
    Func<Exception, int, bool>? next = null,
    CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateConstant(TimeSpan.FromMilliseconds(waitTime), retryCount, fastFirst: true)
            .WaitAndRetry(fun, next, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// For example: 100ms, 200ms, 400ms, 800ms, ...
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Exponential<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int initialDelay = 100,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateExponential(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, input, next, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// For example: 100ms, 200ms, 400ms, 800ms, ...
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Exponential<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 100,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateExponential(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, next, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// For example: 100ms, 200ms, 300ms, 400ms, ..
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Linear<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int initialDelay = 100,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateLinear(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, input, next, cancellationToken);
    }


    /// <summary>
    /// For example: 100ms, 200ms, 300ms, 400ms, ..
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Linear<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 100,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateLinear(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, next, cancellationToken);
    }



    /// <summary>
    /// For example: 850ms, 1455ms, 3060ms.
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Jitter<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int initialDelay = 530,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateJitter(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, input, next, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// For example: 850ms, 1455ms, 3060ms.
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Jitter<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 530,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateJitter(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, next, cancellationToken: cancellationToken);
    }

    [DebuggerStepThrough]
    public static async ValueTask<T> WaitAndRetry<I, T>(
       this IEnumerable<TimeSpan> timeSpans,
       Func<I, CancellationToken, ValueTask<T>> fun,
       I input,
       Func<Exception, int, bool>? next = null,
       CancellationToken cancellationToken = default)
    {
        TimeSpan[] points = [.. timeSpans];

        for (int i = 0; i < points.Length - 1; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                return await fun(input, cancellationToken);
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException)
                {
                    break;
                }
                else if (e.IsCritical() || next != null && !next(e, i))
                {
                    throw;
                }

                await Wait(points[i], cancellationToken);
            }
        }

        if (points.Length > 0)
        {
            await Wait(points[^1], cancellationToken);
        }

        return await fun(input, cancellationToken);
    }

    [DebuggerStepThrough]
    public static async ValueTask<T> WaitAndRetry<T>(
       this IEnumerable<TimeSpan> timeSpans,
       Func<CancellationToken, ValueTask<T>> fun,
       Func<Exception, int, bool>? next = null,
       CancellationToken cancellationToken = default)
    {
        TimeSpan[] points = [.. timeSpans];

        for (int i = 0; i < points.Length - 1; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                return await fun(cancellationToken);
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException)
                {
                    break;
                }
                else if (e.IsCritical() || next != null && !next(e, i))
                {
                    throw;
                }

                await Wait(points[i], cancellationToken);
            }
        }

        if (points.Length > 0)
        {
            await Wait(points[^1], cancellationToken);
        }

        return await fun(cancellationToken);
    }

    private static async Task Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // ignore 
        }
    }
}