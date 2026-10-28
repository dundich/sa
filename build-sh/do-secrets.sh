#!/usr/bin/env bash
#
# Encrypt, decrypt and check Sa.Configuration secrets files.
# Thin wrapper around build-sh/tools/secrets.cs (a .NET 10 file-based app).
#
# Usage:
#   ./build-sh/do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets.lsr.dev.pub
#   ./build-sh/do-secrets.sh decrypt secrets.lsr.dev.pub
#   ./build-sh/do-secrets.sh check
#
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/tasks.sh"

exec dotnet run --file "$ROOT/build-sh/tools/secrets.cs" -- "$@"
