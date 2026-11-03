# AsLink — shared sources linked into Sa.Partitional.PostgreSql

This folder contains no sources of its own. It is the `Link` target for the shared `Sa` files that
`Sa.Partitional.PostgreSql` compiles directly:

- `..\..\Sa\Classes\Retry.cs` → `AsLink\Retry.cs`
- `..\..\Sa\Classes\Section.cs` → `AsLink\Section.cs`
- `..\..\Sa\Extensions\DateTimeExtensions.cs` → `AsLink\DateTimeExtensions.cs`
- `..\..\Sa\Extensions\EnumerableExtensions.cs` → `AsLink\EnumerableExtensions.cs`
- `..\..\Sa\Extensions\ExceptionExtensions.cs` → `AsLink\ExceptionExtensions.cs`
- `..\..\Sa\Extensions\NumericExtensions.cs` → `AsLink\NumericExtensions.cs`
- `..\..\Sa\Extensions\StrToExtensions.cs` → `AsLink\StrToExtensions.cs`

The list lives in `Sa.Partitional.PostgreSql.csproj`; this readme only keeps the folder in git. Edit
the shared file under `src/Libraries/Sa/` — the change then reaches every package that links it.
