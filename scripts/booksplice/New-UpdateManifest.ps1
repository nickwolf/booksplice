[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$SigningKeyPath,
    [Parameter(Mandatory = $true)][string]$PublicKeyPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [int]$SettingsSchemaMin = 1,
    [int]$SettingsSchemaMax = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdateSigning.ps1')

$archive = [IO.Path]::GetFullPath($ArchivePath)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$archiveName = "BookSplice-$Version-win-x64.zip"
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'Release archive was not found.' }
if ([IO.Path]::GetFileName($archive) -cne $archiveName) { throw "Archive must be named '$archiveName'." }
if (Test-Path -LiteralPath $output) { throw 'Output directory already exists; use a fresh directory.' }

# Field order and spelling are the signed contract; the exact bytes written here are the bytes signed.
$manifestText = '{{"schemaVersion":1,"product":"BookSplice","repository":"nickwolf/booksplice","tag":"v{0}","version":"{0}","target":"win-x64","archiveName":"{1}","archiveLength":{2},"archiveSha256":"{3}","packageProtocol":1,"settingsSchemaMin":{4},"settingsSchemaMax":{5}}}' -f
    $Version, $archiveName, (Get-Item -LiteralPath $archive).Length, (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(), $SettingsSchemaMin, $SettingsSchemaMax
$manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes($manifestText)

$rsa = Import-RsaPem ([IO.Path]::GetFullPath($SigningKeyPath))
try {
    $signature = $rsa.SignData((Get-SignedMessage $manifestBytes), 'SHA256', [Security.Cryptography.RSASignaturePadding]::Pss)
    $envelopeText = '{{"schemaVersion":1,"keyId":"{0}","algorithm":"RSA-PSS-SHA256","signature":"{1}"}}' -f (Get-KeyId $rsa), [Convert]::ToBase64String($signature)
}
finally { $rsa.Dispose() }

New-Item -ItemType Directory -Path $output | Out-Null
$manifestPath = Join-Path $output "BookSplice-$Version-win-x64.update.json"
$signaturePath = Join-Path $output "BookSplice-$Version-win-x64.update.sig.json"
try {
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    [IO.File]::WriteAllBytes($signaturePath, [Text.UTF8Encoding]::new($false).GetBytes($envelopeText))
    $checked = & (Join-Path $PSScriptRoot 'Test-UpdateManifest.ps1') -ArchivePath $archive -ManifestPath $manifestPath `
        -SignaturePath $signaturePath -PublicKeyPath ([IO.Path]::GetFullPath($PublicKeyPath))
}
catch {
    Remove-Item -LiteralPath $output -Recurse -Force
    throw
}
[pscustomobject]@{ ManifestPath = $manifestPath; SignaturePath = $signaturePath; ManifestSha256 = $checked.ManifestSha256; KeyId = $checked.KeyId }
