[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "Media tool manifest is missing required property '$Name'."
    }

    return $property.Value
}

function Invoke-MediaTool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & $ExecutablePath @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Media tool '$ExecutablePath' failed with exit code $LASTEXITCODE while running '$($Arguments -join ' ')': $($output.Trim())"
    }

    return $output.Trim()
}

function Assert-Matches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Output,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$Capability
    )

    if ($Output -notmatch $Pattern) {
        throw "Pinned media tools do not report required capability '$Capability'."
    }
}

function Test-MediaToolDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DirectoryPath,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersionPrefix,

        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedExecutables
    )

    foreach ($executable in $ExpectedExecutables) {
        $executablePath = Join-Path $DirectoryPath $executable
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "Validated media tool directory is missing '$executable'."
        }
    }

    $ffmpegPath = Join-Path $DirectoryPath 'ffmpeg.exe'
    $ffprobePath = Join-Path $DirectoryPath 'ffprobe.exe'
    $ffmpegVersion = Invoke-MediaTool -ExecutablePath $ffmpegPath -Arguments @('-version')
    $ffprobeVersion = Invoke-MediaTool -ExecutablePath $ffprobePath -Arguments @('-version')
    $expectedFFprobePrefix = 'ffprobe' + $ExpectedVersionPrefix.Substring('ffmpeg'.Length)

    if (-not $ffmpegVersion.StartsWith($ExpectedVersionPrefix, [StringComparison]::Ordinal)) {
        throw "ffmpeg.exe version drift detected. Expected '$ExpectedVersionPrefix'."
    }

    if (-not $ffprobeVersion.StartsWith($expectedFFprobePrefix, [StringComparison]::Ordinal)) {
        throw "ffprobe.exe version drift detected. Expected '$expectedFFprobePrefix'."
    }

    $encoders = Invoke-MediaTool -ExecutablePath $ffmpegPath -Arguments @('-hide_banner', '-encoders')
    $muxers = Invoke-MediaTool -ExecutablePath $ffmpegPath -Arguments @('-hide_banner', '-muxers')
    $formats = Invoke-MediaTool -ExecutablePath $ffmpegPath -Arguments @('-hide_banner', '-formats')

    Assert-Matches -Output $encoders -Pattern '(?m)^\s*A\S*\s+aac\s' -Capability 'native AAC encoder'
    Assert-Matches -Output $muxers -Pattern '(?m)^\s*E\s+(ipod|mp4)\s' -Capability 'iPod or MP4 muxer'
    Assert-Matches -Output $formats -Pattern '(?m)^\s*D\S*\s+concat\s' -Capability 'concat demuxer'
    Assert-Matches -Output $formats -Pattern '(?m)^\s*D\S*\s+ffmetadata\s' -Capability 'ffmetadata demuxer'
    Assert-Matches -Output $formats -Pattern '(?m)^\s*D?E?\s+image2\s' -Capability 'image2 format'
    Assert-Matches -Output $muxers -Pattern '(?m)^\s*E\s+null\s' -Capability 'null muxer'

    return [pscustomobject]@{
        FFmpegPath = $ffmpegPath
        FFprobePath = $ffprobePath
        FFmpegVersion = ($ffmpegVersion -split "`r?`n")[0]
        FFprobeVersion = ($ffprobeVersion -split "`r?`n")[0]
    }
}

$resolvedManifestPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ManifestPath)
if (-not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf)) {
    throw "Media tool manifest was not found at '$ManifestPath'."
}

try {
    $manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
}
catch {
    throw "Media tool manifest '$ManifestPath' is not valid JSON: $($_.Exception.Message)"
}

$schemaVersion = Get-RequiredProperty -InputObject $manifest -Name 'schemaVersion'
$provider = [string](Get-RequiredProperty -InputObject $manifest -Name 'provider')
$release = [string](Get-RequiredProperty -InputObject $manifest -Name 'release')
$asset = [string](Get-RequiredProperty -InputObject $manifest -Name 'asset')
$variant = [string](Get-RequiredProperty -InputObject $manifest -Name 'variant')
$expectedVersionPrefix = [string](Get-RequiredProperty -InputObject $manifest -Name 'expectedVersionPrefix')
$expectedSha256 = [string](Get-RequiredProperty -InputObject $manifest -Name 'sha256')
$expectedExecutables = @($manifest.expectedExecutables | ForEach-Object { [string]$_ })

if ([int]$schemaVersion -ne 1) {
    throw "Media tool manifest schema version '$schemaVersion' is not supported."
}

if ($provider -cne 'BtbN/FFmpeg-Builds') {
    throw "Media tool provider '$provider' is not supported by this acquisition script."
}

if ($variant -cne 'win64-lgpl') {
    throw "Media tool variant '$variant' is not the required win64 LGPL build."
}

if ([IO.Path]::GetFileName($release) -cne $release -or $release -in @('.', '..')) {
    throw "Media tool release '$release' must be a single safe path segment."
}

if ([IO.Path]::GetFileName($asset) -cne $asset -or -not $asset.EndsWith('.zip', [StringComparison]::Ordinal)) {
    throw "Media tool asset '$asset' must be a single ZIP file name."
}

if ($expectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Media tool manifest SHA-256 must be a 64-character lowercase hexadecimal digest.'
}

if ($expectedExecutables.Count -ne 2 -or
    $expectedExecutables[0] -cne 'ffmpeg.exe' -or
    $expectedExecutables[1] -cne 'ffprobe.exe') {
    throw 'Media tool manifest must list ffmpeg.exe and ffprobe.exe as expected executables.'
}

$resolvedDestinationRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DestinationRoot)
$destinationPath = Join-Path $resolvedDestinationRoot $release

if (Test-Path -LiteralPath $destinationPath) {
    if (-not (Test-Path -LiteralPath $destinationPath -PathType Container)) {
        throw "Media tool destination '$destinationPath' exists but is not a directory."
    }

    $tools = Test-MediaToolDirectory `
        -DirectoryPath $destinationPath `
        -ExpectedVersionPrefix $expectedVersionPrefix `
        -ExpectedExecutables $expectedExecutables
    Write-Output "Pinned media tools are already present and valid at '$destinationPath'."
    Write-Output $tools
    return
}

$downloadDirectory = Join-Path ([IO.Path]::GetTempPath()) "audiobookconverter-ffmpeg-$([guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $downloadDirectory $asset
$extractPath = Join-Path $downloadDirectory 'extracted'
$stagingPath = Join-Path $resolvedDestinationRoot ".$release.$([guid]::NewGuid().ToString('N')).staging"
$assetUri = "https://github.com/$provider/releases/download/$release/$asset"
$destinationRootExisted = Test-Path -LiteralPath $resolvedDestinationRoot

if ($destinationRootExisted -and -not (Test-Path -LiteralPath $resolvedDestinationRoot -PathType Container)) {
    throw "Media tool destination root '$resolvedDestinationRoot' exists but is not a directory."
}

try {
    New-Item -ItemType Directory -Path $downloadDirectory | Out-Null
    New-Item -ItemType Directory -Path $extractPath | Out-Null

    Write-Output "Downloading pinned media tools from '$assetUri'."
    Invoke-WebRequest -Uri $assetUri -OutFile $archivePath

    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actualSha256, $expectedSha256, [StringComparison]::Ordinal)) {
        throw "Downloaded media tool archive failed SHA-256 verification. Expected '$expectedSha256', received '$actualSha256'."
    }

    Write-Output "Verified archive SHA-256 '$actualSha256'."
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath

    $ffmpegCandidates = @(Get-ChildItem -LiteralPath $extractPath -Filter 'ffmpeg.exe' -File -Recurse)
    if ($ffmpegCandidates.Count -ne 1) {
        throw "Expected exactly one ffmpeg.exe in the verified archive, found $($ffmpegCandidates.Count)."
    }

    $sourceDirectory = $ffmpegCandidates[0].Directory.FullName
    foreach ($executable in $expectedExecutables) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $executable) -PathType Leaf)) {
            throw "Verified archive does not place '$executable' beside ffmpeg.exe."
        }
    }

    New-Item -ItemType Directory -Path $resolvedDestinationRoot -Force | Out-Null
    Copy-Item -LiteralPath $sourceDirectory -Destination $stagingPath -Recurse
    $tools = Test-MediaToolDirectory `
        -DirectoryPath $stagingPath `
        -ExpectedVersionPrefix $expectedVersionPrefix `
        -ExpectedExecutables $expectedExecutables

    [IO.Directory]::Move($stagingPath, $destinationPath)
    $tools = [pscustomobject]@{
        FFmpegPath = Join-Path $destinationPath 'ffmpeg.exe'
        FFprobePath = Join-Path $destinationPath 'ffprobe.exe'
        FFmpegVersion = $tools.FFmpegVersion
        FFprobeVersion = $tools.FFprobeVersion
    }
    Write-Output "Installed verified media tools at '$destinationPath'."
    Write-Output $tools
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }

    if (Test-Path -LiteralPath $downloadDirectory) {
        Remove-Item -LiteralPath $downloadDirectory -Recurse -Force
    }

    if (-not $destinationRootExisted -and
        (Test-Path -LiteralPath $resolvedDestinationRoot -PathType Container) -and
        @(Get-ChildItem -LiteralPath $resolvedDestinationRoot -Force).Count -eq 0) {
        Remove-Item -LiteralPath $resolvedDestinationRoot -Force
    }
}
