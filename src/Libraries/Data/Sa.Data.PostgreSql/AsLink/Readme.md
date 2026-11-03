# AsLink — shared sources linked into Sa.Data.PostgreSql

This folder contains no sources of its own. It is the `Link` target for the shared `Sa` files that
`Sa.Data.PostgreSql` compiles directly:

- `..\..\Sa\Classes\Retry.cs` → `AsLink\Retry.cs`
- `..\..\Sa\Extensions\ExceptionExtensions.cs` → `AsLink\ExceptionExtensions.cs`

The list lives in `Sa.Data.PostgreSql.csproj`; this readme only keeps the folder in git. Edit the
shared file under `src/Libraries/Sa/` — the change then reaches every package that links it.
