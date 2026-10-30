#!/usr/bin/env bash
#
# Push the packages produced by ./do-package.sh to nuget.org.
#
# The API key is read from NUGET_API_KEY and passed via --api-key, which is the only form
# `dotnet nuget push` accepts without an <apikeys> entry in NuGet.config. The key is never echoed.
#
# Usage:
#   NUGET_API_KEY=... ./build-sh/do-push-prod.sh
#
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/tasks.sh"

if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "ERROR: NUGET_API_KEY не задан." >&2
  echo "       nuget.org требует ключ: NUGET_API_KEY=... ./build-sh/do-push-prod.sh" >&2
  echo "       для ключа, переданного аргументом, есть ./build-sh/do-push-prod-ex.sh" >&2
  exit 1
fi

nu_push "https://api.nuget.org/v3/index.json" "$NUGET_API_KEY"
