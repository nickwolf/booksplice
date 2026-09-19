[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$MediaToolDirectory,

    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\release')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$mediaManifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools\ffmpeg\manifest.json') -Raw | ConvertFrom-Json
$releaseRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "BookSplice-$Version-win-x64"
$stagingPath = Join-Path $releaseRoot ".$packageName.staging"
$archivePath = Join-Path $releaseRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"
$resolvedTools = [IO.Path]::GetFullPath($MediaToolDirectory)
$expectedLicenseSha256 = [string]$mediaManifest.licenseSha256

foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
    $toolPath = Join-Path $resolvedTools $tool
    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) { throw "Media tool directory is missing '$tool'." }
    $hashProperty = if ($tool -eq 'ffmpeg.exe') { 'ffmpegSha256' } else { 'ffprobeSha256' }
    if (-not ($mediaManifest.PSObject.Properties.Name -contains $hashProperty)) { throw "Media tool manifest is missing '$hashProperty'." }
    $expectedToolHash = [string]$mediaManifest.$hashProperty
    $actualToolHash = (Get-FileHash -LiteralPath $toolPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actualToolHash, $expectedToolHash, [StringComparison]::Ordinal)) {
        throw "$tool checksum mismatch. Expected '$expectedToolHash', received '$actualToolHash'."
    }
}
$mediaLicensePath = Join-Path $resolvedTools 'FFmpeg-LICENSE.txt'
if (-not (Test-Path -LiteralPath $mediaLicensePath -PathType Leaf)) { throw "Media tool directory is missing 'FFmpeg-LICENSE.txt'." }
$actualLicenseSha256 = (Get-FileHash -LiteralPath $mediaLicensePath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [string]::Equals($actualLicenseSha256, $expectedLicenseSha256, [StringComparison]::Ordinal)) {
    throw "Media tool license checksum mismatch. Expected '$expectedLicenseSha256', received '$actualLicenseSha256'."
}

& (Join-Path $PSScriptRoot 'Get-FFmpegLicenseInventory.ps1') -MediaToolDirectory $resolvedTools -Check
if ($LASTEXITCODE -ne 0) { throw "FFmpeg component inventory check failed with exit code $LASTEXITCODE." }

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$releasePrefix = $releaseRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedStaging = [IO.Path]::GetFullPath($stagingPath)
if (-not $resolvedStaging.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release staging path escaped the release output directory.'
}

if (Test-Path -LiteralPath $resolvedStaging) {
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}
foreach ($oldFile in @($archivePath, $checksumPath)) {
    if (Test-Path -LiteralPath $oldFile) { Remove-Item -LiteralPath $oldFile -Force }
}
New-Item -ItemType Directory -Path $resolvedStaging | Out-Null

$commonPublish = @(
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $resolvedStaging,
    "/p:Version=$Version",
    '/p:PublishSingleFile=true',
    '/p:IncludeNativeLibrariesForSelfExtract=true',
    '/p:DebugType=None',
    '/p:DebugSymbols=false',
    '/p:ContinuousIntegrationBuild=true'
)

& dotnet publish (Join-Path $repositoryRoot 'src\BookSplice.Cli\BookSplice.Cli.csproj') @commonPublish
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE." }
& dotnet publish (Join-Path $repositoryRoot 'src\BookSplice.Gui\BookSplice.Gui.csproj') @commonPublish
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed with exit code $LASTEXITCODE." }

Get-ChildItem -LiteralPath $resolvedStaging -Filter '*.pdb' -File | Remove-Item -Force
$toolOutput = New-Item -ItemType Directory -Path (Join-Path $resolvedStaging 'tools\ffmpeg') -Force
Copy-Item -LiteralPath (Join-Path $resolvedTools 'ffmpeg.exe') -Destination $toolOutput.FullName
Copy-Item -LiteralPath (Join-Path $resolvedTools 'ffprobe.exe') -Destination $toolOutput.FullName
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $resolvedStaging
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $resolvedStaging
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'licenses') -Destination $resolvedStaging -Recurse
Copy-Item -LiteralPath $mediaLicensePath -Destination (Join-Path $resolvedStaging 'licenses\FFmpeg-LICENSE.txt') -Force
$releaseReadme = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs\README-RELEASE.md') -Raw).Replace('{{VERSION}}', $Version)
[IO.File]::WriteAllText((Join-Path $resolvedStaging 'README.md'), $releaseReadme, [Text.UTF8Encoding]::new($false))

$manifest = @"
BookSplice $Version
Runtime: Windows x64, self-contained .NET 10
Media tools: BtbN FFmpeg-Builds $($mediaManifest.release), $($mediaManifest.asset), LGPL
Media license SHA-256: $expectedLicenseSha256
"@
[IO.File]::WriteAllText((Join-Path $resolvedStaging 'VERSION.txt'), $manifest.Replace("`n", "`r`n"), [Text.UTF8Encoding]::new($false))

Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
$fixedTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
try {
    $files = @(Get-ChildItem -LiteralPath $resolvedStaging -File -Recurse | Sort-Object FullName)
    foreach ($file in $files) {
        $relativePath = [IO.Path]::GetRelativePath($resolvedStaging, $file.FullName).Replace('\', '/')
        $entry = $archive.CreateEntry($relativePath, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $fixedTimestamp
        $input = $file.OpenRead()
        $output = $entry.Open()
        try { $input.CopyTo($output) }
        finally { $output.Dispose(); $input.Dispose() }
    }
}
finally { $archive.Dispose() }

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksumPath, "$hash  $([IO.Path]::GetFileName($archivePath))`r`n", [Text.UTF8Encoding]::new($false))
Remove-Item -LiteralPath $resolvedStaging -Recurse -Force

[pscustomobject]@{
    ArchivePath = $archivePath
    ChecksumPath = $checksumPath
    Sha256 = $hash
}
