param([string]$Corpus = "artifacts/fixtures/corpus.json", [string]$Output = "artifacts/benchmarks/smoke.csv", [string]$Tools = "artifacts/tools/ffmpeg")
$repo = Split-Path -Parent $PSScriptRoot
$toolPath = Join-Path $repo $Tools
if (-not (Test-Path (Join-Path $toolPath "ffmpeg.exe"))) { $toolPath = (Get-ChildItem -Path $toolPath -Directory | Where-Object { Test-Path (Join-Path $_.FullName "ffmpeg.exe") } | Select-Object -First 1 -ExpandProperty FullName) }
dotnet build (Join-Path $repo "tools/AudiobookConverter.Benchmarks/AudiobookConverter.Benchmarks.csproj") -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet exec (Join-Path $repo "tools/AudiobookConverter.Benchmarks/bin/Release/net10.0/AudiobookConverter.Benchmarks.dll") --corpus (Join-Path $repo $Corpus) --output (Join-Path $repo $Output) --tools $toolPath
exit $LASTEXITCODE
