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
CONFIG="Release"

# How many test assemblies `dotnet test` may run at the same time. Most test assemblies here start
# Testcontainers (PostgreSQL/Minio), so the default "all of them at once" means dozens of live
# database containers and the machine runs out of memory. Override per machine:
#   SA_TEST_PARALLELISM=6 ./build-sh/do-test.sh
MAX_PARALLEL_TEST_MODULES="${SA_TEST_PARALLELISM:-2}"

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
  dotnet build "$SLN_FILE" -c "$CONFIG" -v "$MSBUILD_VERBOSITY" || { _assert_exec; return; }
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

  # `dotnet test` picks the MTP runner from src/global.json, which is only discovered when the cwd
  # is src/ (or below). Everything below therefore runs from $SRC_DIR; from anywhere else it fails
  # with "VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK".
  #
  # --max-parallel-test-modules caps how many test assemblies run at once. Without it every
  # assembly starts simultaneously, and since most of them boot a Testcontainers PostgreSQL that is
  # a dozen-plus concurrent databases on a normal dev machine.
  #
  # That switch is only honoured while `dotnet test` stays on the MTP driver, and several
  # ordinary-looking arguments knock it off onto the MSBuild driver, where the switch is not
  # recognised and the run dies with "MSBUILD : error MSB1001: Unknown switch":
  #   * it must come BEFORE --solution (argument order is significant);
  #   * an implicit restore appends -restore  -> build/restore are split out below;
  #   * -v and --filter do the same (--filter becomes the MSBuild property VSTestTestCaseFilter).
  # MSBUILDDISABLENODEREUSE=1 additionally stops a warm MSBuild worker node from swallowing the
  # switch, which otherwise happens whenever a build ran earlier in the same session.
  #
  # Restoring only when there is no package cache keeps NuGet off the critical path — a restore can
  # stall behind a proxy. If a new package version lands in src/Directory.Packages.props the build
  # fails with a NuGet "run a restore" error; run ./build-sh/do-build.sh.
  if [[ ! -d "$ROOT/src/.packages" ]]; then
    nu_restore
  fi

  (
    cd "$SRC_DIR" || exit 1
    dotnet build "$SLN_FILE" -c "$CONFIG" --no-restore -v "$MSBUILD_VERBOSITY" || exit 1
    MSBUILDDISABLENODEREUSE=1 dotnet test \
      --max-parallel-test-modules "$MAX_PARALLEL_TEST_MODULES" \
      --solution "$SLN_FILE" \
      -c "$CONFIG" \
      --no-build --no-restore
  ) || { _assert_exec; return; }
  _assert_exec
}

test_ci() {
  _step "Running tests (skipping tests requiring local infrastructure)"
  # No --max-parallel-test-modules here: --filter is translated into an MSBuild VSTestTestCaseFilter
  # property, which puts `dotnet test` back on the MSBuild driver where the MTP module-parallelism
  # switch is not recognised (MSB1001). CI runners are expected to have enough resources; the
  # per-assembly concurrency is capped by src/Tests/xunit.runner.json anyway.
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
