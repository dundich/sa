# Arguments — Command-Line Parsing

Parse and consume command-line arguments in .NET apps with a simple dictionary-like API. Supports `--flag=value`, `--flag value`, short options (`-x`), and typed getters.

## Features

- **Dictionary-like API**: access parsed arguments with `args["key"]` and typed getters
- **Multiple formats**: `--flag=value`, `--flag value`, short options (`-x`)
- **`__` as `:`**: a double underscore in a parameter name is the environment-variable spelling of the configuration separator, so `--sa__pg__host` is read as `sa:pg:host`
- **Typed getters**: `GetBool`, `GetInt`, `GetFloat`, `GetLong`, `GetTimeSpan`

Tokens that look like negative numbers (e.g. `-5`, `--1.5`) are treated as **values**, not flags.

> **Key detail:** the parser strips leading dashes from parameter names. When you access a value, use the key **without** leading `-` or `--`.
> Example: `--config_db` in CLI → `args["config_db"]` in code. `-v` in CLI → `args["v"]` in code.

> **`__` as `:`:** a double underscore in a parameter name is the environment-variable spelling of the
> configuration separator, so `--sa__pg__host` is stored and read as `sa:pg:host` — the same convention
> `AddEnvironmentVariables()` applies. See [Key Separators](#key-separators).

## Quick Start

```csharp
using Sa.Configuration.CommandLine;

// Parse args (defaults to Environment.GetCommandLineArgs())
var args = new Arguments(args);

// Indexer access (returns null if absent) — keys have leading dashes stripped
string? db    = args["config_db"];
string? file  = args["config_file"];
bool?   debug = args.GetBool("debug");     // true/1/yes/on → true; null if absent

// Typed helpers (return nullable, null on missing/invalid)
int?     port    = args.GetInt("port");
float?   timeout = args.GetFloat("timeout");
long?    offset  = args.GetLong("offset");
TimeSpan? ttl    = args.GetTimeSpan("ttl");
bool?    verbose = args.GetBool("v");      // "true"/"1"/"yes"/"on" → true
```

### Minimal Console App

```csharp
using Sa.Configuration.CommandLine;

var arguments = new Arguments(args);

Console.WriteLine($"DB:    {arguments["db"]    ?? "(default)"}");
Console.WriteLine($"Port:  {arguments.GetInt("port")    ?? 5432}");
Console.WriteLine($"Debug: {arguments.GetBool("debug") == true}");
Console.WriteLine($"TTL:   {arguments.GetTimeSpan("ttl") ?? TimeSpan.Zero}");
```

Run:

```bash
dotnet run -- --db mydb --port 9999 --debug --ttl 00:05:00 -v
```

Output:

```
DB:    mydb
Port:  9999
Debug: True
TTL:   00:05:00
```

---

## Key Separators

A double underscore in a parameter name is the environment-variable spelling of the configuration
separator `:`, so it is stored and read as `:` — the same convention `AddEnvironmentVariables()`
applies:

| CLI Input | Key in the dictionary | Read as |
|-----------|----------------------|---------|
| `--sa__pg__host` | `"sa:pg:host"` | `args["sa:pg:host"]` or `args["sa__pg__host"]` |
| `--config_db` | `"config_db"` | unchanged — a single underscore is data, not a separator |

Storage is always in canonical `:` form, so `args.Parameters` never contains a `__` spelling.
Reads are normalized the same way, so both `args["sa__pg__host"]` and `args["sa:pg:host"]` resolve
to the same value, and the typed getters (`GetInt`, `GetBool`, …) follow the same rule. The
replacement is pairwise, exactly like the environment provider: four underscores become two
delimiters (`--a____b` → `a::b`).

Example:

```bash
dotnet run -- --sa__pg__host db.internal --sa__pg__port 5432 --config_db mydb
```

```csharp
var args = new Arguments(args);

args["sa:pg:host"];   // "db.internal"  — read as sa:pg:host
args["sa:pg:port"];   // "5432"         — read as sa:pg:port
args["config_db"];    // "mydb"         — single underscore is data, unchanged
```

---

## Supported Formats

| Format | CLI Input | Dictionary Key | Value |
|--------|-----------|---------------|-------|
| Long flag + space | `--key value` | `"key"` | `"value"` |
| Equals sign | `--key=value` | `"key"` | `"value"` |
| Short flag + space | `-k value` | `"k"` | `"value"` |
| Short equals | `-k=v` | `"k"` | `"v"` |
| Boolean flag | `--debug` | `"debug"` | `"true"` |
| Quoted value | `--name "hello world"` | `"name"` | `"hello world"` |
| Negative value | `--offset -5` | `"offset"` | `"-5"` |

---

## API Reference

### Constructor

```csharp
public Arguments(params string[] args)
```

Creates an instance from a list of argument strings.

### Static Factory

```csharp
public static Arguments CreateDefault(string[]? args = null)
```

Shortcut that uses `Environment.GetCommandLineArgs()` when `args` is null.

```csharp
var args = Arguments.CreateDefault();   // uses Environment.GetCommandLineArgs()
```

### Indexer

```csharp
public string? this[string param] { get; }
```

Returns the value for a parameter name, or `null` if not found. Keys are stored without leading dashes.

```csharp
var db = args["database"];   // null if --database was never passed
```

The key is normalized before the lookup, so `args["sa__pg__host"]` and `args["sa:pg:host"]` are
interchangeable — see [Key Separators](#key-separators).

### Contains

```csharp
public bool Contains(string param)           // true if key exists (even if value is empty)
```

### Typed Getters

All return `T?` (nullable) and yield `null` when the parameter is absent or unparsable.

| Method | Return Type | Example |
|--------|-------------|---------|
| `GetBool(string)` | `bool?` | `args.GetBool("verbose")` — accepts `true/1/yes/on` |
| `GetInt(string)`  | `int?`  | `args.GetInt("port")` |
| `GetFloat(string)`| `float?`| `args.GetFloat("ratio")` |
| `GetLong(string)` | `long?` | `args.GetLong("offset")` |
| `GetTimeSpan(string)` | `TimeSpan?` | `args.GetTimeSpan("delay")` |

All numeric parsing uses `CultureInfo.InvariantCulture`.

### Raw Parameters

```csharp
public IReadOnlyDictionary<string, string?> Parameters { get; }
```

Returns the full dictionary of parsed parameters. Keys are stored without leading dashes.

---

## Integration with Microsoft.Extensions.Configuration

Register command-line arguments as an `IConfiguration` source:

```csharp
using Microsoft.Extensions.Configuration;
using Sa.Configuration.CommandLine;

var configuration = new ConfigurationBuilder()
    .AddSaCommandLine(args)        // <-- adds CLI args as config source
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

// Access via IConfiguration indexer — keys still have dashes stripped
var db = configuration["db"];
var port = configuration["port"];
```

Like the indexer, `configuration["sa__pg__host"]` and `configuration["sa:pg:host"]` resolve the
same value, because the provider normalizes the requested key — see [Key Separators](#key-separators).

Order matters: sources registered **later** override earlier ones. Place `AddSaCommandLine` before JSON/file sources if you want CLI to win:

```csharp
new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")      // base defaults
    .AddSaCommandLine(args)                // overrides from CLI
    .Build();
```

---

## Running the Application

### From terminal

```bash
# Long flags with space separator
dotnet run --project MyApp.dll --db production --port 5432 --debug

# Equals syntax
dotnet run --project MyApp.dll --db=production --port=5432

# Mixed formats
dotnet run -- -d --db=prod -p 3306 --ttl 30s
```

### From Visual Studio / VS Code

Set arguments in launchSettings.json:

```json
{
  "profiles": {
    "MyApp": {
      "commandName": "Project",
      "commandLineArgs": "--db test --port 9999 --debug --ttl 00:01:00"
    }
  }
}
```

---

## Edge Cases

| CLI Input | Dictionary Key | Value |
|-----------|---------------|-------|
| `--flag` (no value) | `"flag"` | `"true"` |
| `--flag=` (empty) | `"flag"` | `""` |
| `--flag "quoted value"` | `"flag"` | `"quoted value"` |
| `-short=value` | `"short"` | `"value"` |
| `--key -5` | `"key"` | `"-5"` |
| `-1`, `--1`, `-1.5`, `-1.5` (token after a flag) | appended to the previous key | negative number value |
| Unknown format | Ignored silently | — |
