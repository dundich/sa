# Secrets Class

The `Secrets` class provides a unified way to manage and retrieve sensitive information, such as API keys, database passwords, and other configuration settings, from various sources. It is designed to facilitate secure access to secrets in your application, allowing you to easily populate placeholders in strings with the corresponding secret values.

## Features
- Chained Secret Stores: The Secrets class can combine multiple secret stores, allowing you to retrieve secrets from different sources in a prioritized manner. This includes:
	- File-based Secrets: Load secrets from a primary secrets file (e.g., secrets.txt).
	- Environment-specific Secrets: Load secrets from environment-specific files (e.g., secrets.Development.txt), enabling different configurations for different environments.
	- Environment Variables: Retrieve secrets stored as environment variables, providing flexibility for deployment in various environments.
- Dynamic Host Key Handling: The class can dynamically load additional secret files based on a host key defined in the primary secrets file. This allows for environment-specific configurations that can be tailored to different deployment scenarios.
- Placeholder Replacement: The Secrets class allows you to populate strings with secret values by using placeholders. For example, you can define a string like "Database password: {{db_password}}", and the class will replace {{db_password}} with the actual value retrieved from the secret stores.


## Example

Here’s a simple example demonstrating how to use the Secrets class:

```csharp
string input = "API Key: {{api_key}}, Database Password: {{db_password}}";
string? result = Secrets.Service.PopulateSecrets(input);

Console.WriteLine(result); // Outputs: "API Key: my_api_key, Database Password: my_db_password"
```

## Configuration Loading Order for Secret Information
Your configuration system follows a hierarchical approach for loading secret information from configurations `.txt` files. Here’s the loading order:

- `secrets.txt`: The base file containing common secrets applicable to all environments and hosts.
- `secrets.{Env}.txt`: An environment-specific file (e.g., secrets.Development.txt or secrets.Production.txt) that overrides values from secrets.txt.
- `secrets.{sa_host_key}.txt`: A host-specific file (e.g., secrets.HostA.txt) that can also override values from the previous files.
- `secrets.{sa_host_key}.{Env}.txt`: The most specific file that combines both host key and environment (e.g., secrets.HostA.Development.txt). This file has the highest priority and overrides all previous values.

### Example 

secrets.txt
```
# Host key
# example: `dev|company1|host2`

sa_host_key="dev"

# Postgres

sa_pg_user=user
sa_pg_password=password
sa_pg_host=localhost
sa_pg_port=5432
sa_pg_database=postgres
sa_pg_schema=public


# ElasticSearch
sa_es_connection=http://login:password@localhost:9202
```

appsettings.json
```
{
    "pg": {
      "connection": "User ID={{sa_pg_user}};Password={{sa_pg_password}};Host={{sa_pg_host}};Port={{sa_pg_port}};Database={{sa_pg_database}};Pooling=true;SearchPath={{sa_pg_schema}};Command Timeout=180;"
    },
    "es": {
      "connection": "{{sa_es_connection}}"
    }
}
```

## Conclusion
The Secrets class simplifies the management of sensitive information in your application, providing a flexible and secure way to access secrets from various sources. By using this class, you can ensure that your application remains configurable and secure across different environments.