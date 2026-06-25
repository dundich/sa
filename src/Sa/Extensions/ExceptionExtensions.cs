using System.Diagnostics;
using System.Text;

namespace Sa.Extensions;

internal static class ExceptionExtensions
{
    [DebuggerStepThrough]
    public static bool IsCritical(this Exception ex)
    {
        if (ex is OutOfMemoryException) return true;
        if (ex is StackOverflowException) return true;
        if (ex is AppDomainUnloadedException) return true;
        if (ex is BadImageFormatException) return true;
        if (ex is CannotUnloadAppDomainException) return true;
        if (ex is InvalidProgramException) return true;
        if (ex is ThreadAbortException) return true;

        return false;
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
