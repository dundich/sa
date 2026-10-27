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

# Encrypt, decrypt and check Sa.Configuration secrets files
./do-secrets.sh encrypt secrets.txt --client lsr --env dev -o secrets_enc.lsr.dev.txt
./do-secrets.sh decrypt secrets_enc.lsr.dev.txt
./do-secrets.sh check

# Container images the integration suites need (PostgreSQL, SeaweedFS, Ryuk)
./do-docker-images.sh status
./do-docker-images.sh pull
./do-docker-images.sh save ./docker-images     # then copy the folder to the offline machine
./do-docker-images.sh load ./docker-images

# Route Docker Hub around a VPN tunnel (needs root; only those hosts are affected)
sudo ./do-docker-bypass.sh status
sudo ./do-docker-bypass.sh on
sudo ./do-docker-bypass.sh off
```

## Test container images

Every integration test class starts its own container, but the image is pulled **once** and then
reused from the local daemon — after that the suites need no registry at all. So a machine without
registry access (or behind a tunnel slow enough to make pulls impractical) can still run them.

- `./do-docker-images.sh pull` — pull the three images into the local daemon.
- `./do-docker-images.sh save` / `load` — offline transfer: save on a connected machine, copy the
  `*.tar` files, load on the isolated one.
- The image list lives in `tasks.sh` (`DOCKER_TEST_IMAGES`) and mirrors the fixture defaults in
  `src/Tests/Fixtures/Sa.Data.PostgreSql.Fixture/PostgreSqlFixtureSettings.cs` and
  `src/Tests/Fixtures/Sa.Data.S3.Fixture/S3FixtureSettings.cs`. Update both when bumping a tag.

### S3 server image

The S3 suites run against **SeaweedFS** (`chrislusf/seaweedfs`), not MinIO. `minio/minio` no longer
answers anonymous pulls on Docker Hub (`denied: requested access to the resource is denied`), and
`quay.io/minio/minio` — MinIO's own registry — rejects anonymous pulls too.

The fixture starts `mini`, which runs master, volume, filer and the S3 gateway in one process, and
relies on three of its properties:

- the S3 gateway listens on **8333** (`mini` default; not MinIO's 9000);
- credentials come from `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` — with no config file present
  the gateway turns them into an admin identity, so requests must be signed;
- `-s3.autoCreateBucket=false`, otherwise writing to a missing bucket silently creates it and
  `ThrowIfBucketNotExists` stops throwing.

Both S3 suites pass on it: `Sa.Data.S3Tests` (78) and `Sa.HybridFileStorage.S3Tests` (48).

### When Docker Hub is unreachable or slow

If a VPN tunnel owns the default route, `docker pull` goes through the tunnel as well. That can turn
a 150 MB image into an hours-long download while the physical link is fast.

`./do-docker-bypass.sh on` adds host routes for the registry, its auth endpoint and the blob CDN
into the tunnel's policy-routing table, pointing at the physical gateway. Only those hosts are
affected; everything else keeps using the tunnel. `off` removes the routes.

- Route by IP, so run `on`, then `pull`, then `off` in one go — CDN addresses rotate between
  lookups.
- If `status` still shows `tun0` after `on`, the tunnel has a kill switch in nftables and routing
  alone is not enough; pausing the VPN for the duration of the pull is then the reliable option.

## Secrets tooling

`do-secrets.sh` wraps `tools/secrets.cs` — a .NET 10 file-based app that references
`src/Sa.Configuration/Sa.Configuration.csproj`, so it always uses the in-repo format.

- The key is read from `SA_SECRETS_DECRYPTION_KEY` (or `SA_SECRETS_DECRYPTION_KEY_<CLIENT>`),
  from `--key-file`, from `.env`, or typed interactively. `--key` is rejected on purpose:
  a command-line key leaks through process listings and shell history.
- `check` infers the client/environment from each file header, verifies decryption and exits
  non-zero on the first failure — suitable for CI.
- `encrypt` takes a plaintext `key=value` file and writes `ENC1:` values; comments and blank
  lines are preserved. Use it to re-encrypt after editing the plaintext source.

## Notes

- All scripts use `set -euo pipefail` for strict error handling.
- `dotnet test` runs from the `src/` directory (required by .NET 10 MTP runner).
- Projects to package are defined in `tasks.sh` — same list as the PowerShell version.
- The `local` NuGet source must be configured separately (`dotnet nuget add source ...`).

## Test resource limits

`./do-test.sh` deliberately throttles the run, because every integration test class boots its own
Testcontainers PostgreSQL and the default is to start all of them at once:

- `SA_TEST_PARALLELISM` — how many test assemblies may run at once (default `2`).
  `SA_TEST_PARALLELISM=6 ./do-test.sh` on a beefier machine.
- Per-assembly concurrency is capped separately by `src/Tests/xunit.runner.json`
  (`maxParallelThreads: 2`), which limits how many test classes run at once inside one assembly.

`test_run()` also builds with `--no-restore` and runs the tests with `--no-build --no-restore`,
restoring only when `src/.packages` is missing. That keeps NuGet off the critical path — a restore
can stall behind a proxy. If a new package version lands in `src/Directory.Packages.props` the build
fails with a NuGet "run a restore" error; run `./do-build.sh` once.

Both switches are needed for `--max-parallel-test-modules` to be honoured at all: any implicit
restore, `-v` or `--filter` silently drops `dotnet test` back onto the MSBuild driver, and a warm
MSBuild worker node (hence `MSBUILDDISABLENODEREUSE=1`) does the same. The symptom is always
`MSBUILD : error MSB1001: Unknown switch`. See the "Test load is capped on purpose" section in
`AGENTS.md`.
