param([string]$Output = "artifacts/fixtures", [string]$Tools = "artifacts/tools/ffmpeg", [switch]$Long)
$repo = Split-Path -Parent $PSScriptRoot
$toolPath = Join-Path $repo $Tools
if (-not (Test-Path (Join-Path $toolPath "ffmpeg.exe"))) { $toolPath = (Get-ChildItem -Path $toolPath -Directory | Where-Object { Test-Path (Join-Path $_.FullName "ffmpeg.exe") } | Select-Object -First 1 -ExpandProperty FullName) }
$fixtureArguments = @((Join-Path $repo $Output), $toolPath)
if ($Long) { $fixtureArguments += "--long" }
dotnet build (Join-Path $repo "tools/BookSplice.FixtureGenerator/BookSplice.FixtureGenerator.csproj") -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet exec (Join-Path $repo "tools/BookSplice.FixtureGenerator/bin/Release/net10.0/BookSplice.FixtureGenerator.dll") @fixtureArguments
exit $LASTEXITCODE
