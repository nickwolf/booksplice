# Shared by New-UpdateManifest.ps1 and Test-UpdateManifest.ps1. Dot-source only.
Set-StrictMode -Version Latest

$script:ManifestFields = @('schemaVersion', 'product', 'repository', 'tag', 'version', 'target', 'archiveName',
    'archiveLength', 'archiveSha256', 'packageProtocol', 'settingsSchemaMin', 'settingsSchemaMax')
$script:EnvelopeFields = @('schemaVersion', 'keyId', 'algorithm', 'signature')

function Get-SignedMessage([byte[]]$ManifestBytes) {
    # ASCII "BookSplice update manifest v1", LF, then the exact manifest bytes.
    $domain = [Text.Encoding]::ASCII.GetBytes("BookSplice update manifest v1`n")
    $message = [byte[]]::new($domain.Length + $ManifestBytes.Length)
    [Array]::Copy($domain, $message, $domain.Length)
    [Array]::Copy($ManifestBytes, 0, $message, $domain.Length, $ManifestBytes.Length)
    , $message
}

function Import-RsaPem([string]$Path) {
    $rsa = [Security.Cryptography.RSA]::Create()
    try { $rsa.ImportFromPem([IO.File]::ReadAllText($Path)) }
    catch { $rsa.Dispose(); throw "Could not read an RSA key from '$Path'." }
    if ($rsa.KeySize -ne 3072) { $rsa.Dispose(); throw 'The update signing key must be RSA 3072.' }
    $rsa
}

function Get-KeyId([Security.Cryptography.RSA]$Rsa) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
}

function Read-ExactObject([byte[]]$Bytes, [string[]]$Fields) {
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    $document = [Text.Json.JsonDocument]::Parse($text)
    try {
        $names = @($document.RootElement.EnumerateObject() | ForEach-Object { $_.Name })
        if ($document.RootElement.ValueKind -ne 'Object' -or $names.Count -ne $Fields.Count -or
            @($names | Select-Object -Unique).Count -ne $names.Count -or @($names | Where-Object { $_ -cnotin $Fields }).Count -ne 0) {
            throw 'Signed metadata does not have the exact expected fields.'
        }
    }
    finally { $document.Dispose() }
    $text | ConvertFrom-Json
}
