# Secrets Class

**English** | [Русский](Readme-ru.md)

The `Secrets` class provides a unified way to manage and retrieve sensitive information, such as API keys, database passwords, and other configuration settings, from various sources. It is designed to facilitate secure access to secrets in your application, allowing you to easily populate placeholders in strings with the corresponding secret values.

## Contents

- [Features](#features)
- [Configuration Loading Order](#configuration-loading-order)
  - [Example](#example)
- [Dynamic Resolution](#dynamic-resolution)
- [Encrypted Secrets](#encrypted-secrets)
  - [File Format](#file-format)
  - [Configuration](#configuration)
  - [Creating and Checking Files](#creating-and-checking-files)
  - [Failure Behaviour](#failure-behaviour)
  - [Security Notes](#security-notes)
- [Encodings and value format](#encodings-and-value-format)
- [Conclusion](#conclusion)

## Features

- **Chained Secret Stores**: multiple secret stores combined with LIFO priority — the store passed or added last wins. `AddStore` is thread-safe (atomic snapshot swap). Built-in stores:
    - File-based Secrets: load secrets from a primary secrets file (e.g., `secrets.txt`).
    - Environment-specific Secrets: load secrets from an environment-specific file (e.g., `secrets.Development.txt`), enabling different configurations per environment.
    - Encrypted Secrets: load committed `secrets*.pub` files encrypted with AES-256-GCM (see below).
    - `.env` files: `KEY=VALUE`, `export KEY=VALUE`, quotes, comments.
    - Environment Variables: retrieve secrets stored as environment variables.
    - Command-line Arguments: retrieve secrets from CLI arguments via `Arguments`.
- **Placeholder Replacement**: populate strings with secret values using `{{key}}` placeholders, e.g., `"Database password: {{db_password}}"`.
- **Optional Placeholders**: `{{?key}}` — if the secret is not found, the whole string becomes `null` instead of throwing.
- **Dynamic Resolution**: `SecretOptions.Resolution.Resolver` is a last-chance callback invoked when no store has the key — for Vault, an HTTP call or a cache. `OnUnresolvedSecret` reports the key (never the value) for logging and metrics.
- **Cycle Protection**: nested placeholders resolve up to a depth of 3; circular references throw `InvalidOperationException`.
- **Value Normalization**: secret values are trimmed; surrounding quotes (`'`, `"`, `` ` ``) are stripped.

## Configuration Loading Order

The default chain (`Secrets.CreateDefault()`) is resolved with LIFO priority — the last store wins:

| # | Source | Role |
|---|--------|------|
| 1 | `secrets.txt` | plaintext defaults |
| 2 | `secrets.{env}.txt` | plaintext per environment |
| 3 | `secrets.pub` | encrypted defaults |
| 4 | `secrets.{env}.pub` | encrypted per environment |
| 5 | `secrets.{client}.pub` | encrypted per client |
| 6 | `secrets.{client}.{env}.pub` | encrypted per client and environment |
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

## Dynamic Resolution

Stores are checked first. When every store misses, the optional `Resolver` callback gets a last chance — this is where Vault, an HTTP call or a cache belongs:

```csharp
SecretOptions options = new()
{
    Resolution = new SecretResolutionOptions
    {
        Resolver = key => vault.TryGet(key, out string? value) ? value : null,
        OnUnresolvedSecret = miss => logger.LogWarning(
            "Secret {Key} was not resolved (optional: {Optional})", miss.Key, miss.IsOptional),
    },
};
```

Resolution order for one placeholder:

1. the store chain (LIFO — the last store wins);
2. `Resolution.Resolver`;
3. if the resolver returned `null`, the `?` marker in `{{?key}}` (or `returnNullIfSecretNotFound: true`) decides between `null` and an exception.

`OnUnresolvedSecret` fires only during placeholder substitution, after the resolver has missed and before the `null`/exception decision; it receives the key and whether the miss is optional, never the secret value.

> The configuration pipeline calls `PopulateSecrets(..., returnNullIfSecretNotFound: true)`, so an unresolved configuration placeholder becomes `null` and still reports through `OnUnresolvedSecret`. Direct calls are strict unless the placeholder is `{{?key}}` or the flag is set.

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

A complete example — the same `secrets.txt`, encrypted for client `lsr` and environment `dev`:

```
# saenc: v1
# saenc: kdf=pbkdf2-sha256
# saenc: iterations=600000
# saenc: salt=dFgi+38h+4oDUbhsLEUaXg==
# saenc: client=lsr
# saenc: env=dev
# Postgres
sa_pg_host=ENC1:RkVQFBmvvoUBMLkGSXznSaS9mW0h9MVPltZIz4Oou9Q/PEjWiw==
sa_pg_user=ENC1:pBA9GEfkNcZ8+OxP2KWlsfgzDigAq/NWopcYW4GKH4+Qo5Zr
sa_pg_password=ENC1:8zPuIVo2/qAa9OO9mafijG1OYuq1Ku6i7Hg8QRoG2Q8n25dtvb0P7Z1x

# API keys
api_key=ENC1:Nzz7pPfq4K6rrAixJWQhN37Inu/n6jL+rkFGpJbKyfqAuOs4Aw==
jwt_secret=ENC1:MHP6RX5G7Xc5v+QaSkdhaxVRDRQg8NfCr4OjJzw0BhNEFFgv
```

What stays readable: keys, comments, header. What gets encrypted: only the values. This block was generated by `do-secrets.sh` with the one-time passphrase `demo-passphrase-not-a-real-secret`, so `decrypt` and `check` can be tested directly on it.

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
  ./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets.lsr.dev.pub

# Print the decrypted content
./build-sh/do-secrets.sh decrypt secrets.lsr.dev.pub

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

## Encodings and value format

Everything runs in UTF-8. The only "non-UTF-8" token in a file is the Base64 payload (ciphertext).

| What | How it works |
|------|--------------|
| File encoding | UTF-8. BOM is allowed and ignored. Files without BOM are read as UTF-8 — if yours are CP1251/CP866, re-save them as UTF-8. |
| Line endings | LF, CRLF, and CR are read correctly. When writing (`SecretsCipher` / `do-secrets.sh`), LF is always used, so re-encryption doesn't break diffs. |
| How a value becomes ciphertext | UTF-8 bytes → AES-256-GCM → `nonce(12) ‖ tag(16) ‖ ciphertext` → standard Base64 on one line. |
| Value prefix | `ENC1:` — format version 1 marker. It isn't authenticated itself; the key name is authenticated, so the blob can't be moved to another key. |
| Payload length | `4 × ceil((28 + UTF-8 byte count) / 3)`. Examples: `localhost` (9 bytes) → 52 chars, `superSecret123` (14 bytes) → 56, `Привет` (12 bytes) → 56. |
| Unicode | Keys and values round-trip as UTF-8: `Привет`, `pä£s=word`, emoji. Non-ASCII takes more bytes — hence the longer blob. |
| `=` inside values | Allowed. The line is split on the first `=`, so `api_key=abc=123` is preserved intact. |
| Empty values | Encrypted and restored empty (`empty=`), no error. |
| Whitespace | Keys and values are trimmed at the edges (` key = value ` → `key=value`). Internal spaces are kept. |
| Comments | A line that starts with `#` after trim is copied verbatim. A `# saenc:` comment in the source file is rejected as "already encrypted". |
| Header metadata | `client` / `env` are trimmed, must not contain line breaks, and are checked against the file name on load. |
| Passphrase | Any UTF-8 text, trimmed. Spaces and non-ASCII are allowed. |
| "Raw" key | `base64:` + Base64 exactly 32 bytes (44 chars with padding). Any other length → `32 bytes` error. |
| Salt | Base64 16 random bytes (24 chars) in the `# saenc: salt=` line. Length is checked, so editing the salt by hand breaks decryption. |
| Case | Key names, `ENC1:`, `base64:`, and header fields are case-sensitive. `# saenc: kdf=PBKDF2-SHA256` is not accepted. |
| Placeholder names | `{{key}}` matches `\w+` (Unicode letters, digits, underscore). Hyphens, dots, and spaces don't match — `{{api-key}}` stays as-is. |
| What gets substituted | The value is trimmed and stripped of quotes `'`, `"`, `` ` `` before being inserted into the configuration string. |

`.env` (which stores the decryption key) has its own rules: `KEY=VALUE`, `export KEY=VALUE`, `#` comments, optional UTF-8 BOM, CRLF/LF, quotes `'`, `"`, `` ` ``, and escaping of `\n`, `\r`, `\t`, `\"`, `\\` inside double quotes. Unknown escape sequences such as the Windows path `"C:\Users"` keep the backslash.

## Conclusion

The Secrets class simplifies the management of sensitive information in your application, providing a flexible and secure way to access secrets from various sources. By using this class, you can ensure that your application remains configurable and secure across different environments.
