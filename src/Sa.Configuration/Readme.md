# Sa.Configuration


## Arguments Class
The Arguments class is designed to parse command-line arguments passed to a C# application. It allows you to easily retrieve parameter values using a dictionary-like interface. This class supports both single-value and multi-value parameters, making it flexible for various command-line configurations.

Key Features:
- Parameter Parsing: Splits command-line arguments into key-value pairs.
- Easy Retrieval: Access parameter values using an indexer.
- Default Handling: Automatically assigns default values for boolean flags.

### Example Usage:
```csharp
// some.exe --config_db /share/data.db
var arguments = new Arguments(args);
string? configDb = arguments["config_db"];
```


## Secrets Class
The Secrets class provides a secure way to manage sensitive information, such as API keys and database passwords, from various sources. It supports loading secrets from files, environment variables, and dynamically generated host key files.

Key Features:
- Chained Secret Stores: Combines multiple sources for retrieving secrets.
- Dynamic Loading: Supports environment-specific configurations.
- Placeholder Replacement: Easily populates strings with secret values.

### Example Usage:
```csharp
string input = "Database password: {{db_password}}";
string? populatedString = Secrets.Service.PopulateSecrets(input);
```
