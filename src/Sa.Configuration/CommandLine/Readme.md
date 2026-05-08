# Arguments Class

The `Arguments` class provides a robust way to parse command-line arguments, making it easier to manage application configurations. It handles various parameter formats and provides helper methods for different data types.

## Features

- Parse command-line arguments with support for various formats
- Access parameters by name using indexer syntax
- Helper methods for common data types (bool, int, float, long, TimeSpan)
- Support for both `--param=value` and `--param value` formats
- Support for short options like `-ip_override=127.0.0.1`

## Usage

### Basic Usage

To use the Arguments class, create an instance and access parameters using the indexer syntax:

```csharp
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
```

### Advanced Usage

The class also provides helper methods for different data types:

```csharp
// Check if a parameter exists
if (arguments.Contains("--config_db"))
{
    // Get parameter as boolean
    bool? nosjmp = arguments.GetBool("-nosjmp");

    // Get parameter as integer
    int? port = arguments.GetInt("--port");

    // Get parameter as float
    float? timeout = arguments.GetFloat("--timeout");

    // Get parameter as TimeSpan
    TimeSpan? duration = arguments.GetTimeSpan("--duration");
}
```

## Running the Application

To run the application with command-line arguments, you can use the command line or terminal. Here are examples:

```bash
# Standard usage
dotnet run Some.exe --config_db /opt/service_configs/config_db.json --config_file /opt/service_configs/appsettings.json --config_nlog /opt/service_configs/NLog.config -ip_override=127.0.0.1 -nosjmp

# Alternative format
dotnet run Some.exe --config_db=/opt/service_configs/config_db.json --config_file=/opt/service_configs/appsettings.json --config_nlog=/opt/service_configs/NLog.config -ip_override="127.0.0.1" -nosjmp
```