# build scripts (bash)

Alternative to PowerShell `build/` — these scripts work on Linux/macOS.

## Usage

```bash
# Build from scratch (clean + restore + build)
./do-build.sh

# Run tests
./do-test.sh

# Create NuGet packages
./do-package.sh

# Push packages to local NuGet source
./do-push-local.sh

# Push packages to nuget.org
./do-push-prod.sh

# Enable long paths on Linux (create /sa symlink)
sudo ./do-fix-max-path-linux.sh
```

## Notes

- All scripts use `set -euo pipefail` for strict error handling.
- `dotnet test` runs from the `src/` directory (required by .NET 10 MTP runner).
- Projects to package are defined in `tasks.sh` — same list as the PowerShell version.
- The `local` NuGet source must be configured separately (`dotnet nuget add source ...`).
