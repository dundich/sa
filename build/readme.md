# build packages

sa\build>

```bash
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process

dotnet nuget add source "C:\source\nuget" --name "local"

.\do_package.ps1

.\do_push_local.ps1

```
