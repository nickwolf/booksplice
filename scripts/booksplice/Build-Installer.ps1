[CmdletBinding()]
param(
    # The ZIP from Build-Release.ps1. Setup is compiled from its extracted contents, so both carry the same files.
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    # Defaults to the folder that holds the ZIP.
    [string]$OutputDirectory,

    # Defaults to PATH, then the standard per-user and machine Inno Setup 6 folders.
    [string]$IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$archive = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Archive not found: $archive" }
if ($archive -notmatch '[\\/]BookSplice-(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)-win-x64\.zip$') { throw 'The archive name must be BookSplice-<version>-win-x64.zip.' }
$version = $Matches['version']
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Split-Path $archive -Parent }

if ($PSBoundParameters.ContainsKey('IsccPath') -and -not $IsccPath) { throw '-IsccPath was passed but is empty.' }
function Find-Iscc {
    if ($IsccPath) {
        if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw "ISCC.exe not found at '$IsccPath'." }
        return [IO.Path]::GetFullPath($IsccPath)
    }
    $onPath = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
    throw 'ISCC.exe was not found. Install Inno Setup 6 or pass -IsccPath.'
}
$iscc = Find-Iscc

$work = Join-Path ([IO.Path]::GetTempPath()) ("booksplice-installer-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $source = Join-Path $work 'package'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $source)
    $versionText = Get-Content -LiteralPath (Join-Path $source 'VERSION.txt')
    if ($versionText[0] -ne "BookSplice $version") { throw "VERSION.txt does not match the archive version '$version'." }
    $flavorLine = @($versionText | Where-Object { $_ -match '^Build flavor: (Test|Release)$' })
    if ($flavorLine.Count -ne 1) { throw 'VERSION.txt has no build flavor.' }
    $flavor = ($flavorLine[0] -split ': ')[1]
    # Inno wants up to four numeric parts, so a suffix such as -ci is dropped.
    $numeric = (($version -split '-')[0]) + '.0'

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $setupName = "BookSplice-$version-win-x64-setup.exe"
    $setupPath = Join-Path $output $setupName
    if (Test-Path -LiteralPath $setupPath) { throw "$setupName already exists; use a fresh output directory." }
    & $iscc /Q "/DAppVersion=$version" "/DNumericVersion=$numeric" "/DSourceDir=$source" "/DOutputDir=$output" "/DFlavor=$flavor" (Join-Path $repositoryRoot 'installer\BookSplice.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "ISCC did not produce $setupName." }
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

[pscustomobject]@{
    SetupPath = $setupPath
    Flavor = $flavor
    Sha256 = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
