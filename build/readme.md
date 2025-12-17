# build packages

```bash
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process

dotnet nuget add source "C:\source\nuget" --name "local"

cd build

.\do_package.ps1

.\do_push_local.ps1

```
