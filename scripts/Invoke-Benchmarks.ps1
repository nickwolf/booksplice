param([string]$Corpus = "artifacts/fixtures/corpus.json", [string]$Output = "artifacts/benchmarks/smoke.csv", [string]$Tools = "artifacts/tools/ffmpeg")
$repo = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $repo "tools/AudiobookConverter.Benchmarks") -- --corpus (Join-Path $repo $Corpus) --output (Join-Path $repo $Output) --tools (Join-Path $repo $Tools)
