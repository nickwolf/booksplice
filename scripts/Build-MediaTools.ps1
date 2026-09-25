[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DestinationDirectory,
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\tools\ffmpeg\manifest.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Media tool build destination already exists; use a new directory.' }
New-Item -ItemType Directory -Path $destination | Out-Null

foreach ($source in @($manifest.sourceArchives)) {
    $path = Join-Path $destination ([string]$source.fileName)
    Invoke-WebRequest -Uri ([string]$source.url) -OutFile $path
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne [string]$source.sha256) { throw "Source archive checksum mismatch: $($source.fileName)" }
}

$dockerfile = Join-Path $PSScriptRoot '..\tools\ffmpeg\Dockerfile'
$recipe = Join-Path $PSScriptRoot '..\tools\ffmpeg\build-minimal.sh'
Copy-Item -LiteralPath $recipe -Destination (Join-Path $destination 'build-minimal.sh')
$builderTag = 'booksplice-ffmpeg-builder:20260921'
& docker build --platform linux/amd64 -t $builderTag -f $dockerfile (Split-Path $dockerfile -Parent)
if ($LASTEXITCODE -ne 0) { throw "Media tool builder failed with exit code $LASTEXITCODE." }
& docker run --rm --platform linux/amd64 --memory=4g --cpus=2 -v "${destination}:/work" $builderTag sh /work/build-minimal.sh
if ($LASTEXITCODE -ne 0) { throw "Media tool build failed with exit code $LASTEXITCODE." }

$output = Join-Path $destination 'output'
foreach ($item in @(
    @{ Name = 'ffmpeg.exe'; Hash = [string]$manifest.ffmpegSha256 },
    @{ Name = 'ffprobe.exe'; Hash = [string]$manifest.ffprobeSha256 },
    @{ Name = 'FFmpeg-LICENSE.txt'; Hash = [string]$manifest.licenseSha256 },
    @{ Name = 'zlib-LICENSE.txt'; Hash = [string]$manifest.zlibLicenseSha256 }
)) {
    $path = Join-Path $output $item.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Media tool output is missing '$($item.Name)'." }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $item.Hash) { throw "Media tool output checksum mismatch: $($item.Name)" }
}
[pscustomobject]@{ MediaToolDirectory = $output; BuilderTag = $builderTag }