# Secrets Class

The `Secrets` class provides a unified way to manage and retrieve sensitive information, such as API keys, database passwords, and other configuration settings, from various sources. It is designed to facilitate secure access to secrets in your application, allowing you to easily populate placeholders in strings with the corresponding secret values.

## Features

- **Chained Secret Stores**: multiple secret stores combined with LIFO priority — the store passed or added last wins. `AddStore` is thread-safe (atomic snapshot swap). Built-in stores:
    - File-based Secrets: load secrets from a primary secrets file (e.g., `secrets.txt`).
    - Environment-specific Secrets: load secrets from an environment-specific file (e.g., `secrets.Development.txt`), enabling different configurations per environment.
    - Encrypted Secrets: load committed `secrets_enc*.txt` files encrypted with AES-256-GCM (see below).
    - `.env` files: `KEY=VALUE`, `export KEY=VALUE`, quotes, comments.
    - Environment Variables: retrieve secrets stored as environment variables.
    - Command-line Arguments: retrieve secrets from CLI arguments via `Arguments`.
- **Placeholder Replacement**: populate strings with secret values using `{{key}}` placeholders, e.g., `"Database password: {{db_password}}"`.
- **Optional Placeholders**: `{{?key}}` — if the secret is not found, the whole string becomes `null` instead of throwing.
- **Cycle Protection**: nested placeholders resolve up to a depth of 3; circular references throw `InvalidOperationException`.
- **Value Normalization**: secret values are trimmed; surrounding quotes (`'`, `"`, `` ` ``) are stripped.

## Configuration Loading Order

The default chain (`Secrets.CreateDefault()`) is resolved with LIFO priority — the last store wins:

| # | Source | Role |
|---|--------|------|
| 1 | `secrets.txt` | plaintext defaults |
| 2 | `secrets.{env}.txt` | plaintext per environment |
| 3 | `secrets_enc.txt` | encrypted defaults |
| 4 | `secrets_enc.{env}.txt` | encrypted per environment |
| 5 | `secrets_enc.{client}.txt` | encrypted per client |
| 6 | `secrets_enc.{client}.{env}.txt` | encrypted per client and environment |
| 7 | `.env` | local overrides; also holds the decryption key |
| 8 | `.env.{env}` | local per-environment overrides |
| 9 | Environment variables | override every file |
| 10 | Command-line arguments | highest priority |

`{env}` is the exact value of `Secrets.GetEnvironmentName()` (`DOTNET_ENVIRONMENT` → `ASPNETCORE_ENVIRONMENT` → `environment`, falling back to `Production`). To use short names such as `dev`, set `DOTNET_ENVIRONMENT=dev` — both plaintext and encrypted file names follow it.

File paths are resolved against the current working directory first and `AppContext.BaseDirectory` second.

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

## Encrypted Secrets

Encrypted files are meant to be committed. Values are encrypted, while key names and comments stay readable, so diffs and merges remain usable.

### File Format

```
# saenc: v1
# saenc: kdf=pbkdf2-sha256
# saenc: iterations=600000
# saenc: salt=1sV7...==
# saenc: client=lsr
# saenc: env=Development
# Postgres
sa_pg_password=ENC1:QmFzZTY0...
```

- Each value is `ENC1:<base64>` where the blob is `nonce(12) || tag(16) || ciphertext`.
- AES-256-GCM; the per-file key is `PBKDF2-SHA256(passphrase, salt, iterations, 32)` with a random 16-byte salt.
- The key name is authenticated as associated data (`SAENC1|<keyName>`), so a value cannot be moved to another key.
- A raw 32-byte key can be used instead of a passphrase: prefix it with `base64:`; the header then says `kdf=none`.
- The `client` / `env` header entries are validated against the file name. A mismatch fails startup.
- Encrypted files must contain only `ENC1:` values. A plaintext `key=value` line is rejected — non-secrets belong in `secrets*.txt`.
- Comments and blank lines are preserved verbatim; duplicate keys keep the last value.

### Configuration

| Variable | Meaning |
|----------|---------|
| `SA_SECRETS_DECRYPTION_CLIENT` | Client or comma/semicolon separated list. Later clients win. |
| `SA_SECRETS_DECRYPTION_KEY` | Global key (passphrase or `base64:...`). |
| `SA_SECRETS_DECRYPTION_KEY_{CLIENT}` | Per-client key; takes precedence over the global one. |
| `SA_SECRETS_DECRYPTION_KEY_FILE` | File containing the global key. |
| `SA_SECRETS_DECRYPTION_KEY_FILE_{CLIENT}` | File containing a per-client key. |

The same settings can be supplied through `SecretOptions.Encryption` (`SecretEncryptionOptions`). A real environment variable always wins over `.env`.

### Creating and Checking Files

The repository ships a script (a thin wrapper around a .NET 10 file-based app):

```bash
# Encrypt secrets.txt for client "lsr" and environment "dev"
SA_SECRETS_DECRYPTION_KEY=... \
  ./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets_enc.lsr.dev.txt

# Print the decrypted content
./build-sh/do-secrets.sh decrypt secrets_enc.lsr.dev.txt

# Verify every encrypted file in the current directory (CI-friendly, non-zero exit on failure)
./build-sh/do-secrets.sh check
```

The key is never accepted as a command-line value — use an environment variable, `--key-file`, or interactive input. `SA_SECRETS_DECRYPTION_KEY=<key>` in `.env` also works locally.

`SecretsCipher` exposes the same functionality for custom tooling: `Encrypt`, `Decrypt`, `ReadHeader`, `SecretsFileHeader`.

### Failure Behaviour

| Situation | Behaviour |
|-----------|-----------|
| Encrypted file exists, no key | `InvalidOperationException` naming the file and the expected variable |
| Wrong key / corrupted file | `InvalidOperationException`, no fallback to lower-priority stores |
| Header `client` / `env` mismatch | `InvalidOperationException` |
| Key configured, no files | no-op |
| Client configured, its file missing | no-op |
| `SecretOptions.Encryption.Enabled = false` | encrypted files are ignored |

### Security Notes

- PBKDF2-SHA256 defaults to 600 000 iterations (about 100–200 ms per file at startup), minimum 100 000.
- A random nonce is generated per value; a random salt per file.
- Key names and approximate value lengths are visible in git. Do not use this format if that metadata is sensitive.
- A passphrase is held in a managed `string` and cannot be zeroed; prefer `base64:` raw keys or a key file.
- The key is read once at startup — rotation requires a restart.
- The format is not compatible with SOPS, git-crypt or age.

## Conclusion

The Secrets class simplifies the management of sensitive information in your application, providing a flexible and secure way to access secrets from various sources. By using this class, you can ensure that your application remains configurable and secure across different environments.
