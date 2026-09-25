[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseOwnedDirectory.ps1')

$resolvedArchive = [IO.Path]::GetFullPath($ArchivePath)
$resolvedChecksum = [IO.Path]::GetFullPath($ChecksumPath)
if (-not (Test-Path -LiteralPath $resolvedArchive -PathType Leaf)) { throw 'Release archive was not found.' }
if (-not (Test-Path -LiteralPath $resolvedChecksum -PathType Leaf)) { throw 'Release checksum was not found.' }

$expectedHash = ((Get-Content -LiteralPath $resolvedChecksum -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release checksum mismatch. Expected '$expectedHash', received '$actualHash'."
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName UIAutomationClient
$inspection = [IO.Compression.ZipFile]::OpenRead($resolvedArchive)
try {
    foreach ($entry in $inspection.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or $name -match '(^|/)\.\.(/|$)' -or $name.Contains(':')) {
            throw "Release archive contains an unsafe entry '$name'."
        }
    }
}
finally { $inspection.Dispose() }

$ownedTestRoot = New-ReleaseOwnedDirectory ([IO.Path]::GetTempPath()) "booksplice-release-$([guid]::NewGuid().ToString('N'))"
$testRoot = $ownedTestRoot.Path
try {
    Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $testRoot
    $required = @(
        'BookSplice.Gui.exe', 'booksplice.exe', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'VERSION.txt',
        'licenses\FFmpeg-LICENSE.txt', 'licenses\FFmpeg-components.md', 'licenses\LGPL-2.1.txt', 'licenses\zlib-LICENSE.txt',
        'tools\ffmpeg\ffmpeg.exe', 'tools\ffmpeg\ffprobe.exe', 'sources\manifest.json', 'sources\BUILDING.md', 'sources\Dockerfile', 'sources\build-minimal.sh', 'sources\Build-MediaTools.ps1', 'sources\ffmpeg-946fcce07b.tar.gz', 'sources\zlib-v1.3.2.tar.gz'
    )
    foreach ($relativePath in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $testRoot $relativePath) -PathType Leaf)) {
            throw "Release archive is missing '$relativePath'."
        }
    }
    $versionText = Get-Content -LiteralPath (Join-Path $testRoot 'VERSION.txt') -Raw
    $licenseMatch = [regex]::Match($versionText, '(?m)^Media license SHA-256: ([0-9a-f]{64})$')
    if (-not $licenseMatch.Success) { throw 'Release version manifest does not contain the media license SHA-256.' }
    $licenseHash = (Get-FileHash -LiteralPath (Join-Path $testRoot 'licenses\FFmpeg-LICENSE.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($licenseHash -ne $licenseMatch.Groups[1].Value) { throw 'Bundled FFmpeg license checksum mismatch.' }
    $sourceManifest = Get-Content -LiteralPath (Join-Path $testRoot 'sources\manifest.json') -Raw | ConvertFrom-Json
    foreach ($source in @($sourceManifest.sourceArchives)) {
        $sourcePath = Join-Path (Join-Path $testRoot 'sources') ([string]$source.fileName)
        if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$source.sha256) {
            throw "Bundled source archive checksum mismatch: $($source.fileName)"
        }
    }
    foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
        $expected = if ($tool -eq 'ffmpeg.exe') { [string]$sourceManifest.ffmpegSha256 } else { [string]$sourceManifest.ffprobeSha256 }
        if ((Get-FileHash -LiteralPath (Join-Path $testRoot "tools\ffmpeg\$tool") -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) {
            throw "Bundled media tool checksum mismatch: $tool"
        }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $testRoot 'licenses\zlib-LICENSE.txt') -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$sourceManifest.zlibLicenseSha256) {
        throw 'Bundled zlib license checksum mismatch.'
    }
    $releaseReadme = Get-Content -LiteralPath (Join-Path $testRoot 'README.md') -Raw
    if ($releaseReadme.Contains('{{VERSION}}')) { throw 'Release README contains an unresolved version placeholder.' }
    if (@(Get-ChildItem -LiteralPath $testRoot -Filter '*.pdb' -File -Recurse).Count -ne 0) {
        throw 'Release archive contains debug symbols.'
    }

    $cli = Join-Path $testRoot 'booksplice.exe'
    $ffmpeg = Join-Path $testRoot 'tools\ffmpeg\ffmpeg.exe'
    $ffprobe = Join-Path $testRoot 'tools\ffmpeg\ffprobe.exe'
    $help = & $cli --help 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $help -notmatch 'booksplice <source>') { throw 'Packaged CLI help failed.' }

    $source = Join-Path $testRoot 'generated-source.m4a'
    $output = New-Item -ItemType Directory -Path (Join-Path $testRoot 'output')
    $localData = Join-Path $testRoot 'local-data'
    & $ffmpeg -hide_banner -loglevel error -f lavfi -i 'sine=frequency=440:duration=2' -metadata album=ReleaseSmoke -metadata artist=BookSplice -c:a aac -y $source
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the release smoke-test source.' }
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $priorLocalData = $env:BOOKSPLICE_LOCAL_APP_DATA
    $priorMediaTools = $env:BOOKSPLICE_FFMPEG_DIR
    try {
        $env:BOOKSPLICE_LOCAL_APP_DATA = $localData
        Remove-Item Env:BOOKSPLICE_FFMPEG_DIR -ErrorAction SilentlyContinue

        $settingsDirectory = New-Item -ItemType Directory -Path (Join-Path $localData 'BookSplice')
        $settings = [ordered]@{
            schemaVersion = 1
            outputDirectory = $output.FullName
            qualityProfileId = 'high-quality'
            channelPolicy = 'PreserveSourceChannels'
            createChapters = $true
            conversionJobs = $null
            collisionPolicy = 'AvoidCollision'
            metadataProfileId = 'GenericMp4'
            validationLevel = 'Full'
            logLevel = 'Information'
        } | ConvertTo-Json
        [IO.File]::WriteAllText(
            (Join-Path $settingsDirectory.FullName 'settings.json'),
            $settings,
            [Text.UTF8Encoding]::new($false))

        $guiProcess = Start-Process -FilePath (Join-Path $testRoot 'BookSplice.Gui.exe') -PassThru
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while (-not $guiProcess.HasExited -and $guiProcess.MainWindowTitle -ne 'BookSplice' -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 250
                $guiProcess.Refresh()
            }
            if ($guiProcess.HasExited) { throw "Packaged GUI exited during startup with code $($guiProcess.ExitCode)." }
            if ($guiProcess.MainWindowTitle -ne 'BookSplice') {
                throw "Packaged GUI did not open its main window within 15 seconds. Observed title: '$($guiProcess.MainWindowTitle)'."
            }
            $automationElement = [System.Windows.Automation.AutomationElement]::FromHandle($guiProcess.MainWindowHandle)
            if ($null -eq $automationElement -or $automationElement.Current.AutomationId -ne 'BookSpliceMainWindow') {
                throw 'Packaged GUI opened an unexpected window instead of the BookSplice main window.'
            }
            if (-not $guiProcess.CloseMainWindow()) { throw 'Packaged GUI main window did not accept a close request.' }
            if (-not $guiProcess.WaitForExit(5000)) { throw 'Packaged GUI did not exit normally after its main window closed.' }
            if ($guiProcess.ExitCode -ne 0) { throw "Packaged GUI exited with code $($guiProcess.ExitCode)." }
        }
        finally {
            if (-not $guiProcess.HasExited) {
                Stop-Process -Id $guiProcess.Id -Force
                $guiProcess.WaitForExit()
            }
            $guiProcess.Dispose()
        }

        & $cli $source --output $output.FullName --validation full --metadata-profile GenericMp4
        if ($LASTEXITCODE -ne 0) { throw "Packaged CLI conversion failed with exit code $LASTEXITCODE." }
    }
    finally {
        if ($null -eq $priorLocalData) { Remove-Item Env:BOOKSPLICE_LOCAL_APP_DATA -ErrorAction SilentlyContinue }
        else { $env:BOOKSPLICE_LOCAL_APP_DATA = $priorLocalData }
        if ($null -eq $priorMediaTools) { Remove-Item Env:BOOKSPLICE_FFMPEG_DIR -ErrorAction SilentlyContinue }
        else { $env:BOOKSPLICE_FFMPEG_DIR = $priorMediaTools }
    }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Packaged conversion changed its source file.' }
    $result = @(Get-ChildItem -LiteralPath $output.FullName -Filter '*.m4b' -File)
    if ($result.Count -ne 1) { throw "Expected one packaged conversion output, found $($result.Count)." }
    $decoded = Join-Path $testRoot 'decoded.f32le'
    & $ffmpeg -hide_banner -loglevel error -nostdin -i $result[0].FullName -map '0:a:0' -vn -sn -dn -f f32le -acodec pcm_f32le $decoded
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $decoded -PathType Leaf) -or (Get-Item -LiteralPath $decoded).Length -eq 0) {
        throw 'Packaged media tool could not produce decoded f32 audio for Mp3tag verification.'
    }
    $probeJson = & $ffprobe -v error -show_streams -show_chapters -of json $result[0].FullName | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'Packaged output could not be probed.' }
    $probe = $probeJson | ConvertFrom-Json
    if (@($probe.streams | Where-Object codec_type -eq 'audio').Count -ne 1) { throw 'Packaged output has an invalid audio stream count.' }
    if (@($probe.chapters).Count -ne 1) { throw 'Packaged output has an invalid chapter count.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $localData 'BookSplice\logs') -Filter '*.json' -File).Count -ne 1) {
        throw 'Packaged conversion did not write its audit record.'
    }
}
finally {
    Remove-ReleaseOwnedDirectory $ownedTestRoot
}

[pscustomobject]@{ ArchivePath = $resolvedArchive; Sha256 = $actualHash; SmokeTest = 'passed' }
