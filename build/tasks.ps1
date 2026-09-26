
$root = [System.IO.Path]::GetFullPath("$PSScriptRoot\..")

$sln_file = "$root\src\Sa.slnx"
$src_dir = "$root\src"
$sln_platform = "Any CPU"
$config = "Release"
$dist_folder = "$root\dist"
$msbuild_verbosity = "n"

# How many test assemblies `dotnet test` may run at the same time. Most test assemblies here start
# Testcontainers (PostgreSQL/Minio), so the default "all of them" means dozens of live database
# containers and the machine runs out of memory. Override per machine: $env:SA_TEST_PARALLELISM=6
$max_parallel_test_modules = if ($env:SA_TEST_PARALLELISM) { $env:SA_TEST_PARALLELISM } else { 2 }

$projects = @(
	"Sa.Utils.WorkQueue",

	"Sa.Media",
	"Sa.Media.FFmpeg",

	"Sa.Data.PostgreSql",
	"Sa.Data.S3",

	"Sa.Configuration",
  "Sa.Configuration.PostgreSql",

	"Sa.Schedule",

	"Sa.Partitional.PostgreSql",

	"Sa.Outbox",
	"Sa.Outbox.PostgreSql",

	"Sa.HybridFileStorage",
	"Sa.HybridFileStorage.FileSystem",
	"Sa.HybridFileStorage.Postgres",
	"Sa.HybridFileStorage.S3"
)

# msbuild.exe https://msdn.microsoft.com/pl-pl/library/ms164311(v=vs.80).aspx

function _AssertExec() {
	if ($LastExitCode -ne 0) { exit 1 }
}

function _Step($msg) {
	Write-Host ""
	Write-Host "===== $msg =====" -ForegroundColor Green
}

function NuRestore() {
	_Step "Restore NuGet packages"
	& dotnet restore $sln_file /p:Platform=$sln_platform /p:Configuration=$config --verbosity n
	_AssertExec
}

function _MsBuild($target) {
	_Step "$target solution"
	& dotnet build $sln_file -c $config -v $msbuild_verbosity
	_AssertExec
}

function Clean() {

	_Step "Clean folder $dist_folder"
	# Ensure dist folder exists
	New-Item -ErrorAction Ignore -ItemType directory -Path $dist_folder
	Remove-Item $dist_folder\* -recurse

	_MsBuild "Clean"
}

function Build() {
	Clean
	NuRestore
	_MsBuild "Build"
}

function Test() {
	_Step "Running tests"
	# `dotnet test` picks the MTP runner from src/global.json, which is only
	# discovered from src/ (or below). Run it with cwd=src or it fails with
	# "VSTest target is no longer supported on .NET 10 SDK".
	#
	# --max-parallel-test-modules caps how many test assemblies run at once. Without it every
	# assembly starts at the same time, and since most of them boot a Testcontainers PostgreSQL
	# that is a dozen concurrent databases on a 16 GB machine.
	#
	# That switch is only honoured while `dotnet test` stays on the MTP driver. Build and restore
	# are therefore split out explicitly and the test run is fully offline:
	#  * the switch must precede --solution, otherwise the SDK forwards it to MSBuild;
	#  * an implicit restore (or -v, or --filter) likewise drops back to the MSBuild driver.
	# In all those cases the run dies with "MSBUILD : error MSB1001: Unknown switch".
	# MSBUILDDISABLENODEREUSE additionally stops a warm MSBuild worker node from swallowing the
	# switch, which otherwise happens whenever a build ran earlier in the same session.
	# Side benefit: NuGet stays off the critical path, because a restore can stall behind a proxy.
	#
	# Restore only when there is no package cache yet, so a warm checkout never touches the network.
	# If a new package version shows up in Directory.Packages.props the build below fails with a
	# NuGet "run a restore" error — that is the signal to run .\build\do_build.ps1.
	if (-not (Test-Path "$root\src\.packages")) {
		NuRestore
	}

	Push-Location $src_dir
	& dotnet build $sln_file -c $config --no-restore -v $msbuild_verbosity
	_AssertExec

	$env:MSBUILDDISABLENODEREUSE = "1"
	& dotnet test --max-parallel-test-modules $max_parallel_test_modules --solution $sln_file -c $config --no-build --no-restore
	_AssertExec
	Pop-Location
}

function TestCi() {
	_Step "Running tests (skipping tests requiring local infrastructure)"
	# No --max-parallel-test-modules here: --filter is translated into an MSBuild
	# VSTestTestCaseFilter property, which puts `dotnet test` back on the MSBuild driver where the
	# MTP module-parallelism switch is not recognised (MSB1001). CI runners are expected to have
	# enough resources; per-assembly concurrency is capped by src/Tests/xunit.runner.json anyway.
	Push-Location $src_dir
	& dotnet test --solution $sln_file --filter "Category!=Local"
	_AssertExec
	Pop-Location
}

function NuPack() {
	foreach ($project in $projects) {
		_Step "Package project $project"
		& dotnet pack "$root\src\$project\$project.csproj" --output $dist_folder --configuration $config -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg
		_AssertExec
	}
}

function NuPush($nuget_source) {
	# find both *.nupkg and *.snupkg files
	foreach ($package in Get-ChildItem $dist_folder -filter "*.nupkg" -name) {
		_Step "Push $package to $nuget_source"
		& dotnet nuget push "$dist_folder\$package" --source $nuget_source
		_AssertExec
	}
}

function NuPushEx($nuget_key) {
	$nuget_source = "https://api.nuget.org/v3/index.json"
	# find both *.nupkg and *.snupkg files
	foreach ($package in Get-ChildItem $dist_folder -filter "*.nupkg" -name) {
		_Step "dotnet nuget push $dist_folder\$package -k $nuget_key -s $nuget_source"
		& dotnet nuget push "$dist_folder\$package" -k $nuget_key -s "$nuget_source"
		_AssertExec
	}
}

function Package() {
	Build
	NuPack
}
