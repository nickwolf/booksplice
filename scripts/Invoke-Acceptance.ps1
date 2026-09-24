[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('run', 'prepare-mp3tag', 'verify-mp3tag')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$Corpus,

    [Parameter(Mandatory = $true)]
    [string]$Release,

    [string]$ResultPath,

    [string]$StatePath,

    [string]$CaseId,

    [string]$Mp3tagOutputPath,

    [string]$CliResultPath,

    [switch]$ConfirmedReopened
)

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'BookSplice private acceptance requires PowerShell 7 or later.'
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CliCategories = @('long-audiobook', 'very-long-audiobook', 'another-drive')
$script:AllCategories = @($script:CliCategories + 'mp3tag-roundtrip')
$script:OwnershipMarker = '.booksplice-acceptance-owned'
$script:MinimumDurationByCategory = @{
    'long-audiobook' = [decimal]3600
    'very-long-audiobook' = [decimal]21600
}
$script:RepositoryRoot = [IO.Path]::GetDirectoryName($PSScriptRoot)
$script:AcceptanceArtifactRoot = Join-Path $script:RepositoryRoot 'artifacts\acceptance'
$script:Mp3tagStateIntegrityName = '.booksplice-mp3tag-state-fingerprint'

function Resolve-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A required path was not supplied.' }
    return [IO.Path]::GetFullPath($Path)
}

function Test-IsWithin([string]$Parent, [string]$Child) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $childFull = [IO.Path]::GetFullPath($Child).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ([string]::Equals($parentFull, $childFull, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $childFull.StartsWith($parentFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathsEqual([string]$Left, [string]$Right) {
    $leftFull = [IO.Path]::GetFullPath($Left).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $rightFull = [IO.Path]::GetFullPath($Right).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return [string]::Equals($leftFull, $rightFull, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-AcceptanceArtifactPath([string]$Path, [string]$LeafPattern, [string]$Kind) {
    $root = Resolve-FullPath $script:AcceptanceArtifactRoot
    $resolved = Resolve-FullPath $Path
    $leaf = [IO.Path]::GetFileName($resolved)
    if ((Test-PathsEqual $root $resolved) -or -not (Test-IsWithin $root $resolved) -or $leaf -notmatch $LeafPattern) {
        throw "$Kind must use an approved JSON path under artifacts\acceptance."
    }
    Assert-NoReparseAncestors $resolved
    return $resolved
}

function Resolve-PrivateManifestPath([string]$Path) {
    return Resolve-AcceptanceArtifactPath $Path '^private-(?!.*-result\.json$)[A-Za-z0-9._-]+\.json$' 'The private manifest'
}

function Resolve-AcceptanceResultPath([string]$Path) {
    return Resolve-AcceptanceArtifactPath $Path '^(?:private|mp3tag)-[A-Za-z0-9._-]*result\.json$' 'The acceptance result'
}

function Get-OptionalProperty($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-BytesFingerprint([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ConvertTo-Base64Url ($sha.ComputeHash($Bytes)) } finally { $sha.Dispose() }
}

function Get-CorpusFingerprintFromSnapshot([byte[]]$ManifestBytes, [object]$Manifest) {
    $records = [Text.StringBuilder]::new()
    [void]$records.Append((Get-BytesFingerprint $ManifestBytes)).Append([char]10)
    foreach ($case in @($Manifest.cases | Sort-Object caseId)) {
        $source = Resolve-FullPath ([string]$case.sourcePath)
        foreach ($file in Get-SourceFiles $source) {
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            [void]$records.Append([string]$case.caseId).Append([char]0).Append($relative).Append([char]0).Append($hash).Append([char]10)
        }
    }
    return Get-BytesFingerprint ([Text.UTF8Encoding]::new($false).GetBytes($records.ToString()))
}

function Get-CorpusFingerprint([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-FullPath $Path))
    $manifest = ConvertFrom-PrivateManifestBytes $bytes
    return Get-CorpusFingerprintFromSnapshot $bytes $manifest
}

function Get-ReleaseFingerprint([string]$ReleaseRoot) {
    $root = Resolve-FullPath $ReleaseRoot
    Assert-NoReparsePoints $root
    $pathsByRelative = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Recurse -Force)) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/').ToLowerInvariant()
        if (-not $pathsByRelative.TryAdd($relative, $file.FullName)) {
            throw 'The extracted release contains duplicate canonical paths.'
        }
    }
    if ($pathsByRelative.Count -eq 0) { throw 'The extracted release is empty.' }

    [string[]]$relativePaths = @($pathsByRelative.Keys)
    [Array]::Sort($relativePaths, [StringComparer]::Ordinal)
    $records = [Text.StringBuilder]::new()
    foreach ($relative in $relativePaths) {
        $hash = (Get-FileHash -LiteralPath $pathsByRelative[$relative] -Algorithm SHA256).Hash.ToLowerInvariant()
        [void]$records.Append($relative).Append([char]0).Append($hash).Append([char]10)
    }
    return Get-BytesFingerprint ([Text.UTF8Encoding]::new($false).GetBytes($records.ToString()))
}

function Assert-EvidenceInputsUnchanged([string]$CorpusPath, [string]$ReleaseRoot, [string]$CorpusFingerprint, [string]$ReleaseFingerprint) {
    if (-not [string]::Equals((Get-CorpusFingerprint $CorpusPath), $CorpusFingerprint, [StringComparison]::Ordinal) -or
        -not [string]::Equals((Get-ReleaseFingerprint $ReleaseRoot), $ReleaseFingerprint, [StringComparison]::Ordinal)) {
        throw 'The private acceptance corpus or extracted release changed during execution.'
    }
}

function Assert-AllowedProperties($Object, [string[]]$Allowed, [string]$Context) {
    $unknown = @($Object.PSObject.Properties.Name | Where-Object { $_ -notin $Allowed })
    if ($unknown.Count -gt 0) { throw "The private acceptance $Context contains unsupported properties." }
}

function Assert-NoReparsePoints([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Private acceptance paths cannot contain reparse points.'
    }

    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force -Recurse)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Private acceptance paths cannot contain reparse points.'
            }
        }
    }
}

function Assert-NoReparseAncestors([string]$Path) {
    $candidate = Resolve-FullPath $Path
    while (-not (Test-Path -LiteralPath $candidate)) {
        $parent = [IO.Path]::GetDirectoryName($candidate.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
        if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $candidate, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Private acceptance could not resolve the destination root.'
        }
        $candidate = $parent
    }

    $current = Get-Item -LiteralPath $candidate -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Private acceptance destination ancestors cannot be reparse points.'
        }

        $parentPath = [IO.Path]::GetDirectoryName($current.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
        if ([string]::IsNullOrWhiteSpace($parentPath) -or [string]::Equals($parentPath, $current.FullName, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = Get-Item -LiteralPath $parentPath -Force
    }
}

function New-OwnedDirectory([string]$BaseRoot) {
    $base = Resolve-FullPath $BaseRoot
    Assert-NoReparseAncestors $base
    New-Item -ItemType Directory -Path $base -Force | Out-Null

    $owned = Join-Path $base ('booksplice-acceptance-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $owned | Out-Null
    $token = [guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $owned $script:OwnershipMarker), $token, [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ BaseRoot = $base; OwnedRoot = $owned; Token = $token }
}

function Assert-OwnedDirectory([string]$BaseRoot, [string]$OwnedRoot, [string]$Token) {
    $base = Resolve-FullPath $BaseRoot
    $owned = Resolve-FullPath $OwnedRoot
    Assert-NoReparseAncestors $base
    Assert-NoReparseAncestors $owned
    if ((Test-PathsEqual $base $owned) -or -not (Test-IsWithin $base $owned)) {
        throw 'Private acceptance cleanup refused a directory outside its owned root.'
    }
    if ([IO.Path]::GetFileName($owned) -notmatch '^booksplice-acceptance-[0-9a-f]{32}$') {
        throw 'Private acceptance cleanup refused an unowned directory name.'
    }
    if (-not (Test-Path -LiteralPath $owned -PathType Container)) { return $false }

    Assert-NoReparsePoints $owned
    $marker = Join-Path $owned $script:OwnershipMarker
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        throw 'Private acceptance cleanup could not verify its ownership marker.'
    }
    $actualToken = [IO.File]::ReadAllText($marker).Trim()
    if (-not [string]::Equals($actualToken, $Token, [StringComparison]::Ordinal)) {
        throw 'Private acceptance cleanup could not verify its ownership token.'
    }
    return $true
}

function Remove-OwnedDirectory([string]$BaseRoot, [string]$OwnedRoot, [string]$Token) {
    if (-not (Assert-OwnedDirectory $BaseRoot $OwnedRoot $Token)) { return }
    Remove-Item -LiteralPath (Resolve-FullPath $OwnedRoot) -Recurse -Force
}
function Get-SourceFiles([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw 'A private acceptance source directory was not found.'
    }
    Assert-NoReparseAncestors $Path
    Assert-NoReparsePoints $Path
    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse | Sort-Object FullName)
    if ($files.Count -eq 0) { throw 'A private acceptance source directory was empty.' }
    return $files
}

function Get-Hashes([string]$Path) {
    $root = Resolve-FullPath $Path
    $map = [ordered]@{}
    foreach ($file in Get-SourceFiles $root) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
        $map[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Get-MapKeys($Map) {
    if ($Map -is [Collections.IDictionary]) { return @($Map.Keys) }
    return @($Map.PSObject.Properties.Name)
}

function Get-MapValue($Map, [string]$Key) {
    if ($Map -is [Collections.IDictionary]) { return $Map[$Key] }
    $property = $Map.PSObject.Properties[$Key]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-HashMapsEqual($Before, $After) {
    $beforeKeys = @(Get-MapKeys $Before)
    $afterKeys = @(Get-MapKeys $After)
    if ($beforeKeys.Count -ne $afterKeys.Count) { return $false }
    foreach ($key in $beforeKeys) {
        $afterValue = Get-MapValue $After $key
        if ($null -eq $afterValue -or (Get-MapValue $Before $key) -cne $afterValue) { return $false }
    }
    return $true
}

function Test-SourcesUnchanged([object]$Copy) {
    try {
        return (Test-HashMapsEqual $Copy.OriginalHashes (Get-Hashes $Copy.OriginalPath)) -and
            (Test-HashMapsEqual $Copy.CopiedHashes (Get-Hashes $Copy.CopiedPath))
    }
    catch {
        return $false
    }
}
function Get-DriveClass([string]$Source, [string]$Output) {
    $sourceRoot = [IO.Path]::GetPathRoot((Resolve-FullPath $Source))
    $outputRoot = [IO.Path]::GetPathRoot((Resolve-FullPath $Output))
    if ([string]::IsNullOrWhiteSpace($sourceRoot) -or [string]::IsNullOrWhiteSpace($outputRoot)) { return 'unknown' }
    if ([string]::Equals($sourceRoot, $outputRoot, [StringComparison]::OrdinalIgnoreCase)) { return 'same-volume' }
    return 'other-volume'
}

function Copy-Case([object]$Case, [object]$RunDirectory) {
    $original = Resolve-FullPath ([string]$Case.sourcePath)
    $originalHashes = Get-Hashes $original
    $requestedRoot = Get-OptionalProperty $Case 'copyRoot'
    $baseRoot = if ([string]::IsNullOrWhiteSpace([string]$requestedRoot)) {
        Join-Path $RunDirectory.OwnedRoot 'source'
    } else {
        Resolve-FullPath ([string]$requestedRoot)
    }

    if ((Test-IsWithin $original $baseRoot) -or (Test-IsWithin $baseRoot $original)) {
        throw 'The disposable copy root overlaps the original source.'
    }

    $owned = $null
    try {
        $owned = New-OwnedDirectory $baseRoot
        $leaf = [IO.Path]::GetFileName($original.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
        if ([string]::IsNullOrWhiteSpace($leaf)) { $leaf = 'source' }
        $copyPath = Join-Path $owned.OwnedRoot $leaf
        Copy-Item -LiteralPath $original -Destination $copyPath -Recurse
        Assert-NoReparsePoints $copyPath
        $copiedHashes = Get-Hashes $copyPath
        if (-not (Test-HashMapsEqual $originalHashes $copiedHashes)) {
            throw 'The disposable copy did not match the original source.'
        }

        return [pscustomobject]@{
            OriginalPath = $original
            OriginalHashes = $originalHashes
            CopiedPath = $copyPath
            CopiedHashes = $copiedHashes
            InputCount = @(Get-SourceFiles $copyPath).Count
            BaseRoot = $owned.BaseRoot
            OwnedRoot = $owned.OwnedRoot
            Token = $owned.Token
        }
    }
    catch {
        $originalUnchanged = $false
        try { $originalUnchanged = Test-HashMapsEqual $originalHashes (Get-Hashes $original) } catch { }
        if ($null -ne $owned) {
            try { Remove-OwnedDirectory $owned.BaseRoot $owned.OwnedRoot $owned.Token } catch { }
        }
        if (-not $originalUnchanged) { throw 'The original source changed while its disposable copy was being prepared.' }
        throw
    }
}

function Invoke-JsonProcess([string]$FilePath, [string[]]$Arguments, [hashtable]$Environment) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = [string]$Environment[$key] }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'The requested process did not start.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdoutTask.GetAwaiter().GetResult()
            Stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Read-CliEvents([string]$Stdout) {
    $events = @()
    foreach ($line in @($Stdout -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 })) {
        try { $event = $line | ConvertFrom-Json -Depth 50 } catch { throw 'BookSplice emitted invalid JSON.' }
        if ($event.schemaVersion -ne 1 -or [string]$event.event -notin @('started', 'progress', 'final')) {
            throw 'BookSplice emitted an unrecognized JSON event.'
        }
        $events += $event
    }

    $finals = @($events | Where-Object { $_.event -eq 'final' })
    if ($events.Count -eq 0 -or $finals.Count -ne 1 -or $events[-1].event -ne 'final') {
        throw 'BookSplice did not emit exactly one terminal JSON event.'
    }
    return $events
}

function Get-Audit([string]$LocalData) {
    $logs = Join-Path $LocalData 'BookSplice\logs'
    $files = @(if (Test-Path -LiteralPath $logs -PathType Container) {
        Get-ChildItem -LiteralPath $logs -Filter '*.json' -File
    } else {
        @()
    })
    if ($files.Count -ne 1) { throw 'BookSplice did not produce exactly one audit record.' }
    try { return Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json -Depth 100 } catch {
        throw 'The BookSplice audit record was not valid JSON.'
    }
}

function Test-FullDecodePassed($Audit) {
    if ($null -eq $Audit.validation -or $Audit.validation.isValid -ne $true) { return $false }
    $fullDecode = @($Audit.validation.checks | Where-Object { [string]$_.code -eq 'decode.full' })
    return $fullDecode.Count -eq 1 -and $fullDecode[0].passed -eq $true
}

function Assert-SuccessAudit($Audit) {
    if ([string]$Audit.terminalStatus -ne 'Succeeded') { throw 'The BookSplice audit did not report success.' }
    if ($null -eq $Audit.plan -or [string]$Audit.plan.validationLevel -ne 'Full') {
        throw 'The BookSplice audit did not record full validation.'
    }
    if (-not (Test-FullDecodePassed $Audit)) {
        throw 'The BookSplice audit did not record a successful full decode.'
    }
}

function Normalize-ExpectedRelativePath([string]$CopiedRoot, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or [IO.Path]::IsPathRooted($Value) -or [IO.Path]::IsPathFullyQualified($Value)) {
        throw 'Expected order entries must be root-relative source paths.'
    }

    $root = Resolve-FullPath $CopiedRoot
    $full = [IO.Path]::GetFullPath((Join-Path $root $Value))
    if ([string]::Equals($root, $full, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-IsWithin $root $full)) {
        throw 'An expected order entry escaped the disposable source root.'
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw 'An expected order entry did not identify a copied source file.'
    }
    return [IO.Path]::GetRelativePath($root, $full).Replace([IO.Path]::AltDirectorySeparatorChar, [IO.Path]::DirectorySeparatorChar)
}

function Assert-ExpectedOrder($Audit, [object]$Copy, [object]$Case) {
    $expected = @()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @((Get-OptionalProperty $Case 'expectedOrder'))) {
        $relative = Normalize-ExpectedRelativePath $Copy.CopiedPath ([string]$value)
        if (-not $seen.Add($relative)) { throw 'The expected order contains duplicate source paths.' }
        $expected += $relative
    }

    $actual = @()
    foreach ($value in @($Audit.orderedSources)) {
        $full = Resolve-FullPath ([string]$value)
        if ([string]::Equals($Copy.CopiedPath, $full, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-IsWithin $Copy.CopiedPath $full)) {
            throw 'The audit order contains a source outside the disposable copy.'
        }
        $actual += [IO.Path]::GetRelativePath($Copy.CopiedPath, $full).Replace([IO.Path]::AltDirectorySeparatorChar, [IO.Path]::DirectorySeparatorChar)
    }

    if ($actual.Count -ne $expected.Count) { throw 'The audit order did not match the private manifest.' }
    for ($index = 0; $index -lt $actual.Count; $index++) {
        if (-not [string]::Equals($actual[$index], $expected[$index], [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The audit order did not match the private manifest.'
        }
    }
}

function Get-InputAudioDurationSeconds($Audit) {
    $summaries = @($Audit.analysis.sourceSummaries)
    if ($summaries.Count -eq 0 -or $summaries.Count -ne @($Audit.orderedSources).Count) {
        throw 'The BookSplice audit did not contain complete source duration evidence.'
    }

    [decimal]$total = 0
    foreach ($summary in $summaries) {
        $raw = Get-OptionalProperty $summary 'duration'
        if ($null -eq $raw) { throw 'The BookSplice audit omitted a source duration.' }
        try { [decimal]$duration = $raw } catch { throw 'The BookSplice audit contained an invalid source duration.' }
        if ($duration -le 0) { throw 'The BookSplice audit contained a non-positive source duration.' }
        $total += $duration
    }
    return $total
}

function Assert-CaseDuration($Audit, [object]$Case) {
    $category = [string]$Case.category
    if (-not $script:MinimumDurationByCategory.ContainsKey($category)) { return }
    [decimal]$minimum = Get-OptionalProperty $Case 'minimumAudioDurationSeconds'
    if ($minimum -lt $script:MinimumDurationByCategory[$category]) {
        throw 'The private acceptance duration requirement is below the category floor.'
    }
    if ((Get-InputAudioDurationSeconds $Audit) -lt $minimum) {
        throw 'The disposable audiobook did not meet its declared duration requirement.'
    }
}

function Get-ReleasePaths([string]$ReleaseRoot) {
    $root = Resolve-FullPath $ReleaseRoot
    Assert-NoReparseAncestors $root
    Assert-NoReparsePoints $root
    if ((Test-IsWithin $root $script:AcceptanceArtifactRoot) -or (Test-IsWithin $script:AcceptanceArtifactRoot $root)) {
        throw 'The extracted release cannot overlap private acceptance artifacts.'
    }
    $cli = Join-Path $root 'booksplice.exe'
    $ffmpeg = Join-Path $root 'tools\ffmpeg\ffmpeg.exe'
    $ffprobe = Join-Path $root 'tools\ffmpeg\ffprobe.exe'
    foreach ($path in @($cli, $ffmpeg, $ffprobe)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw 'The extracted release is missing a required executable.'
        }
    }
    return [pscustomobject]@{ Root = $root; Cli = $cli; Ffmpeg = $ffmpeg; Ffprobe = $ffprobe; ToolDirectory = Split-Path $ffmpeg -Parent }
}

function Assert-ManifestPathIsolation([object]$Manifest, [string]$ReleaseRoot) {
    $sources = @($Manifest.cases | ForEach-Object { Resolve-FullPath ([string]$_.sourcePath) })
    $release = Resolve-FullPath $ReleaseRoot
    $tempBase = Resolve-FullPath ([IO.Path]::GetTempPath())

    foreach ($source in $sources) {
        if ((Test-IsWithin $source $release) -or (Test-IsWithin $release $source)) {
            throw 'The extracted release cannot overlap a private acceptance source.'
        }
    }

    foreach ($case in @($Manifest.cases)) {
        $requestedRoot = Get-OptionalProperty $case 'copyRoot'
        if ([string]::IsNullOrWhiteSpace([string]$requestedRoot)) { continue }
        $copyRoot = Resolve-FullPath ([string]$requestedRoot)
        Assert-NoReparseAncestors $copyRoot
        if (-not (Test-Path -LiteralPath $copyRoot -PathType Container)) {
            throw 'A manifest copy root must be a pre-existing dedicated directory.'
        }
        Assert-NoReparsePoints $copyRoot
        foreach ($source in $sources) {
            if ((Test-IsWithin $source $copyRoot) -or (Test-IsWithin $copyRoot $source)) {
                throw 'A manifest copy root overlaps a private acceptance source.'
            }
        }
        foreach ($protected in @($release, (Resolve-FullPath $script:AcceptanceArtifactRoot))) {
            if ((Test-IsWithin $protected $copyRoot) -or (Test-IsWithin $copyRoot $protected)) {
                throw 'A manifest copy root overlaps protected acceptance inputs.'
            }
        }
        if (@(Get-ChildItem -LiteralPath $copyRoot -Force).Count -ne 0) {
            throw 'A manifest copy root must be empty before the release gate starts.'
        }
    }

    foreach ($source in $sources) {
        if ((Test-IsWithin $source $tempBase) -or (Test-IsWithin $tempBase $source)) {
            throw 'Private acceptance sources cannot overlap the default disposable run base.'
        }
    }
}function ConvertFrom-PrivateManifestBytes([byte[]]$Bytes) {
    try {
        $json = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
        $manifest = $json | ConvertFrom-Json -Depth 100
    }
    catch {
        throw 'The private acceptance manifest was not valid UTF-8 JSON.'
    }

    Assert-AllowedProperties $manifest @('schemaVersion', 'privateManifest', 'copyPermission', 'cases') 'manifest'
    if ([int]$manifest.schemaVersion -ne 1 -or $manifest.privateManifest -ne $true -or $manifest.copyPermission -ne $true) {
        throw 'The private acceptance manifest must explicitly set schemaVersion=1, privateManifest=true, and copyPermission=true.'
    }
    if ($null -eq $manifest.cases -or @($manifest.cases).Count -ne 4) {
        throw 'The private acceptance manifest must contain exactly four cases.'
    }

    $ids = @{}
    foreach ($case in @($manifest.cases)) {
        Assert-AllowedProperties $case @('caseId', 'category', 'sourcePath', 'copyPermission', 'copyRoot', 'sourceDriveClass', 'expectedOrder', 'minimumAudioDurationSeconds', 'order', 'channels', 'quality', 'mp3tagFields') 'case'
        $id = [string]$case.caseId
        $category = [string]$case.category
        if ($id -notmatch '^case-[0-9]{2,4}$' -or $ids.ContainsKey($id)) {
            throw 'Private acceptance case IDs must be unique anonymous values such as case-01.'
        }
        if ($category -notin $script:AllCategories) { throw 'The private acceptance manifest contains an unsupported category.' }
        if ($case.copyPermission -ne $true) { throw 'Every private acceptance case must explicitly set copyPermission=true.' }
        if ([string]::IsNullOrWhiteSpace([string]$case.sourcePath)) { throw 'A private acceptance case is missing its source directory.' }
        $sourcePath = Resolve-FullPath ([string]$case.sourcePath)
        Assert-NoReparseAncestors $sourcePath
        if ((Test-IsWithin $sourcePath $script:AcceptanceArtifactRoot) -or (Test-IsWithin $script:AcceptanceArtifactRoot $sourcePath)) {
            throw 'Private acceptance source paths cannot overlap acceptance artifacts.'
        }

        $expectedOrder = Get-OptionalProperty $case 'expectedOrder'
        $relativePaths = @($expectedOrder | ForEach-Object { [string]$_ })
        if ($null -eq $expectedOrder -or $relativePaths.Count -eq 0) {
            throw 'Every private acceptance case requires an expected root-relative source order.'
        }
        foreach ($relativePath in $relativePaths) {
            if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or [IO.Path]::IsPathFullyQualified($relativePath)) {
                throw 'Expected order entries must be root-relative source paths.'
            }
        }

        $minimumDuration = Get-OptionalProperty $case 'minimumAudioDurationSeconds'
        if ($script:MinimumDurationByCategory.ContainsKey($category)) {
            if ($null -eq $minimumDuration) { throw 'Long-form private acceptance cases require a minimum audio duration.' }
            try { $minimumDuration = [decimal]$minimumDuration } catch { throw 'The minimum audio duration must be a number.' }
            if ($minimumDuration -lt $script:MinimumDurationByCategory[$category]) {
                throw 'The minimum audio duration is below the release-gate floor.'
            }
        } elseif ($null -ne $minimumDuration) {
            throw 'This private acceptance category does not support a minimum audio duration.'
        }

        if ($category -in $script:CliCategories) {
            if ([string]::IsNullOrWhiteSpace([string](Get-OptionalProperty $case 'order'))) {
                throw 'CLI private acceptance cases require an explicit order.'
            }
            if ($null -ne (Get-OptionalProperty $case 'mp3tagFields')) {
                throw 'CLI private acceptance cases cannot contain Mp3tag fields.'
            }
        }

        if ($category -eq 'another-drive') {
            if ([string]::IsNullOrWhiteSpace([string](Get-OptionalProperty $case 'copyRoot')) -or [string](Get-OptionalProperty $case 'sourceDriveClass') -ne 'other-volume') {
                throw 'The another-drive case requires copyRoot and sourceDriveClass=other-volume.'
            }
        }

        if ($category -eq 'mp3tag-roundtrip') {
            if ($null -ne (Get-OptionalProperty $case 'copyRoot') -or $null -ne (Get-OptionalProperty $case 'sourceDriveClass')) {
                throw 'The Mp3tag case must keep its disposable copy inside the owned run tree.'
            }
            $fields = Get-OptionalProperty $case 'mp3tagFields'
            if ($null -eq $fields -or @($fields.PSObject.Properties).Count -eq 0) {
                throw 'The Mp3tag case requires explicit expected fields.'
            }
            $fieldNames = @($fields.PSObject.Properties.Name)
            foreach ($requiredField in @('TITLE', 'ALBUM', 'ARTIST', 'ALBUMARTIST')) {
                if ($requiredField -notin $fieldNames) { throw 'The Mp3tag case is missing a required mapped field.' }
            }
            if ([string]::IsNullOrWhiteSpace([string](Get-OptionalProperty $case 'order'))) {
                throw 'The Mp3tag case requires an explicit order.'
            }
        }

        $ids[$id] = $true
    }

    foreach ($requiredCategory in $script:AllCategories) {
        if (@($manifest.cases | Where-Object { [string]$_.category -eq $requiredCategory }).Count -ne 1) {
            throw 'The private acceptance manifest requires exactly one case for each release-gate category.'
        }
    }
    return $manifest
}

function Read-PrivateManifestSnapshot([string]$Path) {
    $resolved = Resolve-PrivateManifestPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw 'The private acceptance manifest was not found.' }
    $bytes = [IO.File]::ReadAllBytes($resolved)
    $manifest = ConvertFrom-PrivateManifestBytes $bytes
    return [pscustomobject]@{
        Path = $resolved
        Manifest = $manifest
        Fingerprint = Get-CorpusFingerprintFromSnapshot $bytes $manifest
    }
}

function Read-PrivateManifest([string]$Path) {
    return (Read-PrivateManifestSnapshot $Path).Manifest
}

function Get-Case([object]$Manifest, [string]$Id) {
    if ([string]::IsNullOrWhiteSpace($Id)) { throw 'CaseId is required for this mode.' }
    $matches = @($Manifest.cases | Where-Object { [string]$_.caseId -eq $Id })
    if ($matches.Count -ne 1) { throw 'The requested private acceptance case was not found.' }
    return $matches[0]
}

function Write-AnonymousResult([string]$Path, [object]$Result) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'ResultPath is required.' }
    $json = $Result | ConvertTo-Json -Depth 30
    if ($json -match '[A-Za-z]:[\/]' -or $json -match '\\' -or $json -match '(?i)\b[0-9a-f]{64}\b') {
        throw 'The anonymous result contains a private path or hash.'
    }

    $destination = Resolve-AcceptanceResultPath $Path
    $parent = [IO.Path]::GetDirectoryName($destination)
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Assert-NoReparsePoints $parent
    }
    $partial = Join-Path $parent ('.' + [IO.Path]::GetFileName($destination) + '.' + [guid]::NewGuid().ToString('N') + '.partial')
    try {
        [IO.File]::WriteAllText($partial, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($partial, $destination, $true)
    }
    finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial -Force }
    }
}
function New-NotRunMp3tagResult {
    return [ordered]@{
        status = 'not-run'
        step1Exported = $false
        step2ReopenedSaved = $false
        fieldsVerified = $false
        audioEssencePreserved = $false
        sourceHashesUnchanged = $false
        coverPreserved = $false
    }
}

function New-FailedCaseResult([object]$Case, $ErrorRecord, [string]$FallbackStage = 'unknown') {
    $allowedStages = @('copy', 'process', 'source-check', 'terminal-event', 'terminal-status', 'audit', 'order', 'duration', 'output', 'cleanup', 'fingerprint', 'unknown')
    $failureStage = if ($null -ne $ErrorRecord -and $null -ne $ErrorRecord.Exception) {
        [string]$ErrorRecord.Exception.Data['AcceptanceStage']
    } else {
        ''
    }
    if ([string]::IsNullOrWhiteSpace($failureStage)) { $failureStage = $FallbackStage }
    if ($failureStage -notin $allowedStages) { $failureStage = 'unknown' }

    $row = [ordered]@{
        caseId = [string]$Case.caseId
        category = [string]$Case.category
        status = 'failed'
        sourceFileCount = 0
        driveClass = 'unknown'
        validationStatus = 'failed'
        auditPresent = $false
        fullDecodePassed = $false
        durationRequirementMet = $false
        strategy = 'unknown'
        sourceHashesUnchanged = $false
        failureCode = 'case-failed'
        failureStage = $failureStage
    }

    if ($failureStage -eq 'terminal-status') {
        $allowedStatuses = @('InvalidInput', 'DecisionRequired', 'ExecutionFailed', 'ValidationFailed', 'PublicationFailed', 'Cancelled', 'UnexpectedFailure')
        $processExitCode = [int]$ErrorRecord.Exception.Data['ProcessExitCode']
        $terminalExitCode = [int]$ErrorRecord.Exception.Data['TerminalExitCode']
        $terminalStatus = [string]$ErrorRecord.Exception.Data['TerminalStatus']
        if ($processExitCode -lt 0 -or $processExitCode -gt 8 -or $terminalExitCode -lt 0 -or $terminalExitCode -gt 8 -or $terminalStatus -notin $allowedStatuses) {
            throw 'Private acceptance rejected unrecognized terminal evidence.'
        }
        $diagnosticCodes = @($ErrorRecord.Exception.Data['DiagnosticCodes'] | ForEach-Object { [string]$_ } |
            Where-Object { $_ -match '^[A-Za-z][A-Za-z0-9.-]{0,127}$' } | Sort-Object -Unique)
        $failedValidationCodes = @($ErrorRecord.Exception.Data['FailedValidationCodes'] | ForEach-Object { [string]$_ } |
            Where-Object { $_ -match '^[A-Za-z][A-Za-z0-9.-]{0,127}$' } | Sort-Object -Unique)
        $row.processExitCode = $processExitCode
        $row.terminalExitCode = $terminalExitCode
        $row.terminalStatus = $terminalStatus
        $row.diagnosticCodes = $diagnosticCodes
        $row.failedValidationCodes = $failedValidationCodes
        $row.validationWarningCount = [int]$ErrorRecord.Exception.Data['ValidationWarningCount']
        $row.validationErrorCount = [int]$ErrorRecord.Exception.Data['ValidationErrorCount']
        $row.expectedChapterCount = [int]$ErrorRecord.Exception.Data['ExpectedChapterCount']
        $row.actualChapterCount = [int]$ErrorRecord.Exception.Data['ActualChapterCount']
        $row.nonPositiveChapterCount = [int]$ErrorRecord.Exception.Data['NonPositiveChapterCount']
        $row.overlappingChapterCount = [int]$ErrorRecord.Exception.Data['OverlappingChapterCount']
        $row.distinctChapterIdCount = [int]$ErrorRecord.Exception.Data['DistinctChapterIdCount']
    }

    return $row
}
function Invoke-Case([object]$Case, [object]$ReleasePaths) {
    $runDirectory = New-OwnedDirectory ([IO.Path]::GetTempPath())
    $copy = $null
    $row = $null
    $stage = 'copy'
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $copy = Copy-Case $Case $runDirectory
        $stage = 'output'
        $output = Join-Path $runDirectory.OwnedRoot 'output'
        New-Item -ItemType Directory -Path $output | Out-Null
        if ((Test-IsWithin $copy.CopiedPath $output) -or (Test-IsWithin $output $copy.CopiedPath)) {
            throw 'The private acceptance source and output paths overlap.'
        }

        $driveClass = Get-DriveClass $copy.CopiedPath $output
        $expectedDriveClass = Get-OptionalProperty $Case 'sourceDriveClass'
        if ($null -ne $expectedDriveClass -and [string]$expectedDriveClass -ne $driveClass) {
            throw 'The measured disposable-copy drive class did not match the manifest.'
        }
        if ([string]$Case.category -eq 'another-drive' -and $driveClass -ne 'other-volume') {
            throw 'The another-drive case did not use another volume.'
        }

        $localData = Join-Path $runDirectory.OwnedRoot 'local-data'
        $arguments = @(
            $copy.CopiedPath,
            '--output', $output,
            '--validation', 'full',
            '--json',
            '--order', [string]$Case.order
        )
        foreach ($name in @('channels', 'quality')) {
            $value = Get-OptionalProperty $Case $name
            if ($null -ne $value) { $arguments += @(('--' + $name), [string]$value) }
        }

        $stage = 'process'
        $processResult = Invoke-JsonProcess $ReleasePaths.Cli $arguments @{
            BOOKSPLICE_LOCAL_APP_DATA = $localData
            BOOKSPLICE_FFMPEG_DIR = $ReleasePaths.ToolDirectory
        }

        $stage = 'source-check'
        if (-not (Test-SourcesUnchanged $copy)) { throw 'Private acceptance source hashes changed.' }

        $stage = 'terminal-event'
        $events = Read-CliEvents $processResult.Stdout
        $final = @($events | Where-Object { $_.event -eq 'final' })[0]
        $stage = 'terminal-status'
        if ($processResult.ExitCode -ne 0 -or [int]$final.exitCode -ne 0 -or [string]$final.status -ne 'Succeeded') {
            Write-Verbose ('BookSplice exit={0}, finalExit={1}, status={2}.' -f $processResult.ExitCode, $final.exitCode, $final.status)
            $diagnosticCodes = @()
            $failedValidationCodes = @()
            $validationWarningCount = 0
            $validationErrorCount = 0
            $expectedChapterCount = 0
            $actualChapterCount = 0
            $nonPositiveChapterCount = 0
            $overlappingChapterCount = 0
            $distinctChapterIdCount = 0
            try {
                $failureAudit = Get-Audit $localData
                $diagnosticCodes = @($failureAudit.diagnostics | ForEach-Object { [string]$_.code } |
                    Where-Object { $_ -match '^[A-Za-z][A-Za-z0-9.-]{0,127}$' } | Sort-Object -Unique)
                if ($null -ne $failureAudit.validation) {
                    $failedValidationCodes = @($failureAudit.validation.checks |
                        Where-Object { $_.passed -ne $true -and $_.required -ne $false } |
                        ForEach-Object { [string]$_.code } |
                        Where-Object { $_ -match '^[A-Za-z][A-Za-z0-9.-]{0,127}$' } | Sort-Object -Unique)
                    $validationWarningCount = @($failureAudit.validation.warnings).Count
                    $validationErrorCount = @($failureAudit.validation.errors).Count
                    $expectedChapterCount = @($failureAudit.orderedSources).Count
                    $chapters = @($failureAudit.validation.outputFacts.chapters)
                    $actualChapterCount = $chapters.Count
                    $nonPositiveChapterCount = @($chapters | Where-Object { [decimal]$_.endTime -le [decimal]$_.startTime }).Count
                    for ($chapterIndex = 1; $chapterIndex -lt $chapters.Count; $chapterIndex++) {
                        if ([decimal]$chapters[$chapterIndex].startTime -lt [decimal]$chapters[$chapterIndex - 1].endTime) {
                            $overlappingChapterCount++
                        }
                    }
                    $distinctChapterIdCount = @($chapters | ForEach-Object { [long]$_.id } | Sort-Object -Unique).Count
                    try {
                        $nonPositiveIndexes = @(for ($index = 0; $index -lt $chapters.Count; $index++) {
                            if ([decimal]$chapters[$index].endTime -le [decimal]$chapters[$index].startTime) { $index }
                        })
                        if ($nonPositiveIndexes.Count -eq 1) {
                            if ($nonPositiveIndexes[0] -eq 0) { $diagnosticCodes += 'chapters.nonpositive-first' }
                            elseif ($nonPositiveIndexes[0] -eq $chapters.Count - 1) { $diagnosticCodes += 'chapters.nonpositive-final' }
                            else { $diagnosticCodes += 'chapters.nonpositive-interior' }
                        }
                        $duplicateStartCount = 0
                        for ($index = 1; $index -lt $chapters.Count; $index++) {
                            if ([decimal]$chapters[$index].startTime -eq [decimal]$chapters[$index - 1].startTime) { $duplicateStartCount++ }
                        }
                        if ($duplicateStartCount -gt 0) { $diagnosticCodes += 'chapters.duplicate-start' }
                        if (@($chapters | Where-Object { [decimal]$_.startTime -eq 0 }).Count -gt 1) { $diagnosticCodes += 'chapters.multiple-zero-starts' }
                        if ($chapters.Count -gt 0) {
                            $outputDuration = [decimal]$failureAudit.validation.outputFacts.duration
                            $finalChapter = $chapters[-1]
                            if ($outputDuration -le [decimal]$finalChapter.startTime) { $diagnosticCodes += 'chapters.output-at-or-before-final-start' }
                            if ([math]::Abs($outputDuration - [decimal]$finalChapter.endTime) -le 0.02) { $diagnosticCodes += 'chapters.final-end-matches-output-duration' }
                            $sourceDurations = @($failureAudit.analysis.sourceSummaries | ForEach-Object { [decimal]$_.duration })
                            if ($sourceDurations.Count -eq $chapters.Count) {
                                $analysisTotal = [decimal]($sourceDurations | Measure-Object -Sum).Sum
                                if ([math]::Abs($analysisTotal - $outputDuration) -le 0.02) { $diagnosticCodes += 'duration.analysis-total-matches-output' }
                                $analysisPrefix = if ($sourceDurations.Count -gt 1) { [decimal]($sourceDurations[0..($sourceDurations.Count - 2)] | Measure-Object -Sum).Sum } else { 0m }
                                if ([math]::Abs($analysisPrefix - [decimal]$finalChapter.startTime) -le 0.02) { $diagnosticCodes += 'chapters.final-start-matches-analysis-prefix' }
                                $expectedCursor = 0m
                                $startMismatchIndexes = @()
                                $endMismatchIndexes = @()
                                for ($index = 0; $index -lt $chapters.Count; $index++) {
                                    $expectedEnd = $expectedCursor + [decimal]$sourceDurations[$index]
                                    if ([math]::Abs([decimal]$chapters[$index].startTime - $expectedCursor) -gt 0.02) { $startMismatchIndexes += $index }
                                    if ([math]::Abs([decimal]$chapters[$index].endTime - $expectedEnd) -gt 0.02) { $endMismatchIndexes += $index }
                                    $expectedCursor = $expectedEnd
                                }
                                if ($startMismatchIndexes.Count -eq 1) { $diagnosticCodes += 'chapters.single-start-timing-mismatch' }
                                if ($endMismatchIndexes.Count -eq 1) { $diagnosticCodes += 'chapters.single-end-timing-mismatch' }
                                if ($startMismatchIndexes.Count -gt 0 -and $startMismatchIndexes[0] -eq 0) { $diagnosticCodes += 'chapters.first-start-timing-mismatch' }
                                if ($endMismatchIndexes.Count -gt 0 -and $endMismatchIndexes[0] -eq 0) { $diagnosticCodes += 'chapters.first-end-timing-mismatch' }
                            }
                        }
                    }
                    catch { }                }
            }
            catch { }
            $failure = [InvalidOperationException]::new('BookSplice did not report a successful terminal result.')
            $failure.Data['AcceptanceStage'] = 'terminal-status'
            $failure.Data['ProcessExitCode'] = [int]$processResult.ExitCode
            $failure.Data['TerminalExitCode'] = [int]$final.exitCode
            $failure.Data['TerminalStatus'] = [string]$final.status
            $failure.Data['DiagnosticCodes'] = [string[]]$diagnosticCodes
            $failure.Data['FailedValidationCodes'] = [string[]]$failedValidationCodes
            $failure.Data['ValidationWarningCount'] = [int]$validationWarningCount
            $failure.Data['ValidationErrorCount'] = [int]$validationErrorCount
            $failure.Data['ExpectedChapterCount'] = [int]$expectedChapterCount
            $failure.Data['ActualChapterCount'] = [int]$actualChapterCount
            $failure.Data['NonPositiveChapterCount'] = [int]$nonPositiveChapterCount
            $failure.Data['OverlappingChapterCount'] = [int]$overlappingChapterCount
            $failure.Data['DistinctChapterIdCount'] = [int]$distinctChapterIdCount
            throw $failure
        }

        $stage = 'audit'
        $audit = Get-Audit $localData
        Assert-SuccessAudit $audit
        $stage = 'order'
        Assert-ExpectedOrder $audit $copy $Case
        $stage = 'duration'
        Assert-CaseDuration $audit $Case

        $stage = 'output'
        $outputs = @(Get-ChildItem -LiteralPath $output -Filter '*.m4b' -File)
        if ($outputs.Count -ne 1) { throw 'Private acceptance did not publish exactly one M4B.' }

        $strategy = if ($null -ne $audit.plan -and -not [string]::IsNullOrWhiteSpace([string]$audit.plan.strategy)) {
            [string]$audit.plan.strategy
        } else {
            'unknown'
        }

        $stopwatch.Stop()
        $row = [ordered]@{
            caseId = [string]$Case.caseId
            category = [string]$Case.category
            status = 'passed'
            sourceFileCount = [int]$copy.InputCount
            driveClass = $driveClass
            validationStatus = 'full'
            auditPresent = $true
            fullDecodePassed = $true
            durationRequirementMet = $true
            strategy = $strategy
            sourceHashesUnchanged = $true
            wallSeconds = [math]::Max(0.001, [math]::Round($stopwatch.Elapsed.TotalSeconds, 3))
        }
    }
    catch {
        if (-not $_.Exception.Data.Contains('AcceptanceStage')) {
            $_.Exception.Data['AcceptanceStage'] = $stage
        }
        throw
    }
    finally {
        $sourceCheckFailed = $false
        $cleanupFailed = $false
        if ($null -ne $copy) {
            if (-not (Test-SourcesUnchanged $copy)) { $sourceCheckFailed = $true }
            try { Remove-OwnedDirectory $copy.BaseRoot $copy.OwnedRoot $copy.Token } catch { $cleanupFailed = $true }
        }
        try { Remove-OwnedDirectory $runDirectory.BaseRoot $runDirectory.OwnedRoot $runDirectory.Token } catch { $cleanupFailed = $true }
        if ($sourceCheckFailed) {
            $failure = [InvalidOperationException]::new('Private acceptance source hashes changed.')
            $failure.Data['AcceptanceStage'] = 'source-check'
            throw $failure
        }
        if ($cleanupFailed) {
            $failure = [InvalidOperationException]::new('Private acceptance could not safely clean its owned data.')
            $failure.Data['AcceptanceStage'] = 'cleanup'
            throw $failure
        }
    }
    return $row
}
function Get-DecodedAudioHash([string]$MediaPath, [string]$FfmpegPath) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FfmpegPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-hide_banner', '-loglevel', 'error', '-nostdin', '-i', (Resolve-FullPath $MediaPath), '-map', '0:a:0', '-vn', '-sn', '-dn', '-f', 'f32le', '-acodec', 'pcm_f32le', 'pipe:1')) {
        [void]$start.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        if (-not $process.Start()) { throw 'FFmpeg did not start for decoded-audio verification.' }
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $hash = $sha.ComputeHash($process.StandardOutput.BaseStream)
        $process.WaitForExit()
        [void]$stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'FFmpeg could not decode the Mp3tag candidate.' }
        return [Convert]::ToHexString($hash).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $process.Dispose()
    }
}

function Get-CoverPayloadHash([string]$MediaPath, [string]$FfmpegPath) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FfmpegPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-hide_banner', '-loglevel', 'error', '-nostdin', '-i', (Resolve-FullPath $MediaPath), '-map', '0:v:0', '-c', 'copy', '-f', 'image2pipe', 'pipe:1')) {
        [void]$start.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        if (-not $process.Start()) { throw 'FFmpeg did not start for cover verification.' }
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $hash = $sha.ComputeHash($process.StandardOutput.BaseStream)
        $process.WaitForExit()
        [void]$stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $hash.Length -eq 0) { throw 'FFmpeg could not extract the cover payload.' }
        return [Convert]::ToHexString($hash).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $process.Dispose()
    }
}
function Get-TagValue($TagSets, [string]$Name) {
    $wanted = $Name.ToLowerInvariant().Replace('_', '')
    foreach ($tagSet in @($TagSets)) {
        if ($null -eq $tagSet) { continue }
        foreach ($property in $tagSet.PSObject.Properties) {
            $candidate = $property.Name.ToLowerInvariant().Replace('_', '')
            if ([string]::Equals($candidate, $wanted, [StringComparison]::Ordinal)) {
                return [string]$property.Value
            }
        }
    }
    return $null
}

function Resolve-Mp3tagStatePath([string]$StateFile) {
    $allowedRoot = $script:AcceptanceArtifactRoot
    $resolved = Resolve-FullPath $StateFile
    $leaf = [IO.Path]::GetFileName($resolved)
    if ((Test-PathsEqual $allowedRoot $resolved) -or
        -not (Test-IsWithin $allowedRoot $resolved) -or
        $leaf -notmatch '^mp3tag-[A-Za-z0-9._-]+\.json$' -or
        $leaf -match '-result\.json$') {
        throw 'Mp3tag state must use a mp3tag-*.json path under artifacts\acceptance.'
    }
    Assert-NoReparseAncestors $resolved
    return $resolved
}

function Get-Mp3tagStatePathFingerprint([string]$Path) {
    $normalized = (Resolve-FullPath $Path).ToLowerInvariant()
    return Get-BytesFingerprint ([Text.UTF8Encoding]::new($false).GetBytes($normalized))
}

function Write-Mp3tagState([string]$StatePath, [object]$State, [string]$RunOwnedRoot) {
    $resolved = Resolve-Mp3tagStatePath $StatePath
    $integrityPath = Join-Path (Resolve-FullPath $RunOwnedRoot) $script:Mp3tagStateIntegrityName
    $stateCreated = $false
    $integrityCreated = $false
    try {
        $stateBytes = [Text.UTF8Encoding]::new($false).GetBytes(($State | ConvertTo-Json -Depth 100) + [Environment]::NewLine)
        $stateStream = [IO.File]::Open($resolved, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stateStream.Write($stateBytes, 0, $stateBytes.Length) } finally { $stateStream.Dispose() }
        $stateCreated = $true

        $integrity = [ordered]@{
            schemaVersion = 1
            stateFingerprint = Get-BytesFingerprint $stateBytes
            statePathFingerprint = Get-Mp3tagStatePathFingerprint $resolved
        }
        $integrityBytes = [Text.UTF8Encoding]::new($false).GetBytes(($integrity | ConvertTo-Json -Depth 10) + [Environment]::NewLine)
        $integrityStream = [IO.File]::Open($integrityPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $integrityStream.Write($integrityBytes, 0, $integrityBytes.Length) } finally { $integrityStream.Dispose() }
        $integrityCreated = $true
    }
    catch {
        if ($integrityCreated -and (Test-Path -LiteralPath $integrityPath -PathType Leaf)) { Remove-Item -LiteralPath $integrityPath -Force -ErrorAction SilentlyContinue }
        if ($stateCreated -and (Test-Path -LiteralPath $resolved -PathType Leaf)) { Remove-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue }
        throw
    }
}

function Read-TrustedMp3tagState([string]$StateFile, [object]$Case, [string]$ReleaseFingerprint, [string]$CorpusFingerprint) {
    $statePath = Resolve-Mp3tagStatePath $StateFile
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw 'The Mp3tag state file was not found.' }
    $stateBytes = [IO.File]::ReadAllBytes($statePath)
    try {
        $stateJson = [Text.UTF8Encoding]::new($false, $true).GetString($stateBytes)
        $state = $stateJson | ConvertFrom-Json -Depth 100
    }
    catch {
        throw 'The Mp3tag state file was not valid UTF-8 JSON.'
    }

    Assert-AllowedProperties $state @(
        'schemaVersion', 'caseId', 'releaseFingerprint', 'corpusFingerprint',
        'originalSourcePath', 'originalSourceHashes', 'copiedSourcePath', 'copiedSourceHashes',
        'copyBaseRoot', 'copyOwnedRoot', 'copyToken', 'runBaseRoot', 'runOwnedRoot', 'runToken',
        'preparedOutputPath', 'saveRoot', 'decodedAudioSha256', 'coverPayloadSha256', 'priorValidationStatus', 'priorAuditPresent',
        'priorFullDecodePassed', 'priorStrategy'
    ) 'Mp3tag state'
    if ([int]$state.schemaVersion -ne 2 -or [string]$state.caseId -ne [string]$Case.caseId -or
        -not [string]::Equals([string]$state.releaseFingerprint, $ReleaseFingerprint, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$state.corpusFingerprint, $CorpusFingerprint, [StringComparison]::Ordinal)) {
        throw 'The Mp3tag state file did not match the requested case, corpus, and extracted release.'
    }
    if ([string]$state.decodedAudioSha256 -notmatch '^[0-9a-f]{64}$' -or [string]$state.coverPayloadSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'The Mp3tag state file did not contain sealed prepared-media hashes.'
    }

    $expectedRunBase = Resolve-FullPath ([IO.Path]::GetTempPath())
    if (-not (Test-PathsEqual $expectedRunBase ([string]$state.runBaseRoot)) -or
        -not (Assert-OwnedDirectory $expectedRunBase ([string]$state.runOwnedRoot) ([string]$state.runToken))) {
        throw 'The Mp3tag state did not identify its expected owned run directory.'
    }
    $runOwned = Resolve-FullPath ([string]$state.runOwnedRoot)
    $integrityPath = Join-Path $runOwned $script:Mp3tagStateIntegrityName
    if (-not (Test-Path -LiteralPath $integrityPath -PathType Leaf)) { throw 'The Mp3tag state integrity record was not found.' }
    try { $integrity = Get-Content -LiteralPath $integrityPath -Raw | ConvertFrom-Json -Depth 10 } catch {
        throw 'The Mp3tag state integrity record was not valid JSON.'
    }
    Assert-AllowedProperties $integrity @('schemaVersion', 'stateFingerprint', 'statePathFingerprint') 'Mp3tag state integrity record'
    if ([int]$integrity.schemaVersion -ne 1 -or
        -not [string]::Equals([string]$integrity.stateFingerprint, (Get-BytesFingerprint $stateBytes), [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$integrity.statePathFingerprint, (Get-Mp3tagStatePathFingerprint $statePath), [StringComparison]::Ordinal)) {
        throw 'The Mp3tag state integrity check failed.'
    }

    $expectedCopyBase = Join-Path $runOwned 'source'
    if (-not (Test-PathsEqual $expectedCopyBase ([string]$state.copyBaseRoot)) -or
        -not (Assert-OwnedDirectory $expectedCopyBase ([string]$state.copyOwnedRoot) ([string]$state.copyToken))) {
        throw 'The Mp3tag state did not identify its expected owned source copy.'
    }
    $copyOwned = Resolve-FullPath ([string]$state.copyOwnedRoot)
    $original = Resolve-FullPath ([string]$state.originalSourcePath)
    if (-not (Test-PathsEqual $original (Resolve-FullPath ([string]$Case.sourcePath)))) {
        throw 'The Mp3tag state did not identify the manifest source.'
    }
    $copied = Resolve-FullPath ([string]$state.copiedSourcePath)
    if ((Test-PathsEqual $copyOwned $copied) -or -not (Test-IsWithin $copyOwned $copied) -or -not (Test-Path -LiteralPath $copied -PathType Container)) {
        throw 'The Mp3tag state contained an invalid disposable source path.'
    }
    Assert-NoReparseAncestors $original
    Assert-NoReparsePoints $original
    Assert-NoReparseAncestors $copied
    Assert-NoReparsePoints $copied

    $outputRoot = Join-Path $runOwned 'output'
    $prepared = Resolve-FullPath ([string]$state.preparedOutputPath)
    if ((Test-PathsEqual $outputRoot $prepared) -or -not (Test-IsWithin $outputRoot $prepared) -or -not (Test-Path -LiteralPath $prepared -PathType Leaf)) {
        throw 'The Mp3tag state contained an invalid prepared output path.'
    }
    $expectedSaveRoot = Join-Path $runOwned 'mp3tag-saved'
    if (-not (Test-PathsEqual $expectedSaveRoot ([string]$state.saveRoot)) -or -not (Test-Path -LiteralPath $expectedSaveRoot -PathType Container)) {
        throw 'The Mp3tag state contained an invalid save directory.'
    }
    Assert-NoReparsePoints $outputRoot
    Assert-NoReparsePoints $expectedSaveRoot

    return [pscustomobject]@{ State = $state; StatePath = $statePath }
}
function Invoke-Mp3tagPrepare([object]$Case, [object]$ReleasePaths, [string]$CorpusPath, [string]$StateFile, [string]$ReleaseFingerprint, [string]$CorpusFingerprint) {
    if ([string]$Case.category -ne 'mp3tag-roundtrip') { throw 'The requested case is not an Mp3tag case.' }
    $stateDestination = Resolve-Mp3tagStatePath $StateFile
    if (Test-Path -LiteralPath $stateDestination) { throw 'The Mp3tag state path already exists.' }
    Assert-EvidenceInputsUnchanged $CorpusPath $ReleasePaths.Root $CorpusFingerprint $ReleaseFingerprint

    $runDirectory = New-OwnedDirectory ([IO.Path]::GetTempPath())
    $copy = $null
    $stateWritten = $false
    $stage = 'copy'
    $terminalSummary = ''
    try {
        $copy = Copy-Case $Case $runDirectory
        $stage = 'conversion'
        $output = Join-Path $runDirectory.OwnedRoot 'output'
        $saveRoot = Join-Path $runDirectory.OwnedRoot 'mp3tag-saved'
        New-Item -ItemType Directory -Path $output | Out-Null
        New-Item -ItemType Directory -Path $saveRoot | Out-Null
        $localData = Join-Path $runDirectory.OwnedRoot 'local-data'

        $arguments = @(
            $copy.CopiedPath,
            '--output', $output,
            '--validation', 'full',
            '--json',
            '--metadata-profile', 'NickMp3tag',
            '--order', [string](Get-OptionalProperty $Case 'order')
        )
        foreach ($name in @('channels', 'quality')) {
            $value = Get-OptionalProperty $Case $name
            if ($null -ne $value) { $arguments += @(('--' + $name), [string]$value) }
        }

        $processResult = Invoke-JsonProcess $ReleasePaths.Cli $arguments @{
            BOOKSPLICE_LOCAL_APP_DATA = $localData
            BOOKSPLICE_FFMPEG_DIR = $ReleasePaths.ToolDirectory
        }
        $stage = 'cli-events'
        $events = Read-CliEvents $processResult.Stdout
        $final = @($events | Where-Object { $_.event -eq 'final' })[0]
        $stage = 'terminal-status'
        if ($processResult.ExitCode -ne 0 -or [int]$final.exitCode -ne 0 -or [string]$final.status -ne 'Succeeded') {
            $safeStatuses = @('InvalidInput', 'DecisionRequired', 'ExecutionFailed', 'ValidationFailed', 'PublicationFailed', 'Cancelled', 'UnexpectedFailure')
            $safeStatus = if ([string]$final.status -in $safeStatuses) { [string]$final.status } else { 'unknown' }
            $safeCodes = @()
            try {
                $failureAudit = Get-Audit $localData
                $safeCodes = @($failureAudit.diagnostics | ForEach-Object { [string]$_.code } |
                    Where-Object { $_ -match '^[A-Za-z][A-Za-z0-9.-]{0,127}$' } | Sort-Object -Unique)
            }
            catch { }
            $terminalSummary = 'process=' + [int]$processResult.ExitCode + '; final=' + [int]$final.exitCode + '; status=' + $safeStatus + '; codes=' + ($safeCodes -join ',')
            throw 'BookSplice could not prepare the Mp3tag candidate.'
        }

        $stage = 'audit'
        $audit = Get-Audit $localData
        Assert-SuccessAudit $audit
        Assert-ExpectedOrder $audit $copy $Case
        Assert-CaseDuration $audit $Case
        $outputs = @(Get-ChildItem -LiteralPath $output -Filter '*.m4b' -File)
        if ($outputs.Count -ne 1) { throw 'Mp3tag preparation did not publish exactly one M4B.' }
        if (-not (Test-SourcesUnchanged $copy)) {
            throw 'Private acceptance source hashes changed during Mp3tag preparation.'
        }
        Assert-EvidenceInputsUnchanged $CorpusPath $ReleasePaths.Root $CorpusFingerprint $ReleaseFingerprint

        $stage = 'seal-state'
        $state = [ordered]@{
            schemaVersion = 2
            caseId = [string]$Case.caseId
            releaseFingerprint = $ReleaseFingerprint
            corpusFingerprint = $CorpusFingerprint
            originalSourcePath = $copy.OriginalPath
            originalSourceHashes = $copy.OriginalHashes
            copiedSourcePath = $copy.CopiedPath
            copiedSourceHashes = $copy.CopiedHashes
            copyBaseRoot = $copy.BaseRoot
            copyOwnedRoot = $copy.OwnedRoot
            copyToken = $copy.Token
            runBaseRoot = $runDirectory.BaseRoot
            runOwnedRoot = $runDirectory.OwnedRoot
            runToken = $runDirectory.Token
            preparedOutputPath = $outputs[0].FullName
            saveRoot = $saveRoot
            decodedAudioSha256 = Get-DecodedAudioHash $outputs[0].FullName $ReleasePaths.Ffmpeg
            coverPayloadSha256 = Get-CoverPayloadHash $outputs[0].FullName $ReleasePaths.Ffmpeg
            priorValidationStatus = [string]$audit.plan.validationLevel
            priorAuditPresent = $true
            priorFullDecodePassed = Test-FullDecodePassed $audit
            priorStrategy = [string]$audit.plan.strategy
        }

        $stateParent = [IO.Path]::GetDirectoryName($stateDestination)
        New-Item -ItemType Directory -Path $stateParent -Force | Out-Null
        Assert-NoReparsePoints $stateParent
        $stage = 'write-state'
        Write-Mp3tagState $stateDestination $state $runDirectory.OwnedRoot
        $stateWritten = $true
        Write-Output ('PreparedOutputPath=' + $outputs[0].FullName)
        Write-Output ('Mp3tagSaveRoot=' + $saveRoot)
    }
    catch {
        if ($null -ne $copy) {
            [void](Test-SourcesUnchanged $copy)
            try { Remove-OwnedDirectory $copy.BaseRoot $copy.OwnedRoot $copy.Token } catch { }
        }
        try { Remove-OwnedDirectory $runDirectory.BaseRoot $runDirectory.OwnedRoot $runDirectory.Token } catch { }
        if ($stateWritten -and (Test-Path -LiteralPath $stateDestination -PathType Leaf)) {
            Remove-Item -LiteralPath $stateDestination -Force -ErrorAction SilentlyContinue
        }
        throw "Mp3tag preparation failed at stage: $stage; $terminalSummary"
    }
}
function Read-CliGateResult([string]$Path, [string]$ReleaseFingerprint, [string]$CorpusFingerprint, [object]$Manifest) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'The CLI gate result path was not supplied.' }
    $resolved = Resolve-AcceptanceResultPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw 'The CLI gate result was not found.' }
    try {
        $raw = [IO.File]::ReadAllText($resolved)
        $schemaPath = Join-Path $script:RepositoryRoot 'benchmarks\acceptance-results.schema.json'
        if (-not (Test-Json -Json $raw -SchemaFile $schemaPath -ErrorAction Stop)) { throw 'schema' }
        $result = $raw | ConvertFrom-Json -Depth 50
    }
    catch {
        throw 'The CLI gate result did not match the anonymous result schema.'
    }
    Assert-AllowedProperties $result @('schemaVersion', 'mode', 'status', 'gateComplete', 'privateGatesComplete', 'releaseFingerprint', 'corpusFingerprint', 'categories', 'cases', 'mp3tag') 'CLI gate result'
    if ([int]$result.schemaVersion -ne 1 -or [string]$result.mode -ne 'run' -or [string]$result.status -ne 'passed' -or
        $result.gateComplete -ne $true -or $result.privateGatesComplete -ne $false -or
        -not [string]::Equals([string]$result.releaseFingerprint, $ReleaseFingerprint, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$result.corpusFingerprint, $CorpusFingerprint, [StringComparison]::Ordinal)) {
        throw 'The CLI gate result did not match the current corpus and extracted release.'
    }

    $categories = @($result.categories | ForEach-Object { [string]$_ })
    if ($categories.Count -ne 3 -or @($categories | Sort-Object -Unique).Count -ne 3) {
        throw 'The CLI gate result did not contain three complete categories.'
    }
    foreach ($category in $script:CliCategories) {
        if ($category -notin $categories) { throw 'The CLI gate result did not contain three complete categories.' }
    }

    $cases = @($result.cases)
    if ($cases.Count -ne 3 -or $null -eq $Manifest) { throw 'The CLI gate result did not contain three complete cases.' }
    $seenCaseIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($category in $script:CliCategories) {
        $matches = @($cases | Where-Object { [string]$_.category -eq $category })
        $manifestMatches = @($Manifest.cases | Where-Object { [string]$_.category -eq $category })
        if ($matches.Count -ne 1 -or $manifestMatches.Count -ne 1) { throw 'The CLI gate result did not contain one case for every category.' }
        $row = $matches[0]
        if (-not $seenCaseIds.Add([string]$row.caseId) -or -not [string]::Equals([string]$row.caseId, [string]$manifestMatches[0].caseId, [StringComparison]::Ordinal)) {
            throw 'The CLI gate result case identities did not match the current manifest.'
        }
        Assert-AllowedProperties $row @('caseId', 'category', 'status', 'sourceFileCount', 'driveClass', 'validationStatus', 'auditPresent', 'fullDecodePassed', 'durationRequirementMet', 'strategy', 'sourceHashesUnchanged', 'wallSeconds', 'failureCode', 'failureStage', 'processExitCode', 'terminalExitCode', 'terminalStatus', 'diagnosticCodes', 'failedValidationCodes', 'validationWarningCount', 'validationErrorCount', 'expectedChapterCount', 'actualChapterCount', 'nonPositiveChapterCount', 'overlappingChapterCount', 'distinctChapterIdCount') 'CLI case result'
        if ([string]$row.caseId -notmatch '^case-[0-9]{2,4}$' -or [string]$row.status -ne 'passed' -or [int]$row.sourceFileCount -le 0 -or
            [string]$row.driveClass -notin @('same-volume', 'other-volume') -or [string]$row.validationStatus -ne 'full' -or
            $row.auditPresent -ne $true -or $row.fullDecodePassed -ne $true -or $row.durationRequirementMet -ne $true -or
            $row.sourceHashesUnchanged -ne $true -or [string]$row.strategy -eq 'unknown' -or
            [string]$row.strategy -notmatch '^[A-Za-z][A-Za-z0-9]+$' -or [decimal]$row.wallSeconds -le 0) {
            throw 'The CLI gate result contained incomplete case evidence.'
        }
        if ($category -eq 'another-drive' -and [string]$row.driveClass -ne 'other-volume') {
            throw 'The CLI gate result did not prove another-volume execution.'
        }
    }

    Assert-AllowedProperties $result.mp3tag @('status', 'step1Exported', 'step2ReopenedSaved', 'fieldsVerified', 'audioEssencePreserved', 'coverPreserved', 'sourceHashesUnchanged') 'CLI Mp3tag result'
    if ([string]$result.mp3tag.status -ne 'not-run' -or $result.mp3tag.step1Exported -ne $false -or
        $result.mp3tag.step2ReopenedSaved -ne $false -or $result.mp3tag.fieldsVerified -ne $false -or
        $result.mp3tag.audioEssencePreserved -ne $false -or $result.mp3tag.coverPreserved -ne $false -or
        $result.mp3tag.sourceHashesUnchanged -ne $false) {
        throw 'The CLI gate result contained invalid Mp3tag evidence.'
    }
    return $result
}
function Invoke-Mp3tagVerify([object]$Case, [object]$Manifest, [object]$ReleasePaths, [string]$CorpusPath, [string]$StateFile, [string]$SavedPath, [string]$ResultFile, [string]$CliGateFile, [bool]$Reopened, [string]$ReleaseFingerprint, [string]$CorpusFingerprint) {
    $state = $null
    $trustedStatePath = $null
    $stateTrusted = $false
    $cliGateComplete = $false
    $fieldsVerified = $false
    $audioPreserved = $false
    $coverPreserved = $false
    $sourceUnchanged = $false
    $step1Exported = $false
    $cleanupPassed = $false
    $evidencePassed = $false
    $inputsUnchanged = $false

    try {
        $trusted = Read-TrustedMp3tagState $StateFile $Case $ReleaseFingerprint $CorpusFingerprint
        $state = $trusted.State
        $trustedStatePath = $trusted.StatePath
        $stateTrusted = $true
        [void](Read-CliGateResult $CliGateFile $ReleaseFingerprint $CorpusFingerprint $Manifest)
        $cliGateComplete = $true
        Assert-EvidenceInputsUnchanged $CorpusPath $ReleasePaths.Root $CorpusFingerprint $ReleaseFingerprint

        $saved = Resolve-FullPath $SavedPath
        if (-not (Test-Path -LiteralPath $saved -PathType Leaf)) { throw 'The Mp3tag saved file was not found.' }
        Assert-NoReparseAncestors $saved
        Assert-NoReparsePoints $saved
        if (-not (Test-IsWithin ([string]$state.saveRoot) $saved) -or (Test-PathsEqual $saved ([string]$state.preparedOutputPath))) {
            throw 'The Mp3tag saved file must be a separate file inside the prepared save directory.'
        }
        $step1Exported = $true

        $originalNow = Get-Hashes ([string]$state.originalSourcePath)
        $copyNow = Get-Hashes ([string]$state.copiedSourcePath)
        $sourceUnchanged = (Test-HashMapsEqual $state.originalSourceHashes $originalNow) -and (Test-HashMapsEqual $state.copiedSourceHashes $copyNow)

        $preparedDecoded = Get-DecodedAudioHash ([string]$state.preparedOutputPath) $ReleasePaths.Ffmpeg
        $savedDecoded = Get-DecodedAudioHash $saved $ReleasePaths.Ffmpeg
        $audioPreserved = -not [string]::IsNullOrWhiteSpace([string]$state.decodedAudioSha256) -and
            [string]::Equals($preparedDecoded, [string]$state.decodedAudioSha256, [StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($savedDecoded, [string]$state.decodedAudioSha256, [StringComparison]::OrdinalIgnoreCase)
        $preparedCover = Get-CoverPayloadHash ([string]$state.preparedOutputPath) $ReleasePaths.Ffmpeg
        $savedCover = Get-CoverPayloadHash $saved $ReleasePaths.Ffmpeg
        $coverPreserved = -not [string]::IsNullOrWhiteSpace([string]$state.coverPayloadSha256) -and
            [string]::Equals($preparedCover, [string]$state.coverPayloadSha256, [StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($savedCover, [string]$state.coverPayloadSha256, [StringComparison]::OrdinalIgnoreCase)

        $probe = Invoke-JsonProcess $ReleasePaths.Ffprobe @('-v', 'error', '-show_entries', 'format_tags:stream_tags', '-of', 'json', $saved) @{}
        if ($probe.ExitCode -ne 0) { throw 'FFprobe could not inspect the Mp3tag saved file.' }
        $probeJson = $probe.Stdout | ConvertFrom-Json -Depth 50
        $tagSets = @((Get-OptionalProperty $probeJson.format 'tags')) + @($probeJson.streams | ForEach-Object { Get-OptionalProperty $_ 'tags' })
        $fieldsVerified = $true
        $expectedFields = Get-OptionalProperty $Case 'mp3tagFields'
        foreach ($property in $expectedFields.PSObject.Properties) {
            $actual = Get-TagValue $tagSets $property.Name
            if ($null -eq $actual) {
                $fieldsVerified = $false
                continue
            }
            if (-not [string]::IsNullOrEmpty([string]$property.Value) -and -not [string]::Equals($actual, [string]$property.Value, [StringComparison]::Ordinal)) {
                $fieldsVerified = $false
            }
        }

        Assert-EvidenceInputsUnchanged $CorpusPath $ReleasePaths.Root $CorpusFingerprint $ReleaseFingerprint
        $evidencePassed = $step1Exported -and $Reopened -and $fieldsVerified -and $audioPreserved -and $coverPreserved -and $sourceUnchanged -and
            [string]$state.priorValidationStatus -eq 'Full' -and $state.priorAuditPresent -eq $true -and
            $state.priorFullDecodePassed -eq $true -and -not [string]::IsNullOrWhiteSpace([string]$state.priorStrategy)
    }
    catch {
        $evidencePassed = $false
    }
    finally {
        if ($stateTrusted -and $null -ne $state) {
            try {
                $originalNow = Get-Hashes ([string]$state.originalSourcePath)
                $copyNow = Get-Hashes ([string]$state.copiedSourcePath)
                $sourceUnchanged = (Test-HashMapsEqual $state.originalSourceHashes $originalNow) -and (Test-HashMapsEqual $state.copiedSourceHashes $copyNow)
                Remove-OwnedDirectory ([string]$state.copyBaseRoot) ([string]$state.copyOwnedRoot) ([string]$state.copyToken)
                Remove-OwnedDirectory ([string]$state.runBaseRoot) ([string]$state.runOwnedRoot) ([string]$state.runToken)
                $cleanupPassed = $true
            }
            catch {
                $cleanupPassed = $false
            }
        }
        if ($cleanupPassed -and $null -ne $trustedStatePath -and (Test-Path -LiteralPath $trustedStatePath -PathType Leaf)) {
            try { Remove-Item -LiteralPath $trustedStatePath -Force } catch { $cleanupPassed = $false }
        }
    }

    try {
        Assert-EvidenceInputsUnchanged $CorpusPath $ReleasePaths.Root $CorpusFingerprint $ReleaseFingerprint
        $inputsUnchanged = $true
    }
    catch {
        $inputsUnchanged = $false
    }

    $passed = $evidencePassed -and $cleanupPassed -and $sourceUnchanged -and $inputsUnchanged -and $cliGateComplete
    $result = [ordered]@{
        schemaVersion = 1
        releaseFingerprint = $ReleaseFingerprint
        corpusFingerprint = $CorpusFingerprint
        mode = 'verify-mp3tag'
        status = if ($passed) { 'passed' } else { 'failed' }
        gateComplete = $passed
        privateGatesComplete = $passed
        categories = @('mp3tag-roundtrip')
        cases = @()
        mp3tag = [ordered]@{
            status = if ($passed) { 'passed' } else { 'failed' }
            step1Exported = $step1Exported
            step2ReopenedSaved = $Reopened
            fieldsVerified = $fieldsVerified
            audioEssencePreserved = $audioPreserved
            coverPreserved = $coverPreserved
            sourceHashesUnchanged = $sourceUnchanged
        }
    }
    Write-AnonymousResult $ResultFile $result
    if (-not $passed) { throw 'Mp3tag verification failed.' }
}
$manifestSnapshot = Read-PrivateManifestSnapshot $Corpus
$manifest = $manifestSnapshot.Manifest
$releasePaths = Get-ReleasePaths $Release
Assert-ManifestPathIsolation $manifest $releasePaths.Root
$corpusFingerprint = $manifestSnapshot.Fingerprint
$releaseFingerprint = Get-ReleaseFingerprint $releasePaths.Root
Assert-EvidenceInputsUnchanged $manifestSnapshot.Path $releasePaths.Root $corpusFingerprint $releaseFingerprint

if ($Mode -eq 'run') {
    $resultDestination = Resolve-AcceptanceResultPath $ResultPath
    $runCases = if ([string]::IsNullOrWhiteSpace($CaseId)) {
        @($manifest.cases | Where-Object { [string]$_.category -in $script:CliCategories })
    } else {
        @((Get-Case $manifest $CaseId))
    }

    if (@($runCases | Where-Object { [string]$_.category -eq 'mp3tag-roundtrip' }).Count -gt 0) {
        throw 'Mp3tag cases must use the prepare and verify modes.'
    }

    if ([string]::IsNullOrWhiteSpace($CaseId)) {
        foreach ($category in $script:CliCategories) {
            if (@($runCases | Where-Object { [string]$_.category -eq $category }).Count -ne 1) {
                throw 'A full private acceptance run requires exactly one case for each CLI release gate.'
            }
        }
    }

    $rows = @()
    foreach ($case in $runCases) {
        $failureStage = 'fingerprint'
        try {
            Assert-EvidenceInputsUnchanged $manifestSnapshot.Path $releasePaths.Root $corpusFingerprint $releaseFingerprint
            $failureStage = 'unknown'
            $row = Invoke-Case $case $releasePaths
            $failureStage = 'fingerprint'
            Assert-EvidenceInputsUnchanged $manifestSnapshot.Path $releasePaths.Root $corpusFingerprint $releaseFingerprint
            $rows += $row
        }
        catch {
            Write-Verbose ('Private acceptance case failed: ' + [string]$case.caseId)
            $rows += New-FailedCaseResult $case $_ $failureStage
        }
    }

    Assert-EvidenceInputsUnchanged $manifestSnapshot.Path $releasePaths.Root $corpusFingerprint $releaseFingerprint
    $failed = @($rows | Where-Object { $_.status -eq 'failed' }).Count -gt 0
    $fullScope = [string]::IsNullOrWhiteSpace($CaseId)
    $gateComplete = $fullScope -and -not $failed
    $result = [ordered]@{
        schemaVersion = 1
        releaseFingerprint = $releaseFingerprint
        corpusFingerprint = $corpusFingerprint
        mode = 'run'
        status = if ($failed) { 'failed' } else { 'passed' }
        gateComplete = $gateComplete
        privateGatesComplete = $false
        categories = @($rows | ForEach-Object { $_.category } | Sort-Object -Unique)
        cases = $rows
        mp3tag = New-NotRunMp3tagResult
    }
    Write-AnonymousResult $resultDestination $result
    if ($failed) { throw 'Private acceptance failed.' }
    return
}

$case = Get-Case $manifest $CaseId
if ([string]$case.category -ne 'mp3tag-roundtrip') { throw 'This mode requires an Mp3tag case.' }
if ([string]::IsNullOrWhiteSpace($StatePath)) { throw 'StatePath is required for Mp3tag modes.' }
[void](Resolve-Mp3tagStatePath $StatePath)

if ($Mode -eq 'prepare-mp3tag') {
    Invoke-Mp3tagPrepare $case $releasePaths $manifestSnapshot.Path $StatePath $releaseFingerprint $corpusFingerprint
    return
}

if ([string]::IsNullOrWhiteSpace($Mp3tagOutputPath)) { throw 'Mp3tagOutputPath is required for verify-mp3tag mode.' }
$resultDestination = Resolve-AcceptanceResultPath $ResultPath
$cliGateDestination = Resolve-AcceptanceResultPath $CliResultPath
if ((Test-PathsEqual $resultDestination $cliGateDestination) -or (Test-PathsEqual $resultDestination (Resolve-Mp3tagStatePath $StatePath))) {
    throw 'Mp3tag result, state, and CLI gate files must use separate paths.'
}
Invoke-Mp3tagVerify $case $manifest $releasePaths $manifestSnapshot.Path $StatePath $Mp3tagOutputPath $resultDestination $cliGateDestination ([bool]$ConfirmedReopened) $releaseFingerprint $corpusFingerprint
