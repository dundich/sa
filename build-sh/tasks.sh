#!/usr/bin/env bash
#
# Common functions and configuration for build scripts.
# Sourced by do-*.sh scripts — not meant to be executed directly.
#

set -euo pipefail

# Resolve paths relative to this script's directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SLN_FILE="$ROOT/src/Sa.slnx"
SRC_DIR="$ROOT/src"
DIST_FOLDER="$ROOT/dist"
MSBUILD_VERBOSITY="n"

# List of projects to package (order mirrors tasks.ps1)
PROJECTS=(
  "Sa.Utils.WorkQueue"

  "Sa.Media"
  "Sa.Media.FFmpeg"

  "Sa.Data.PostgreSql"
  "Sa.Data.S3"

  "Sa.Configuration"
  "Sa.Configuration.PostgreSql"

  "Sa.Schedule"

  "Sa.Partitional.PostgreSql"

  "Sa.Outbox"
  "Sa.Outbox.PostgreSql"

  "Sa.HybridFileStorage"
  "Sa.HybridFileStorage.FileSystem"
  "Sa.HybridFileStorage.Postgres"
  "Sa.HybridFileStorage.S3"
)

# ── Helpers ──────────────────────────────────────────────────────

_assert_exec() {
  # Exit with code 1 if the previous command failed
  if [[ $? -ne 0 ]]; then
    exit 1
  fi
}

_step() {
  echo ""
  echo -e "\033[32m===== $1 =====\033[0m"
}

# ── Core tasks ───────────────────────────────────────────────────

nu_restore() {
  _step "Restore NuGet packages"
  dotnet restore "$SLN_FILE" --verbosity "$MSBUILD_VERBOSITY" || { _assert_exec; return; }
  _assert_exec
}

_msbuild() {
  _step "$1 solution"
  dotnet build "$SLN_FILE" -c Release -v "$MSBUILD_VERBOSITY" || { _assert_exec; return; }
  _assert_exec
}

clean() {
  _step "Clean folder $DIST_FOLDER"
  mkdir -p "$DIST_FOLDER"
  rm -rf "$DIST_FOLDER"/*
  _msbuild "Clean"
}

build() {
  clean
  nu_restore
  _msbuild "Build"
}

test_run() {
  _step "Running tests"
  # `dotnet test` picks the MTP runner from src/global.json, which is only
  # discovered from src/ (or below). Run it with cwd=src or it fails with
  # "VSTest target is no longer supported on .NET 10 SDK".
  ( cd "$SRC_DIR" && dotnet test "$SLN_FILE" -v "$MSBUILD_VERBOSITY" ) || { _assert_exec; return; }
  _assert_exec
}

test_ci() {
  _step "Running tests (skipping tests requiring local infrastructure)"
  ( cd "$SRC_DIR" && dotnet test "$SLN_FILE" --filter "Category!=Local" ) || { _assert_exec; return; }
  _assert_exec
}

nu_pack() {
  for project in "${PROJECTS[@]}"; do
    _step "Package project $project"
    dotnet pack "$ROOT/src/$project/$project.csproj" \
      --output "$DIST_FOLDER" \
      --configuration Release \
      -p:IncludeSymbols=true \
      -p:SymbolPackageFormat=snupkg || { _assert_exec; return; }
    _assert_exec
  done
}

nu_push() {
  local nuget_source="$1"
  _step "Push packages to $nuget_source"

  # Find both *.nupkg and *.snupkg files
  while IFS= read -r -d '' package; do
    _step "Push $(basename "$package") to $nuget_source"
    dotnet nuget push "$package" --source "$nuget_source" || { _assert_exec; return; }
    _assert_exec
  done < <(find "$DIST_FOLDER" -maxdepth 1 \( -name "*.nupkg" -o -name "*.snupkg" \) -print0 | sort -z)
}

nu_push_ex() {
  local nuget_key="$1"
  local nuget_source="https://api.nuget.org/v3/index.json"
  _step "Push packages to $nuget_source (key=$nuget_key)"

  while IFS= read -r -d '' package; do
    _step "Push $(basename "$package") to $nuget_source"
    dotnet nuget push "$package" -k "$nuget_key" -s "$nuget_source" || { _assert_exec; return; }
    _assert_exec
  done < <(find "$DIST_FOLDER" -maxdepth 1 \( -name "*.nupkg" -o -name "*.snupkg" \) -print0 | sort -z)
}

package() {
  build
  nu_pack
}
