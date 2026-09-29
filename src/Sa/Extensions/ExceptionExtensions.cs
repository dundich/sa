using System.Diagnostics;
using System.Text;

namespace Sa.Extensions;

internal static class ExceptionExtensions
{
    /// <summary>
    /// Exceptions that leave the process unusable, so a caller must neither retry them nor
    /// swallow them. <see cref="AccessViolationException"/> is part of the list: it means the
    /// process state is corrupt, and <c>Retry.IsFatal</c> has always treated it that way —
    /// the two lists used to disagree, so the same failure was fatal to
    /// <c>Retry</c> and retriable by its callers.
    /// </summary>
    [DebuggerStepThrough]
    public static bool IsCritical(this Exception ex)
    {
        return ex is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException
            or CannotUnloadAppDomainException
            or InvalidProgramException
            or ThreadAbortException;
    }

    [DebuggerStepThrough]
    public static string GetErrorMessages(this Exception exception)
    {
        var sb = new StringBuilder(exception.Message.Length + 64);
        var current = exception;
        while (current != null)
        {
            sb.AppendLine(current.Message);
            current = current.InnerException;
        }
        return sb.ToString();
    }
}
