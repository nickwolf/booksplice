[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root = Join-Path $repositoryRoot "artifacts\update-tooling-test\$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$new = Join-Path $PSScriptRoot 'New-UpdateManifest.ps1'
$check = Join-Path $PSScriptRoot 'Test-UpdateManifest.ps1'
$failures = [Collections.Generic.List[string]]::new()

function New-KeyPair([string]$Name) {
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        [IO.File]::WriteAllText((Join-Path $root "$Name.key.pem"), $rsa.ExportPkcs8PrivateKeyPem())
        [IO.File]::WriteAllText((Join-Path $root "$Name.pub.pem"), $rsa.ExportSubjectPublicKeyInfoPem())
    }
    finally { $rsa.Dispose() }
}

function Expect-Failure([string]$Name, [scriptblock]$Action) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if (-not $threw) { $failures.Add("Expected failure but succeeded: $Name") }
}

function Expect-Success([string]$Name, [scriptblock]$Action) {
    try { & $Action | Out-Null } catch { $failures.Add("Unexpected failure: $Name ($($_.Exception.Message))") }
}

try {
    New-KeyPair 'a'
    New-KeyPair 'b'
    $zip = Join-Path $root 'BookSplice-1.2.3-win-x64.zip'
    [IO.File]::WriteAllText($zip, 'abc')
    $out = Join-Path $root 'out'
    $a = @{ SigningKeyPath = "$root\a.key.pem"; PublicKeyPath = "$root\a.pub.pem" }

    Expect-Success 'sign and verify' { & $new -ArchivePath $zip -Version 1.2.3 -OutputDirectory $out @a }
    $m = Join-Path $out 'BookSplice-1.2.3-win-x64.update.json'
    $s = Join-Path $out 'BookSplice-1.2.3-win-x64.update.sig.json'
    Expect-Success 'verify assets' { & $check -ArchivePath $zip -ManifestPath $m -SignaturePath $s -PublicKeyPath "$root\a.pub.pem" }

    Expect-Failure 'wrong public key' { & $check -ArchivePath $zip -ManifestPath $m -SignaturePath $s -PublicKeyPath "$root\b.pub.pem" }
    $otherZip = Join-Path $root 'other\BookSplice-1.2.3-win-x64.zip'
    New-Item -ItemType Directory -Path (Split-Path $otherZip) | Out-Null
    [IO.File]::WriteAllText($otherZip, 'abd')
    Expect-Failure 'wrong zip bytes' { & $check -ArchivePath $otherZip -ManifestPath $m -SignaturePath $s -PublicKeyPath "$root\a.pub.pem" }
    $renamed = Join-Path $root 'BookSplice-1.2.4-win-x64.zip'
    Copy-Item $zip $renamed
    Expect-Failure 'wrong version in zip name' { & $check -ArchivePath $renamed -ManifestPath $m -SignaturePath $s -PublicKeyPath "$root\a.pub.pem" }
    Expect-Failure 'version does not match zip name' { & $new -ArchivePath $zip -Version 1.2.4 -OutputDirectory (Join-Path $root 'o2') @a }
    Expect-Failure 'prerelease version' { & $new -ArchivePath $zip -Version 1.2.3-ci -OutputDirectory (Join-Path $root 'o3') @a }
    Expect-Failure 'existing output directory' { & $new -ArchivePath $zip -Version 1.2.3 -OutputDirectory $out @a }
    Expect-Failure 'public key does not match signing key' {
        & $new -ArchivePath $zip -Version 1.2.3 -OutputDirectory (Join-Path $root 'o4') -SigningKeyPath "$root\a.key.pem" -PublicKeyPath "$root\b.pub.pem" }
    if (Test-Path (Join-Path $root 'o4')) { $failures.Add('Failed signing left an output directory.') }

    $tampered = Join-Path $root 'tampered.json'
    $bytes = [IO.File]::ReadAllBytes($m); $bytes[20] = $bytes[20] -bxor 1
    [IO.File]::WriteAllBytes($tampered, $bytes)
    Expect-Failure 'tampered manifest' { & $check -ArchivePath $zip -ManifestPath $tampered -SignaturePath $s -PublicKeyPath "$root\a.pub.pem" }

    # A production build refuses to proceed without the reviewed production key, and nothing generates one.
    $productionKey = Join-Path $repositoryRoot 'src\BookSplice.Updates\Trust\production-public-key.pem'
    if (-not (Test-Path -LiteralPath $productionKey)) {
        Expect-Failure 'Build-Release Release flavor without production key' {
            & (Join-Path $repositoryRoot 'scripts\Build-Release.ps1') -Version 1.2.3 -MediaToolDirectory $root -Flavor Release -OutputDirectory (Join-Path $root 'rel') }
        & dotnet build (Join-Path $repositoryRoot 'src\BookSplice.Updates\BookSplice.Updates.csproj') -p:BookSpliceFlavor=Release -o (Join-Path $root 'bin') *> $null
        if ($LASTEXITCODE -eq 0) { $failures.Add('Release msbuild flavor built without the production key.') }
        if (Test-Path -LiteralPath $productionKey) { $failures.Add('A production key was created.') }
    }
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Output 'Update tooling tests passed.'
