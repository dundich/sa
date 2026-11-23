#!/usr/bin/env bash
#
# Release driver for the Sa package family.
#
# Encodes the flow used for 0.20.0/0.21.0 together with the traps it hit:
#
#   * PublicAPI.Unshipped.txt may carry *REMOVED*-prefixed entries (members that were shipped and
#     are gone now). Folding them must delete the matching line from Shipped and drop the entry —
#     copying them verbatim fails the build with RS0024 ("shipped API file can't have removed
#     members").
#   * An intentional 0.x break makes ApiCompat fail `dotnet pack` (CP0001/CP0002). That is a
#     decision, not a script problem: record it in CompatibilitySuppressions.xml (generated with
#     `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`) before packing.
#   * package() -> build() -> clean() wipes dist/ and runs `dotnet clean`, so release notes kept in
#     dist/ have to survive that step (prepare stashes and restores *.md).
#   * The baseline cannot be pinned before the new version is published and resolvable, and once it
#     is pinned the suppression file records nothing and turns into an "unnecessary suppressions"
#     error — the two changes belong in one commit, which is what `pin` does.
#
# Tag placement: this script tags the *release* commit (`commit --tag`). v0.20.0 and v0.21.0
# happen to point at the later baseline-pin commit; the pin changes no package content, so the
# release commit is the accurate target. `git tag -f` is there if the old shape is wanted.
#
# Usage:
#   ./do-release.sh status
#   ./do-release.sh api     [--dry-run]
#   ./do-release.sh prepare [--skip-tests] [--dry-run]
#   ./do-release.sh commit  [--tag] [--notes FILE] [--dry-run]
#   ./do-release.sh publish [--no-nuget] [--no-github] [--draft] [--push] [--notes FILE] [--dry-run]
#   ./do-release.sh pin     [--notes FILE] [--dry-run]
#
# Credentials are read from the environment or from .secrets/ (git-ignored) and never printed:
#   NUGET_API_KEY         or .secrets/nuget-key    — nuget.org push
#   GITHUB_TOKEN          or .secrets/github-token — GitHub release (needs python3)
#
# Release notes are prose, so they are not generated here: every command that needs them takes
# --notes FILE. The default is dist/release-<version>.md, which `prepare` preserves across clean.
#
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/tasks.sh"

VERSION=""
BASELINE=""
TAG=""
NOTES_FILE=""
DRY_RUN=0
OPT_SKIP_TESTS=0
OPT_TAG=0
OPT_NO_NUGET=0
OPT_NO_GITHUB=0
OPT_DRAFT=0
OPT_PUSH=0

# ── Helpers ──────────────────────────────────────────────────────

_usage() {
  sed -n '3,/^SCRIPT_DIR=/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//' -e '$d'
}

_fail() {
  echo "ERROR: $*" >&2
  exit 1
}

# Prints a mutating command instead of running it when --dry-run is set.
run() {
  if [[ $DRY_RUN -eq 1 ]]; then
    printf '  [dry-run] %s\n' "$*"
    return 0
  fi
  "$@"
}

_parse_flags() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --dry-run) DRY_RUN=1 ;;
      --skip-tests) OPT_SKIP_TESTS=1 ;;
      --tag) OPT_TAG=1 ;;
      --no-nuget) OPT_NO_NUGET=1 ;;
      --no-github) OPT_NO_GITHUB=1 ;;
      --draft) OPT_DRAFT=1 ;;
      --push) OPT_PUSH=1 ;;
      --notes)
        [[ $# -ge 2 ]] || _fail "--notes requires a path"
        NOTES_FILE="$2"
        shift ;;
      -h | --help) _usage; exit 0 ;;
      *) _fail "unknown argument: $1 (see --help)" ;;
    esac
    shift
  done
  NOTES_FILE="${NOTES_FILE:-$DIST_FOLDER/release-$VERSION.md}"
}

# ── Version bookkeeping ─────────────────────────────────────────

_props_value() {
  sed -n 's:.*<'"$1"'>\([^<]*\)</'"$1"'>.*:\1:p' "$ROOT/src/Libraries/Directory.Build.props" | head -1
}

_load_versions() {
  VERSION="$(_props_value Version)"
  BASELINE="$(_props_value PackageValidationBaselineVersion)"
  [[ -n "$VERSION" ]] || _fail "не нашёл <Version> в src/Libraries/Directory.Build.props"
  TAG="v$VERSION"
  NOTES_FILE="${NOTES_FILE:-$DIST_FOLDER/release-$VERSION.md}"
}

_dist_count() {
  [[ -d "$DIST_FOLDER" ]] || { echo 0; return; }
  find "$DIST_FOLDER" -maxdepth 1 -name "*.$VERSION.$1" | wc -l
}

_unshipped_entries() {
  local file="$1"
  [[ -f "$file" ]] || { echo 0; return; }
  grep -vc -x '#nullable enable' "$file" || true
}

_secret() {
  # $1 = env var name, $2 = file name under .secrets/
  local value="${!1:-}"
  if [[ -z "$value" && -s "$ROOT/.secrets/$2" ]]; then
    value="$(tr -d '[:space:]' <"$ROOT/.secrets/$2")"
  fi
  printf '%s' "$value"
}

_api_file() {
  # $1 = Unshipped|Shipped, $2 = project name
  local csproj
  csproj="$(find "$ROOT/src/Libraries" \( -name bin -o -name obj \) -prune -o -name "$2.csproj" -print | head -1)"
  [[ -n "$csproj" ]] || return 1
  echo "$(dirname "$csproj")/PublicAPI.$1.txt"
}

# ── Public API: Unshipped -> Shipped ────────────────────────────

fold_api() {
  _step "Fold PublicAPI.Unshipped.txt into Shipped ($VERSION)"
  local un sh rem add tmp_sh dup entry added_total=0 removed_total=0 count=0
  local -a missing=()

  while IFS= read -r -d '' un; do
    sh="${un%Unshipped.txt}Shipped.txt"
    [[ -f "$sh" ]] || _fail "нет $sh рядом с $un"
    rem="$(mktemp)"
    add="$(mktemp)"

    awk -v rem="$rem" -v add="$add" '
      $0 == "#nullable enable" { next }
      /^\*REMOVED\*/ { print substr($0, 10) > rem; next }
      { print > add }
    ' "$un"

    if [[ ! -s "$rem" && ! -s "$add" ]]; then
      rm -f "$rem" "$add"
      continue
    fi

    while IFS= read -r entry; do
      grep -qxF -- "$entry" "$sh" || missing+=("${sh#"$ROOT/"}: $entry")
    done <"$rem"

    tmp_sh="$(mktemp)"
    if [[ -s "$rem" ]]; then
      awk 'NR == FNR { skip[$0] = 1; next } !($0 in skip)' "$rem" "$sh" >"$tmp_sh"
    else
      cp "$sh" "$tmp_sh"
    fi
    [[ -s "$add" ]] && cat "$add" >>"$tmp_sh"

    dup="$(grep -v -x '#nullable enable' "$tmp_sh" | sort | uniq -d | head -3)"
    [[ -z "$dup" ]] || _fail "после свёртки в $sh появились дубликаты: $dup"

    local label="${sh#"$ROOT/src/Libraries/"}"
    label="${label%/PublicAPI.Shipped.txt}"
    if [[ $DRY_RUN -eq 1 ]]; then
      echo "  [dry-run] $label: +$(wc -l <"$add") -$(wc -l <"$rem")"
    else
      # cat, not mv: mktemp creates 0600 and the shipped lists are 0644.
      cat "$tmp_sh" >"$sh"
      printf '#nullable enable\n' >"$un"
      echo "  $label: +$(wc -l <"$add") -$(wc -l <"$rem")"
    fi
    added_total=$((added_total + $(wc -l <"$add")))
    removed_total=$((removed_total + $(wc -l <"$rem")))
    count=$((count + 1))
    rm -f "$rem" "$add" "$tmp_sh"
  done < <(find "$ROOT/src/Libraries" \( -name bin -o -name obj \) -prune -o -name PublicAPI.Unshipped.txt -print0)

  # Guarded: bash before 4.4 treats an empty "${array[@]}" as unbound under `set -u`.
  if [[ ${#missing[@]} -gt 0 ]]; then
    for entry in "${missing[@]}"; do
      echo "  ! *REMOVED* entry not found in Shipped ($entry) — nothing to delete" >&2
    done
  fi

  if [[ $count -eq 0 ]]; then
    echo "  already folded: every Unshipped list is empty"
  else
    echo "  total: +$added_total added, -$removed_total removed across $count package(s)"
  fi
}

# ── Release notes survive the clean ─────────────────────────────

_notes_backup=""

_stash_notes() {
  local -a files=()
  while IFS= read -r -d '' file; do files+=("$file"); done \
    < <(find "$DIST_FOLDER" -maxdepth 1 -name "*.md" -print0 2>/dev/null)
  [[ ${#files[@]} -gt 0 ]] || return 0
  _notes_backup="$(mktemp -d)"
  cp "${files[@]}" "$_notes_backup/"
  echo "  сохранил ${#files[@]} .md из dist/ на время clean"
}

_restore_notes() {
  [[ -n "$_notes_backup" ]] || return 0
  cp "$_notes_backup"/*.md "$DIST_FOLDER"/
  rm -rf "$_notes_backup"
  _notes_backup=""
}

# ── Commands ────────────────────────────────────────────────────

cmd_status() {
  _load_versions
  _step "Sa release status"
  printf '  version:   %s\n' "$VERSION"
  printf '  tag:       %s (%s)\n' "$TAG" \
    "$(git -C "$ROOT" rev-parse -q --verify "refs/tags/$TAG" >/dev/null && echo present || echo missing)"
  printf '  baseline:  %s%s\n' "$BASELINE" \
    "$([[ "$BASELINE" == "$VERSION" ]] && echo " (pinned to this release)")"
  printf '  branch:    %s\n' "$(git -C "$ROOT" rev-parse --abbrev-ref HEAD)"
  printf '  dist:      %s .nupkg + %s .snupkg at %s\n' \
    "$(_dist_count nupkg)" "$(_dist_count snupkg)" "$VERSION"

  local total=0 project file
  for project in "${PROJECTS[@]}"; do
    file="$(_api_file Unshipped "$project")" || continue
    total=$((total + $(_unshipped_entries "$file")))
  done
  printf '  unshipped: %s entries across %s packages\n' "$total" "${#PROJECTS[@]}"
  printf '  breaks recorded: %s CompatibilitySuppressions.xml in src/Libraries\n' \
    "$(find "$ROOT/src/Libraries" -name CompatibilitySuppressions.xml -not -path '*/obj/*' | wc -l)"
}

cmd_api() {
  _load_versions
  fold_api
  if [[ $DRY_RUN -eq 0 ]]; then
    echo ""
    echo "Next: review 'git diff --stat', then ./build-sh/do-release.sh prepare"
  fi
}

cmd_prepare() {
  _load_versions
  _step "Release $VERSION: prepare (API fold -> gate -> pack -> payload check)"

  fold_api
  [[ $DRY_RUN -eq 1 ]] && { echo ""; echo "  [dry-run] gate, pack and payload check skipped"; return; }

  _step "Gate: formatting"
  dotnet format "$SLN_FILE" --no-restore --verify-no-changes || _fail "dotnet format --verify-no-changes красный"

  if [[ $OPT_SKIP_TESTS -eq 1 ]]; then
    _step "Gate: tests SKIPPED (--skip-tests), building $CONFIG instead"
    dotnet build "$SLN_FILE" -c "$CONFIG" || _fail "build $CONFIG красный"
  else
    # test_run() builds the configuration it tests and caps module parallelism.
    test_run || _fail "тесты красные"
  fi

  _stash_notes
  # package() = build() (clean + restore + Release build) + nu_pack into dist/.
  package || _fail "pack красный"
  _restore_notes

  package_check || _fail "payload check красный"

  _step "Packaged $VERSION"
  local nupkg snupkg expected=${#PROJECTS[@]}
  nupkg="$(_dist_count nupkg)"
  snupkg="$(_dist_count snupkg)"
  printf '  %s .nupkg, %s .snupkg (expected %s each)\n' "$nupkg" "$snupkg" "$expected"
  [[ "$nupkg" -eq "$expected" && "$snupkg" -eq "$expected" ]] \
    || _fail "dist/ не содержит полного набора $VERSION — см. вывод pack выше"

  echo ""
  echo "Next: review the API diff and the packages, write the release notes, then"
  echo "      ./build-sh/do-release.sh commit --tag"
}

cmd_commit() {
  _load_versions
  _step "Release $VERSION: commit folded API"

  local changed
  changed="$(git -C "$ROOT" status --porcelain)"
  [[ -n "$changed" ]] || _fail "нечего коммитить: Unshipped уже свёрнут в Shipped"

  local unexpected
  unexpected="$(git -C "$ROOT" status --porcelain \
    | grep -vE ' src/Libraries/.*(PublicAPI\.(Shipped|Unshipped)\.txt|CompatibilitySuppressions\.xml)$' || true)"
  if [[ -n "$unexpected" ]]; then
    echo "$unexpected" >&2
    _fail "в рабочем дереве есть посторонние изменения — закоммитьте их отдельно"
  fi

  local added removed
  added="$(git -C "$ROOT" diff --numstat -- '*PublicAPI.Shipped.txt' | awk '{ a += $1 } END { print a + 0 }')"
  removed="$(git -C "$ROOT" diff --numstat -- '*PublicAPI.Shipped.txt' | awk '{ r += $2 } END { print r + 0 }')"

  local message
  message="$(printf 'build: release the package family as %s\n\nThe Unshipped API lists are folded into Shipped — %s members added, %s removed —\nso PublicAPI.Shipped.txt now describes the surface %s ships.\n\nVerified: full gate green (build, tests, dotnet format) and dotnet pack green\nfor all %s packages with package validation.\n' \
    "$VERSION" "$added" "$removed" "$VERSION" "${#PROJECTS[@]}")"

  run git -C "$ROOT" add -A src/Libraries
  run git -C "$ROOT" commit -m "$message"

  if [[ $OPT_TAG -eq 1 ]]; then
    [[ -s "$NOTES_FILE" ]] || _fail "для --tag нужны заметки: --notes FILE (по умолчанию $NOTES_FILE)"
    run git -C "$ROOT" tag -a "$TAG" -F "$NOTES_FILE"
    echo "  tag $TAG -> release commit"
  fi
  echo ""
  echo "Next: ./build-sh/do-release.sh publish --push"
}

cmd_publish() {
  _load_versions
  _step "Release $VERSION: publish"

  local expected=${#PROJECTS[@]} nupkg snupkg
  nupkg="$(_dist_count nupkg)"
  snupkg="$(_dist_count snupkg)"
  [[ "$nupkg" -eq "$expected" && "$snupkg" -eq "$expected" ]] \
    || _fail "dist/ не содержит полного набора $VERSION ($nupkg/$snupkg из $expected) — сначала prepare"
  git -C "$ROOT" rev-parse -q --verify "refs/tags/$TAG" >/dev/null \
    || _fail "локального тега $TAG нет — ./build-sh/do-release.sh commit --tag"

  if [[ $OPT_NO_NUGET -eq 0 ]]; then
    local key
    key="$(_secret NUGET_API_KEY nuget-key)"
    [[ -n "$key" ]] || _fail "нет ключа nuget.org: NUGET_API_KEY или .secrets/nuget-key"
    if [[ $DRY_RUN -eq 1 ]]; then
      echo "  [dry-run] push $nupkg .nupkg + $snupkg .snupkg to nuget.org (key set)"
    else
      nu_push "https://api.nuget.org/v3/index.json" "$key" || _fail "nuget.org push красный"
    fi
    _step "Push packages into the local feed (src/nupkgs)"
    nu_push "$ROOT/src/nupkgs" || _fail "локальный фид красный"
  fi

  if [[ $OPT_NO_GITHUB -eq 0 ]]; then
    [[ -s "$NOTES_FILE" ]] || _fail "для GitHub-релиза нужны заметки: --notes FILE (по умолчанию $NOTES_FILE)"
    local token draft="false"
    token="$(_secret GITHUB_TOKEN github-token)"
    [[ -n "$token" ]] || _fail "нет токена GitHub: GITHUB_TOKEN или .secrets/github-token"
    [[ $OPT_DRAFT -eq 1 ]] && draft="true"
    if [[ $DRY_RUN -eq 1 ]]; then
      echo "  [dry-run] create/update GitHub release $TAG (draft=$draft) from $NOTES_FILE"
    else
      python3 - "$TAG" "Sa $VERSION" "$NOTES_FILE" "$draft" "$token" <<'PY' || exit 1
import json, sys, urllib.request, urllib.error

tag, name, notes_path, draft, token = sys.argv[1:6]
headers = {
    "Authorization": f"Bearer {token}",
    "Accept": "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "Content-Type": "application/json",
}
base = "https://api.github.com/repos/dundich/sa/releases"

def call(url, data=None, method="GET"):
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)

payload = json.dumps({
    "tag_name": tag,
    "name": name,
    "body": open(notes_path, encoding="utf-8").read(),
    "draft": draft == "true",
}).encode()

try:
    existing = [r for r in call(f"{base}?per_page=100") if r.get("tag_name") == tag]
except urllib.error.HTTPError as e:
    print("status:", e.code, e.read().decode("utf-8", "replace")[:400])
    sys.exit(1)

if existing:
    release = call(f"{base}/{existing[0]['id']}", data=payload, method="PATCH")
    print("updated release", release.get("id"), "| draft:", release.get("draft"))
else:
    release = call(base, data=payload, method="POST")
    print("created release", release.get("id"), "| draft:", release.get("draft"))
print("url:", release.get("html_url"))
PY
    fi
  fi

  if [[ $OPT_PUSH -eq 1 ]]; then
    local branch
    branch="$(git -C "$ROOT" rev-parse --abbrev-ref HEAD)"
    run git -C "$ROOT" push origin "$branch" "refs/tags/$TAG"
  fi
  echo ""
  echo "Next: ./build-sh/do-release.sh pin"
}

cmd_pin() {
  _load_versions
  [[ "$BASELINE" != "$VERSION" ]] || _fail "baseline уже $VERSION — этот релиз закреплён"

  _step "Pin the package validation baseline to $VERSION"
  echo "  $BASELINE -> $VERSION (требуется: пакеты $VERSION опубликованы и резолвятся)"
  echo "  пакетов $VERSION в локальном фиде: $(find "$ROOT/src/nupkgs" -maxdepth 1 -name "*.$VERSION.nupkg" | wc -l)/${#PROJECTS[@]}"

  local -a suppressions=()
  while IFS= read -r -d '' file; do suppressions+=("$file"); done \
    < <(find "$ROOT/src/Libraries" -name CompatibilitySuppressions.xml -not -path '*/obj/*' -print0)
  if [[ ${#suppressions[@]} -gt 0 ]]; then
    echo "  drop ${#suppressions[@]} CompatibilitySuppressions.xml: against $VERSION they record nothing"
    for file in "${suppressions[@]}"; do run rm "$file"; done
  fi

  local props="$ROOT/src/Libraries/Directory.Build.props" tmp
  [[ "$(grep -c '<PackageValidationBaselineVersion>' "$props")" -eq 1 ]] \
    || _fail "ожидал ровно одно свойство PackageValidationBaselineVersion в $props"
  tmp="$(mktemp)"
  sed -e "s:<PackageValidationBaselineVersion>[^<]*</PackageValidationBaselineVersion>:<PackageValidationBaselineVersion>$VERSION</PackageValidationBaselineVersion>:" \
    -e "s:published release ([0-9][0-9.]*):published release ($VERSION):" \
    "$props" >"$tmp"
  cmp -s "$props" "$tmp" && { rm -f "$tmp"; _fail "baseline в props не изменился"; }
  run cp "$tmp" "$props"
  rm -f "$tmp"

  _step "Verify: pack all ${#PROJECTS[@]} packages against the $VERSION baseline"
  if [[ $DRY_RUN -eq 1 ]]; then
    echo "  [dry-run] nu_pack (validation against $VERSION)"
  else
    nu_pack || _fail "pack против $VERSION красный — baseline не резолвится или API разошёлся"
  fi

  local message
  message="$(printf 'build: pin the package validation baseline to %s\n\n%s is published, so the baseline moves to it. Any CompatibilitySuppressions.xml\nis dropped in the same commit: against %s the removals it recorded exist on\nneither side, and ApiCompat fails the pack on unnecessary suppressions.\n\nVerified: dotnet pack green for all %s packages against the %s baseline.\n' \
    "$VERSION" "$VERSION" "$VERSION" "${#PROJECTS[@]}" "$VERSION")"
  run git -C "$ROOT" add -A src/Libraries
  run git -C "$ROOT" commit -m "$message"
  echo ""
  echo "Next: git push \$(git rev-parse --abbrev-ref HEAD)   # the pin commit"
}

main() {
  local command="${1:-}"
  [[ $# -gt 0 ]] && shift
  case "$command" in
    status) _parse_flags "$@"; cmd_status ;;
    api) _parse_flags "$@"; cmd_api ;;
    prepare) _parse_flags "$@"; cmd_prepare ;;
    commit) _parse_flags "$@"; cmd_commit ;;
    publish) _parse_flags "$@"; cmd_publish ;;
    pin) _parse_flags "$@"; cmd_pin ;;
    "" | -h | --help) _usage ;;
    *) _fail "unknown command: $command (see --help)" ;;
  esac
}

main "$@"
