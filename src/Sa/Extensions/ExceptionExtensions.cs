using System.Diagnostics;
using System.Text;

namespace Sa.Extensions;

public static class ExceptionExtensions
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
        StringBuilder sb = new();
        sb.AppendLine(exception.Message);
        if (exception.InnerException != null)
        {
            sb.AppendLine(GetErrorMessages(exception.InnerException));
        }
        return sb.ToString();
    }
}
