# Arguments Class

The `Arguments` class provides a robust way to parse command-line arguments, making it easier to manage application configurations. The improvements made enhance its functionality and maintainability. You can further


Running the Application

To run the application with command-line arguments, you can use the command line or terminal. Here’s an example command to run the compiled executable:

```bash
dotnet run Some.exe --config_db /opt/service_configs/config_db.json --config_file /opt/service_configs/appsettings.json --config_nlog /opt/service_configs/NLog.config -ip_override=127.0.0.1 -nosjmp
```

```csharp
class Program
{
    static void Main(string[] args)
    {
        // Create an instance of the Arguments class, passing the command-line arguments
        var arguments = new Arguments(args);

        // Retrieve values for specific parameters
        string? configDb = arguments["--config_db"];
        string? configFile = arguments["--config_file"];
        string? configNLog = arguments["--config_nlog"];
        string? ipOverride = arguments["-ip_override"];

        // Display the retrieved values
        Console.WriteLine("Configuration Database: " + (configDb ?? "Not provided"));
        Console.WriteLine("Configuration File: " + (configFile ?? "Not provided"));
        Console.WriteLine("NLog Configuration: " + (configNLog ?? "Not provided"));
        Console.WriteLine("IP Override: " + (ipOverride ?? "Not provided"));
    }
}
```