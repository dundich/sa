# Arguments — Command-Line Parsing

Parse and consume command-line arguments in .NET apps with a simple dictionary-like API. Supports `--flag=value`, `--flag value`, short options (`-x`), and typed getters.

> **Key detail:** the parser strips leading dashes from parameter names. When you access a value, use the key **without** leading `-` or `--`.
> Example: `--config_db` in CLI → `args["config_db"]` in code. `-v` in CLI → `args["v"]` in code.

## Quick Start

```csharp
using Sa.Configuration.CommandLine;

// Parse args (defaults to Environment.GetCommandLineArgs())
var args = new Arguments(args);

// Indexer access (returns null if absent) — keys have leading dashes stripped
string? db    = args["config_db"];
string? file  = args["config_file"];
bool    debug = args.IsPresent("debug");   // true if flag present & truthy

// Typed helpers (return nullable, null on missing/invalid)
int?     port    = args.GetInt("port");
float?   timeout = args.GetFloat("timeout");
long     offset  = args.GetLong("offset");
TimeSpan ttl     = args.GetTimeSpan("ttl");
bool     verbose = args.GetBool("v");       // "true"/"1"/"yes"/"on" → true
```

### Minimal Console App

```csharp
using Sa.Configuration.CommandLine;

var arguments = new Arguments(args);

Console.WriteLine($"DB:    {arguments["db"]    ?? "(default)"}");
Console.WriteLine($"Port:  {arguments.GetInt("port")    ?? 5432}");
Console.WriteLine($"Debug: {arguments.IsPresent("debug")}");
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

## Supported Formats

| Format | CLI Input | Dictionary Key | Value |
|--------|-----------|---------------|-------|
| Long flag + space | `--key value` | `"key"` | `"value"` |
| Equals sign | `--key=value` | `"key"` | `"value"` |
| Short flag + space | `-k value` | `"k"` | `"value"` |
| Short equals | `-k=v` | `"k"` | `"v"` |
| Boolean flag | `--debug` | `"debug"` | `"true"` |
| Quoted value | `--name "hello world"` | `"name"` | `"hello world"` |

---

## API Reference

### Constructor

```csharp
public Arguments(params IReadOnlyList<string> args)
```

Creates an instance from a list of argument strings.

### Static Factory

```csharp
public static Arguments CreateDefault(string[]? args = null)
```

Shortcut that uses `Environment.GetCommandLineArgs()` when `args` is null.

```csharp
var args = Arguments.CreateDefault();   // reads Process.GetCurrentProcess().CommandLine
```

### Indexer

```csharp
public string? this[string param] { get; }
```

Returns the value for a parameter name, or `null` if not found. Keys are stored without leading dashes.

```csharp
var db = args["database"];   // null if --database was never passed
```

### Contains / IsPresent

```csharp
public bool Contains(string param)           // true if key exists (even if value is empty)
public bool IsPresent(string param)          // true if key exists AND value is non-null
```

`IsPresent` distinguishes between a missing flag and a present-but-empty flag.

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
| Unknown format | Ignored silently | — |
