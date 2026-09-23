# Secrets Class

The `Secrets` class provides a unified way to manage and retrieve sensitive information, such as API keys, database passwords, and other configuration settings, from various sources. It is designed to facilitate secure access to secrets in your application, allowing you to easily populate placeholders in strings with the corresponding secret values.

## Features

- **Chained Secret Stores**: multiple secret stores combined with LIFO priority — the store passed or added last wins. `AddStore` is thread-safe (atomic snapshot swap). Built-in stores:
    - File-based Secrets: load secrets from a primary secrets file (e.g., `secrets.txt`).
    - Environment-specific Secrets: load secrets from an environment-specific file (e.g., `secrets.Development.txt`), enabling different configurations per environment.
    - Environment Variables: retrieve secrets stored as environment variables.
    - Command-line Arguments: retrieve secrets from CLI arguments via `Arguments`.
- **Placeholder Replacement**: populate strings with secret values using `{{key}}` placeholders, e.g., `"Database password: {{db_password}}"`.
- **Optional Placeholders**: `{{?key}}` — if the secret is not found, the whole string becomes `null` instead of throwing.
- **Cycle Protection**: nested placeholders resolve up to a depth of 3; circular references throw `InvalidOperationException`.
- **Value Normalization**: secret values are trimmed; surrounding quotes (`'`, `"`, `` ` ``) are stripped.

## Example

```csharp
using Sa.Configuration.SecretStore;

var secrets = Secrets.CreateDefault();

string input = "API Key: {{api_key}}, Database Password: {{db_password}}";
string? result = secrets.PopulateSecrets(input);

Console.WriteLine(result); // Outputs: "API Key: my_api_key, Database Password: my_db_password"
```

## Configuration Loading Order

The default chain (`Secrets.CreateDefault()`) is resolved with LIFO priority — the last store wins:

1. `secrets.txt` — the base file containing common secrets for all environments.
2. `secrets.{Env}.txt` — an environment-specific file (e.g., `secrets.Development.txt`) that overrides values from `secrets.txt`.
3. Environment variables — override both files.
4. Command-line arguments — the highest priority; override everything.

The environment name is resolved by `Secrets.GetEnvironmentName()` from `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` / `environment`, falling back to `Production`.

### Example

secrets.txt
```
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
