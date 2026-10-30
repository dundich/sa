#!/usr/bin/env bash
#
# Push the packages produced by ./do-package.sh into the repository-local feed (src/nupkgs).
# That is the same folder src/nuget.config maps for `Sa.*`, so no machine-level NuGet source
# has to be registered.
#
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/tasks.sh"

mkdir -p "$ROOT/src/nupkgs"
nu_push "$ROOT/src/nupkgs"
