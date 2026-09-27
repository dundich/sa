#!/usr/bin/env bash
#
# Package payload verification for Sa.Media.FFmpeg.
#
# Builds Sa.Media.FFmpeg as a NuGet package, consumes it from a throwaway project and asserts that
# the bundled FFmpeg binaries actually landed in the consumer's output directory.
#
# This check exists because the "works out of the box, no system install" promise is enforced
# entirely by buildTransitive/Sa.Media.FFmpeg.targets, and that file can fail SILENTLY: a RID that
# does not resolve to a payload folder makes the extraction target skip itself, and the package
# then ships a library that cannot find its own FFmpeg. The most common instance of that is the
# distro-qualified RID reported by $(NETCoreSdkRuntimeIdentifier) on Linux/macOS
# (`ubuntu.24.04-x64`, `osx.14-arm64`) instead of a bare `linux-x64`.
#
# ProjectReference-based builds — the test suite, the console sample — never catch this, because they
# take the Sa.Media.FFmpeg.csproj code path rather than the packaged .targets.
#
# Sourced by tasks.sh; expects SCRIPT_DIR/ROOT/SRC_DIR/CONFIG/MSBUILD_VERBOSINESS/_step/_assert_exec.

package_check() {
  local work
  work="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$work'" RETURN

  local feed="$work/feed"
  mkdir -p "$feed"

  # Windows (Git Bash): bash записывает путь feed в nuget.config потребителя как текст, а .NET
  # читает этот файл как Windows-путь — POSIX-форма (/tmp/...) решалась бы в C:\tmp\... и ломала
  # restore. Командам bash путь остаётся POSIX; конвертируется только то, что уходит в файл.
  local is_windows=0
  case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) is_windows=1 ;; esac

  local feed_path="$feed"
  if [[ $is_windows -eq 1 ]]; then
    feed_path="$(cygpath -w "$feed")"
  fi

  _step "Packing Sa.Media.FFmpeg into a throwaway feed"
  dotnet pack "$SRC_DIR/Sa.Media.FFmpeg/Sa.Media.FFmpeg.csproj" \
    --output "$feed" \
    --configuration "$CONFIG" \
    -p:IncludeSymbols=false || { _assert_exec; return; }

  local version
  version="$(find "$feed" -maxdepth 1 -name 'Sa.Media.FFmpeg.*.nupkg' ! -name '*.symbols.nupkg' | head -1)"
  if [[ -z "$version" ]]; then
    echo "ERROR: no Sa.Media.FFmpeg nupkg was produced" >&2
    exit 1
  fi
  version="$(basename "$version")"
  version="${version#Sa.Media.FFmpeg.}"
  version="${version%.nupkg}"
  echo "Packed version: $version"

  # A stale extraction of the same id/version in the global cache would mask a broken package.
  local cache="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
  rm -rf "$cache/sa.media.ffmpeg/$version"

  _step "Creating a throwaway consumer"
  mkdir -p "$work/consumer"

  cat > "$work/consumer/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed_path" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

  cat > "$work/consumer/consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Sa.Media.FFmpeg" Version="$version" />
  </ItemGroup>
</Project>
EOF

  cat > "$work/consumer/Program.cs" <<'EOF'
using Sa.Media.FFmpeg;

// Must resolve the bundled binary (AppContext.BaseDirectory/sa/native), not a system FFmpeg,
// and must be able to actually run it.
Console.WriteLine(IFFMpegExecutor.Default.Executor.ExecutablePath);
Console.WriteLine((await IFFMpegExecutor.Default.GetVersion()).Split('\n')[0]);
EOF

  _step "Restoring and building the consumer"
  dotnet build "$work/consumer/consumer.csproj" -c Debug -v m || { _assert_exec; return; }

  _step "Asserting the bundled payload"
  local outdir="$work/consumer/bin/Debug/net10.0"

  for binary in ffmpeg ffprobe; do
    local name
    if [[ $is_windows -eq 1 ]]; then
      name="$binary.exe"
    else
      name="$binary"
    fi

    if [[ ! -f "$outdir/sa/native/$name" ]]; then
      echo "ERROR: $outdir/sa/native/$name is missing — the package did not unpack its payload" >&2
      exit 1
    fi
    if [[ $is_windows -eq 0 && ! -x "$outdir/sa/native/$name" ]]; then
      echo "ERROR: $outdir/sa/native/$name is not executable" >&2
      exit 1
    fi
  done

  _step "Running the consumer"
  local reported
  reported="$(dotnet run --project "$work/consumer/consumer.csproj" --no-build)" || { _assert_exec; return; }
  echo "$reported"

  # Разделитель пути в ExecutablePath зависит от ОС: '/' на Unix, '\' на Windows.
  if ! grep -qE 'sa[/\\]native' <<<"$reported"; then
    echo "ERROR: the consumer resolved a system FFmpeg instead of the bundled one:" >&2
    echo "$reported" >&2
    exit 1
  fi
  if ! grep -qi 'ffmpeg version' <<<"$reported"; then
    echo "ERROR: 'ffmpeg -version' produced no recognizable output" >&2
    exit 1
  fi

  _step "Package payload OK ($version)"
}
