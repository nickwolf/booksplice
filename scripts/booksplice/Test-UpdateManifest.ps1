[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$SignaturePath,
    [Parameter(Mandatory = $true)][string]$PublicKeyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UpdateSigning.ps1')

$archive = [IO.Path]::GetFullPath($ArchivePath)
$manifestBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ManifestPath))
$envelopeBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($SignaturePath))
$rsa = Import-RsaPem ([IO.Path]::GetFullPath($PublicKeyPath))
try {
    $envelope = Read-ExactObject $envelopeBytes $script:EnvelopeFields
    if ($envelope.schemaVersion -ne 1 -or $envelope.algorithm -cne 'RSA-PSS-SHA256') { throw 'Unsupported signature envelope.' }
    if ($envelope.keyId -cne (Get-KeyId $rsa)) { throw 'Signature key id does not match the public key.' }
    $signature = [Convert]::FromBase64String([string]$envelope.signature)
    if ($signature.Length -ne 384 -or
        -not $rsa.VerifyData((Get-SignedMessage $manifestBytes), $signature, 'SHA256', [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'Manifest signature did not verify.'
    }

    $manifest = Read-ExactObject $manifestBytes $script:ManifestFields
    $name = [IO.Path]::GetFileName($archive)
    $version = [string]$manifest.version
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -cne 'BookSplice' -or $manifest.repository -cne 'nickwolf/booksplice' -or
        $manifest.target -cne 'win-x64' -or $manifest.packageProtocol -ne 1 -or
        $version -cnotmatch '^\d+\.\d+\.\d+$' -or $manifest.tag -cne "v$version" -or
        $manifest.archiveName -cne "BookSplice-$version-win-x64.zip" -or $manifest.archiveName -cne $name -or
        $manifest.archiveLength -ne (Get-Item -LiteralPath $archive).Length -or $manifest.archiveSha256 -cne $hash -or
        $manifest.settingsSchemaMin -gt $manifest.settingsSchemaMax) {
        throw 'Manifest does not match the archive.'
    }
    [pscustomobject]@{ ManifestSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant(); KeyId = $envelope.keyId }
}
finally { $rsa.Dispose() }
