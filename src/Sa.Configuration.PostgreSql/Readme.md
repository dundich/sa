# Sa.Configuration.PostgreSql

The `AddPostgreSqlConfiguration` extension method allows you to add a PostgreSQL-based configuration source to an IConfigurationBuilder. This enables your application to load configuration settings directly from a PostgreSQL database.

## Key Components
- PostgreSqlConfigurationOptions: A record that holds the connection string, SQL query, and optional parameters for querying the database.
- DatabaseConfigurationSource: Implements IConfigurationSource and creates a DatabaseConfigurationProvider to fetch configuration data.
- DatabaseConfigurationProvider: Inherits from ConfigurationProvider and overrides the Load method to execute the SQL query and populate the configuration data.

## Example Usage
```csharp
using Microsoft.Extensions.Configuration;
using Sa.Configuration.PostgreSql;

var builder = new ConfigurationBuilder();

// Define PostgreSqlConfigurationOptions
var options = new PostgreSqlConfigurationOptions(
    "Host=my_host;Database=my_db;Username=my_user;Password=my_pw",
    "SELECT key, value FROM configuration"
);

// Add PostgreSQL configuration to the builder
builder.AddSaPostgreSqlConfiguration(options);

// Build the configuration
var configuration = builder.Build();

// Access configuration values
string setting1 = configuration["Setting1"];
Console.WriteLine($"Setting1: {setting1}");
```
