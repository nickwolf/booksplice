[CmdletBinding()]
param(
    [string]$Version = '6.7.3',
    # SHA-256 of innosetup-<Version>.exe from the jrsoftware/issrc release. Update both together.
    [string]$Sha256 = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732',
    [string]$Destination = (Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })) "InnoSetup-$Version")
)

# CI only. Installs the pinned Inno Setup next to the build instead of trusting whatever the runner image has,
# and prints the path of its ISCC.exe for the caller to capture. It registers an uninstall entry, so do not run it on a developer machine.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tag = 'is-' + $Version.Replace('.', '_')
$installer = Join-Path ([IO.Path]::GetTempPath()) "innosetup-$Version.exe"
Invoke-WebRequest -Uri "https://github.com/jrsoftware/issrc/releases/download/$tag/innosetup-$Version.exe" -OutFile $installer
$actual = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $Sha256) { throw "Inno Setup $Version download hash mismatch: expected $Sha256, got $actual." }
$process = Start-Process -FilePath $installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', "/DIR=`"$Destination`"" -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "The Inno Setup installer failed with exit code $($process.ExitCode)." }
$iscc = Join-Path $Destination 'ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw "ISCC.exe was not installed at $iscc." }
$iscc
