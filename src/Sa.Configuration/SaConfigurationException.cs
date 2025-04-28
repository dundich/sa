namespace Sa.Configuration;

public class SaConfigurationException : Exception
{
    public SaConfigurationException() : base()
    {
    }

    public SaConfigurationException(string message) : base(message)
    {
    }

    public SaConfigurationException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
