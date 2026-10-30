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
# Testcontainers (PostgreSQL/SeaweedFS), so the default "all of them at once" means dozens of live
# database containers and the machine runs out of memory. Override per machine:
#   SA_TEST_PARALLELISM=6 ./build-sh/do-test.sh
MAX_PARALLEL_TEST_MODULES="${SA_TEST_PARALLELISM:-2}"

# List of projects to package (order mirrors tasks.ps1)
PROJECTS=(
  "Sa.Utils.WorkQueue"

  "Sa.Media"
  "Sa.Media.FFmpeg"

  "Sa.Data.TempFolder"
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

# Images the integration suites start (one container per test class, so they are pulled once and
# then reused from the local daemon — after a pull the suites need no registry at all).
#
# Kept in sync by hand with the fixture defaults:
#   src/Tests/Fixtures/Sa.Data.PostgreSql.Fixture/PostgreSqlFixtureSettings.cs  -> postgres
#   src/Tests/Fixtures/Sa.Data.S3.Fixture/S3FixtureSettings.cs                  -> seaweedfs
# The Ryuk image is pinned by the Testcontainers package itself (the version in
# src/Directory.Packages.props decides it); bump this line when that version changes.
DOCKER_TEST_IMAGES=(
  "postgres:15"
  "chrislusf/seaweedfs:4.50"
  "testcontainers/ryuk:0.14.0"
)

DOCKER_IMAGE_ARCHIVE_DIR="$ROOT/dist/docker-images"

# Docker Hub endpoints a pull talks to. The blobs themselves are served from a CDN that the
# registry redirects to, so the CDN hosts have to be routed as well.
DOCKER_REGISTRY_HOSTS=(
  "auth.docker.io"
  "registry-1.docker.io"
  "production.cloudflare.docker.com"
  "production.cloudfront.docker.com"
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
  # `dotnet build` alone does not clean: without this step stale bin/obj survive and an
  # incremental build can hide artifacts from a previous tree state.
  _step "Clean solution"
  dotnet clean "$SLN_FILE" -c "$CONFIG" -v "$MSBUILD_VERBOSITY" || { _assert_exec; return; }
  _assert_exec
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

# ── Testcontainers images ────────────────────────────────────────
#
# Every integration test class boots its own container, so the images below are pulled once and
# then reused from the local daemon. After that the suites need no registry at all — which matters
# on a machine where the registry is unreachable or (as behind a slow tunnel) effectively is.
#
# Usage: ./build-sh/do-docker-images.sh {status|pull|save|load}

_docker_image_archive() {
  # postgres:15 -> postgres_15.tar
  echo "$DOCKER_IMAGE_ARCHIVE_DIR/$(echo "$1" | tr '/:' '__').tar"
}

docker_images_status() {
  _step "Testcontainers images"
  local image
  for image in "${DOCKER_TEST_IMAGES[@]}"; do
    if docker image inspect "$image" >/dev/null 2>&1; then
      printf '  %-45s present\n' "$image"
    else
      printf '  %-45s MISSING\n' "$image"
    fi
  done
}

docker_images_pull() {
  _step "Pulling Testcontainers images"
  local image
  for image in "${DOCKER_TEST_IMAGES[@]}"; do
    _step "docker pull $image"
    docker pull "$image" || { _assert_exec; return; }
  done
  docker_images_status
}

docker_images_save() {
  local dir="${1:-$DOCKER_IMAGE_ARCHIVE_DIR}"
  _step "Saving Testcontainers images to $dir"
  mkdir -p "$dir"
  local image archive
  for image in "${DOCKER_TEST_IMAGES[@]}"; do
    archive="$(_docker_image_archive "$image")"
    # An image that was never pulled cannot be saved; fail loudly instead of writing an empty tar.
    docker image inspect "$image" >/dev/null 2>&1 || {
      echo "образ $image отсутствует локально — сначала ./build-sh/do-docker-images.sh pull" >&2
      return 1
    }
    _step "docker save $image"
    docker save -o "$archive" "$image" || { _assert_exec; return; }
    printf '  %s (%s)\n' "$archive" "$(du -h "$archive" | cut -f1)"
  done
}

docker_images_load() {
  local dir="${1:-$DOCKER_IMAGE_ARCHIVE_DIR}"
  _step "Loading Testcontainers images from $dir"
  [[ -d "$dir" ]] || { echo "нет каталога $dir" >&2; return 1; }
  local archive found=0
  for archive in "$dir"/*.tar; do
    [[ -e "$archive" ]] || continue
    found=1
    _step "docker load $archive"
    docker load -i "$archive" || { _assert_exec; return; }
  done
  [[ $found -eq 1 ]] || { echo "в $dir нет *.tar" >&2; return 1; }
  docker_images_status
}

# ── Docker Hub routing ───────────────────────────────────────────
#
# When a VPN/tunnel owns the default route, every pull is pushed through it — which on a throttled
# tunnel turns a 150 MB image into an hours-long download. These helpers add host routes for the
# registry and its CDN through the physical interface so pulls go direct, without touching the
# tunnel for any other traffic.
#
# Usage: sudo ./build-sh/do-docker-bypass.sh {status|on|off}

_docker_host_ips() {
  local host
  for host in "${DOCKER_REGISTRY_HOSTS[@]}"; do
    getent ahostsv4 "$host" 2>/dev/null | awk '{print $1}'
  done | sort -u
}

# Table id the tunnel keeps its default route in, as referenced by `ip rule`. A bypass route in
# `main` alone is never consulted: the policy rules look up this table first, and its default route
# wins before `main` is reached.
_vpn_route_table() {
  ip -4 rule show 2>/dev/null \
    | awk '{for (i = 1; i <= NF; i++) if ($i == "lookup") print $(i + 1)}' \
    | grep -E '^[0-9]+$' \
    | grep -vxE '(0|253|254|255)' \
    | sort -u | head -1
}

_phys_iface() {
  ip -4 route show default table main 2>/dev/null \
    | awk '{for (i = 1; i <= NF; i++) if ($i == "dev") print $(i + 1)}' | head -1
}

_phys_gateway() {
  ip -4 route show default table main 2>/dev/null \
    | awk '{for (i = 1; i <= NF; i++) if ($i == "via") print $(i + 1)}' | head -1
}

_phys_address() {
  ip -4 -o addr show dev "$1" scope global 2>/dev/null \
    | awk '{print $4}' | cut -d/ -f1 | head -1
}

# Prints "<table> <gateway> <device> <address>" — the route every bypass entry is built from.
_docker_bypass_plan() {
  local dev gw src table
  dev="$(_phys_iface)"
  gw="$(_phys_gateway)"
  table="$(_vpn_route_table)"
  if [[ -z "$dev" || -z "$gw" ]]; then
    echo "нет default-маршрута в таблице main — не от чего отталкиваться" >&2
    return 1
  fi
  src="$(_phys_address "$dev")"
  echo "${table:-main} $gw $dev $src"
}

docker_bypass_status() {
  _step "Маршруты до Docker Hub"
  local plan table gw dev src ip
  plan="$(_docker_bypass_plan)" || return 1
  read -r table gw dev src <<<"$plan"
  echo "физический канал: $dev ${src:-?} через $gw"
  echo "таблица туннеля:  ${table}"
  echo ""
  while read -r ip; do
    [[ -n "$ip" ]] || continue
    printf '  %-40s ' "$ip"
    # `ip route get` honours the policy rules, so this shows the path a pull would really take.
    ip -4 route get "$ip" 2>/dev/null | head -1
  done < <(_docker_host_ips)
}

docker_bypass_on() {
  _step "Направляю Docker Hub мимо туннеля"
  local plan table gw dev src ip t
  plan="$(_docker_bypass_plan)" || return 1
  read -r table gw dev src <<<"$plan"

  local tables=("main")
  [[ "$table" != "main" ]] && tables+=("$table")

  while read -r ip; do
    [[ -n "$ip" ]] || continue
    for t in "${tables[@]}"; do
      if [[ -n "$src" ]]; then
        sudo ip route replace "$ip/32" via "$gw" dev "$dev" src "$src" table "$t" >/dev/null
      else
        sudo ip route replace "$ip/32" via "$gw" dev "$dev" table "$t" >/dev/null
      fi
    done
    printf '  %-40s -> %s dev %s (таблицы: %s)\n' "$ip" "$gw" "$dev" "${tables[*]}"
  done < <(_docker_host_ips)

  echo ""
  docker_bypass_status
  echo ""
  echo "Проверка: путь должен идти через $dev, а не через tun0."
  echo "Если путь всё ещё через tun0 — у туннеля kill switch (nftables); тогда надёжнее"
  echo "поставить VPN на паузу на время pull."
}

docker_bypass_off() {
  _step "Убираю маршруты до Docker Hub"
  local plan table gw dev src ip t
  plan="$(_docker_bypass_plan)" || return 1
  read -r table gw dev src <<<"$plan"

  local tables=("main")
  [[ "$table" != "main" ]] && tables+=("$table")

  while read -r ip; do
    [[ -n "$ip" ]] || continue
    for t in "${tables[@]}"; do
      sudo ip route del "$ip/32" table "$t" >/dev/null 2>&1 || true
    done
    printf '  %-40s снят\n' "$ip"
  done < <(_docker_host_ips)

  echo ""
  docker_bypass_status
}

nu_pack() {
  local project csproj
  for project in "${PROJECTS[@]}"; do
    _step "Package project $project"
    # Locate the project by name anywhere under src/Libraries so a regrouping into domain
    # folders does not require touching this list's path logic.
    csproj="$(find "$ROOT/src/Libraries" \( -name bin -o -name obj \) -prune -o -name "$project.csproj" -print | head -1)"
    if [[ -z "$csproj" ]]; then
      echo "ERROR: не найден $project.csproj в $ROOT/src/Libraries" >&2
      return 1
    fi
    dotnet pack "$csproj" \
      --output "$DIST_FOLDER" \
      --configuration Release \
      -p:IncludeSymbols=true \
      -p:SymbolPackageFormat=snupkg || { _assert_exec; return; }
    _assert_exec
  done
}

nu_push() {
  local nuget_source="$1"
  local api_key="${2:-${NUGET_API_KEY:-}}"
  _step "Push packages to $nuget_source"

  # Find both *.nupkg and *.snupkg. An empty dist/ must fail loudly: the loop below would
  # otherwise do nothing and the script would report success without pushing anything.
  local packages=()
  while IFS= read -r -d '' package; do
    packages+=("$package")
  done < <(find "$DIST_FOLDER" -maxdepth 1 \( -name "*.nupkg" -o -name "*.snupkg" \) -print0 | sort -z)

  if [[ ${#packages[@]} -eq 0 ]]; then
    echo "ERROR: в $DIST_FOLDER нет пакетов — сначала ./build-sh/do-package.sh" >&2
    return 1
  fi

  local package
  for package in "${packages[@]}"; do
    _step "Push $(basename "$package") to $nuget_source"
    if [[ -n "$api_key" ]]; then
      dotnet nuget push "$package" --source "$nuget_source" --api-key "$api_key" --skip-duplicate \
        || { _assert_exec; return; }
    else
      dotnet nuget push "$package" --source "$nuget_source" --skip-duplicate \
        || { _assert_exec; return; }
    fi
    _assert_exec
  done
}

nu_push_ex() {
  local nuget_key="$1"
  local nuget_source="https://api.nuget.org/v3/index.json"
  _step "Push packages to $nuget_source (key=$nuget_key)"

  while IFS= read -r -d '' package; do
    _step "Push $(basename "$package") to $nuget_source"
    dotnet nuget push "$package" -k "$nuget_key" -s "$nuget_source" --skip-duplicate || { _assert_exec; return; }
    _assert_exec
  done < <(find "$DIST_FOLDER" -maxdepth 1 \( -name "*.nupkg" -o -name "*.snupkg" \) -print0 | sort -z)
}

package() {
  build
  nu_pack
}

# Sourced late: it defines package_check, which needs the variables above.
source "$SCRIPT_DIR/package-check.sh"
