[CmdletBinding()]
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\tools\ffmpeg\manifest.json'),
    [string]$MediaToolDirectory,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\licenses\FFmpeg-components.md'),
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($MediaToolDirectory)) { $MediaToolDirectory = $env:BOOKSPLICE_FFMPEG_DIR }
if ([string]::IsNullOrWhiteSpace($MediaToolDirectory)) { throw 'MediaToolDirectory is required.' }
$ffmpeg = Join-Path ([IO.Path]::GetFullPath($MediaToolDirectory)) 'ffmpeg.exe'
if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) { throw 'Pinned ffmpeg.exe was not found.' }
$actual = (Get-FileHash -LiteralPath $ffmpeg -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -cne [string]$manifest.ffmpegSha256) { throw 'Pinned ffmpeg.exe checksum drift detected.' }
$output = & $ffmpeg -hide_banner -buildconf 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Could not read FFmpeg build configuration.' }
$flags = @($output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_.StartsWith('--') })
if ($flags.Count -eq 0) { throw 'FFmpeg build configuration is empty.' }
$configuration = ($flags -join "`n") + "`n"
$configurationHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($configuration))).ToLowerInvariant()
if ($configurationHash -cne [string]$manifest.configurationSha256) { throw 'FFmpeg build configuration drift detected.' }
foreach ($flag in @('--enable-zlib', '--disable-autodetect', '--disable-everything', '--disable-gpl', '--disable-nonfree')) {
    if ($flags -cnotcontains $flag) { throw "FFmpeg build configuration is missing '$flag'." }
}
if (@($flags | Where-Object { $_ -match '^--enable-(?:gpl|nonfree|lib[^=]*)' }).Count -ne 0) {
    throw 'FFmpeg build enables an unreviewed external library or license switch.'
}
$version = (& $ffmpeg -version 2>&1 | Select-Object -First 1).ToString().Trim()
if (-not $version.StartsWith([string]$manifest.expectedVersionPrefix, [StringComparison]::Ordinal)) { throw 'FFmpeg version drift detected.' }
$ffmpegSource = @($manifest.sourceArchives | Where-Object fileName -like 'ffmpeg-*')[0]
$zlibSource = @($manifest.sourceArchives | Where-Object fileName -like 'zlib-*')[0]
$lines = @(
    '# Pinned FFmpeg component inventory',
    '',
    'This inventory describes the source-built tools in the Windows release package. The exact executable checksum and configure flags are checked by `scripts/Get-FFmpegLicenseInventory.ps1`.',
    '',
    "- FFmpeg version: $version",
    "- ffmpeg.exe SHA-256: $($manifest.ffmpegSha256)",
    "- ffprobe.exe SHA-256: $($manifest.ffprobeSha256)",
    "- Configure SHA-256: $configurationHash",
    "- FFmpeg source archive SHA-256: $($ffmpegSource.sha256)",
    "- zlib source archive SHA-256: $($zlibSource.sha256)",
    '',
    '## External components',
    '',
    '| Component | Version | Terms | Source |',
    '| --- | --- | --- | --- |',
    '| FFmpeg | 946fcce07b6dcd0331c8cc609192aeff5e1924f8 | LGPL 2.1 or later with component-specific exceptions in the upstream source | [FFmpeg source](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b6dcd0331c8cc609192aeff5e1924f8) |',
    '| zlib | 1.3.2 | zlib License | [zlib source](https://github.com/madler/zlib/releases/tag/v1.3.2) |',
    '',
    'The build disables automatic dependency detection, GPL and nonfree components. zlib is the only enabled external library. The package includes the corresponding source archives and build recipe. Codec patent questions are separate from the copyright terms.',
    '',
    '## Exact configure flags',
    '',
    '```text'
) + $flags + @('```', '')
$content = ($lines -join "`n") + "`n"
$path = [IO.Path]::GetFullPath($OutputPath)
if ($Check) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'FFmpeg component inventory is missing.' }
    if ([IO.File]::ReadAllText($path) -cne $content) { throw 'FFmpeg component inventory is stale.' }
    Write-Output 'FFmpeg component inventory is current.'
}
else {
    [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
    Write-Output 'Wrote FFmpeg component inventory.'
}