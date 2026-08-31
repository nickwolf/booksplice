param([string]$Output = "artifacts/fixtures", [string]$Tools = "artifacts/tools/ffmpeg")
$repo = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $repo "tools/AudiobookConverter.FixtureGenerator") -- (Join-Path $repo $Output) (Join-Path $repo $Tools)
