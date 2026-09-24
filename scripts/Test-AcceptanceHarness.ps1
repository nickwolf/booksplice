[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Invoke-Acceptance.ps1'
$sourceLines = Get-Content -LiteralPath $scriptPath
$mainLine = ($sourceLines | Select-String -SimpleMatch '$manifestSnapshot = Read-PrivateManifestSnapshot $Corpus' | Select-Object -First 1).LineNumber
if ($null -eq $mainLine) { throw 'Acceptance harness entry point was not found.' }
$functionsPath = Join-Path $PSScriptRoot ('.Invoke-Acceptance.functions.' + [guid]::NewGuid().ToString('N') + '.ps1')
[IO.File]::WriteAllLines($functionsPath, $sourceLines[0..($mainLine - 2)], [Text.UTF8Encoding]::new($false))

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$ownedTestRoot = $null
$testRoot = $null
$artifactRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\acceptance'
$createdArtifacts = [Collections.Generic.List[string]]::new()
$failures = [Collections.Generic.List[string]]::new()
$passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    $threw = $false
    try { & $Action | Out-Null }
    catch {
        $threw = $true
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "$Message Unexpected error: $($_.Exception.Message)"
        }
    }
    if (-not $threw) { throw $Message }
}

function Invoke-Check([string]$Name, [scriptblock]$Action) {
    try {
        & $Action
        $script:passed++
    }
    catch {
        $script:failures.Add("$Name`: $($_.Exception.Message)")
    }
}

try {
    . $functionsPath -Mode run -Corpus 'unused' -Release 'unused'
    $ownedTestRoot = New-OwnedDirectory $tempBase
    $testRoot = $ownedTestRoot.OwnedRoot
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

    Invoke-Check 'result path is restricted' {
        $outside = Join-Path $testRoot 'outside-result.json'
        Assert-Throws { Write-AnonymousResult $outside ([ordered]@{ status='passed' }) } 'artifacts.*acceptance|result path' 'An outside result path was accepted.'
        Assert-True (-not (Test-Path -LiteralPath $outside)) 'The rejected outside result path was written.'
    }

    Invoke-Check 'allowed result remains anonymous' {
        $allowed = Join-Path $artifactRoot ('private-generated-' + [guid]::NewGuid().ToString('N') + '-result.json')
        $createdArtifacts.Add($allowed)
        Write-AnonymousResult $allowed ([ordered]@{ value='safe' })
        Assert-True (Test-Path -LiteralPath $allowed -PathType Leaf) 'An allowed result was not written.'
    }

    Invoke-Check 'source reparse ancestors are rejected' {
        $realRoot = Join-Path $testRoot 'real'
        $book = Join-Path $realRoot 'book'
        $alias = Join-Path $testRoot 'alias'
        try {
            New-Item -ItemType Directory -Path $book -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $book '01.mp3'), 'source')
            New-Item -ItemType Junction -Path $alias -Target $realRoot | Out-Null
            Assert-Throws { Get-SourceFiles (Join-Path $alias 'book') } 'reparse.*ancestor|reparse point' 'A source below a junction ancestor was accepted.'
        }
        finally {
            if (Test-Path -LiteralPath $alias) { Remove-Item -LiteralPath $alias -Force }
        }
    }

    Invoke-Check 'cleanup requires exact GUID ownership name' {
        $base = Join-Path $testRoot 'cleanup-base'
        $owned = Join-Path $base 'booksplice-acceptance-not-a-guid'
        New-Item -ItemType Directory -Path $owned -Force | Out-Null
        $token = [guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllText((Join-Path $owned '.booksplice-acceptance-owned'), $token)
        Assert-Throws { Remove-OwnedDirectory $base $owned $token } 'unowned directory name|GUID' 'Cleanup accepted a non-GUID owned directory name.'
        Assert-True (Test-Path -LiteralPath $owned -PathType Container) 'Rejected cleanup removed the directory.'
    }

    Invoke-Check 'CLI evidence requires three complete cases' {
        $releaseFingerprint = 'r' * 43
        $corpusFingerprint = 'c' * 43
        $path = Join-Path $artifactRoot ('private-empty-' + [guid]::NewGuid().ToString('N') + '-result.json')
        $createdArtifacts.Add($path)
        $manifest = [pscustomobject]@{ cases=@(
            [pscustomobject]@{ caseId='case-01'; category='long-audiobook' },
            [pscustomobject]@{ caseId='case-02'; category='very-long-audiobook' },
            [pscustomobject]@{ caseId='case-03'; category='another-drive' }
        ) }
        $empty = [ordered]@{
            schemaVersion=1; mode='run'; status='passed'; gateComplete=$true; privateGatesComplete=$false
            releaseFingerprint=$releaseFingerprint; corpusFingerprint=$corpusFingerprint
            categories=@(); cases=@(); mp3tag=(New-NotRunMp3tagResult)
        }
        [IO.File]::WriteAllText($path, ($empty | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
        Assert-Throws { Read-CliGateResult $path $releaseFingerprint $corpusFingerprint $manifest } 'complete|three|required|match' 'Empty CLI evidence was accepted.'
        $schemaPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'benchmarks\acceptance-results.schema.json'
        $schemaRejected = $false
        try { $schemaRejected = -not (Test-Json -Json ([IO.File]::ReadAllText($path)) -SchemaFile $schemaPath) } catch { $schemaRejected = $true }
        Assert-True $schemaRejected 'The result schema accepted an empty completed CLI gate.'
    }

    Invoke-Check 'manifest snapshot binds parsing and fingerprint to the same bytes' {
        $sources = @()
        foreach ($index in 1..4) {
            $source = Join-Path $testRoot ("snapshot-source-$index")
            New-Item -ItemType Directory -Path $source | Out-Null
            [IO.File]::WriteAllText((Join-Path $source '01.mp3'), 'generated')
            $sources += $source
        }
        $manifest = [ordered]@{
            schemaVersion=1; privateManifest=$true; copyPermission=$true; cases=@(
                [ordered]@{ caseId='case-01'; category='long-audiobook'; sourcePath=$sources[0]; copyPermission=$true; expectedOrder=@('01.mp3'); minimumAudioDurationSeconds=3600; order='natural' },
                [ordered]@{ caseId='case-02'; category='very-long-audiobook'; sourcePath=$sources[1]; copyPermission=$true; expectedOrder=@('01.mp3'); minimumAudioDurationSeconds=21600; order='natural' },
                [ordered]@{ caseId='case-03'; category='another-drive'; sourcePath=$sources[2]; copyPermission=$true; copyRoot=(Join-Path $testRoot 'snapshot-copy-root'); sourceDriveClass='other-volume'; expectedOrder=@('01.mp3'); order='natural' },
                [ordered]@{ caseId='case-04'; category='mp3tag-roundtrip'; sourcePath=$sources[3]; copyPermission=$true; expectedOrder=@('01.mp3'); order='natural'; mp3tagFields=[ordered]@{ TITLE='Title'; ALBUM='Album'; ARTIST='Artist'; ALBUMARTIST='Album Artist' } }
            )
        }
        $path = Join-Path $artifactRoot ('private-snapshot-' + [guid]::NewGuid().ToString('N') + '.json')
        $createdArtifacts.Add($path)
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 30))
        [IO.File]::WriteAllBytes($path, $bytes)
        $snapshot = Read-PrivateManifestSnapshot $path
        Assert-True ([string]::Equals($snapshot.Fingerprint, (Get-CorpusFingerprint $path), [StringComparison]::Ordinal)) 'Snapshot fingerprint did not bind the parsed manifest and source inventory.'
        Assert-True ([string]$snapshot.Manifest.cases[0].caseId -eq 'case-01') 'Snapshot parsing did not use the expected byte buffer.'
        [IO.File]::WriteAllText((Join-Path $sources[1] '01.mp3'), 'changed generated source')
        Assert-True (-not [string]::Equals($snapshot.Fingerprint, (Get-CorpusFingerprint $path), [StringComparison]::Ordinal)) 'A source mutation did not change the corpus fingerprint.'
        [IO.File]::AppendAllText($path, ' ')
        Assert-True (-not [string]::Equals($snapshot.Fingerprint, (Get-CorpusFingerprint $path), [StringComparison]::Ordinal)) 'A later manifest mutation altered the captured snapshot.'
        Assert-True ([string]$snapshot.Manifest.cases[0].caseId -eq 'case-01') 'A later manifest mutation altered the parsed snapshot.'
    }

    Invoke-Check 'fingerprints are deterministic and sensitive' {
        $corpus = Join-Path $testRoot 'fingerprint-corpus.json'
        [IO.File]::WriteAllText($corpus, '{"value":1}', [Text.UTF8Encoding]::new($false))
        $corpusFirst = Get-BytesFingerprint ([IO.File]::ReadAllBytes($corpus))
        $corpusSecond = Get-BytesFingerprint ([IO.File]::ReadAllBytes($corpus))
        Assert-True ([string]::Equals($corpusFirst, $corpusSecond, [StringComparison]::Ordinal)) 'The corpus fingerprint was not deterministic.'
        [IO.File]::AppendAllText($corpus, ' ')
        Assert-True (-not [string]::Equals($corpusFirst, (Get-BytesFingerprint ([IO.File]::ReadAllBytes($corpus))), [StringComparison]::Ordinal)) 'A manifest mutation did not change its byte fingerprint.'

        $releaseRoot = Join-Path $testRoot 'fingerprint-release'
        New-Item -ItemType Directory -Path $releaseRoot | Out-Null
        $releaseFile = Join-Path $releaseRoot 'booksplice.exe'
        [IO.File]::WriteAllText($releaseFile, 'one')
        $releaseFirst = Get-ReleaseFingerprint $releaseRoot
        $releaseSecond = Get-ReleaseFingerprint $releaseRoot
        Assert-True ([string]::Equals($releaseFirst, $releaseSecond, [StringComparison]::Ordinal)) 'The release fingerprint was not deterministic.'
        [IO.File]::WriteAllText($releaseFile, 'two')
        Assert-True (-not [string]::Equals($releaseFirst, (Get-ReleaseFingerprint $releaseRoot), [StringComparison]::Ordinal)) 'A release mutation did not change its fingerprint.'
        [IO.File]::SetAttributes($releaseFile, [IO.FileAttributes]::Hidden)
        $hiddenFirst = Get-ReleaseFingerprint $releaseRoot
        [IO.File]::SetAttributes($releaseFile, [IO.FileAttributes]::Normal)
        [IO.File]::WriteAllText($releaseFile, 'three')
        [IO.File]::SetAttributes($releaseFile, [IO.FileAttributes]::Hidden)
        Assert-True (-not [string]::Equals($hiddenFirst, (Get-ReleaseFingerprint $releaseRoot), [StringComparison]::Ordinal)) 'A hidden release executable mutation did not change its fingerprint.'
    }

    Invoke-Check 'manifest order and duration requirements are mandatory' {
        $sources = @()
        foreach ($index in 1..4) {
            $source = Join-Path $testRoot ("manifest-source-$index")
            New-Item -ItemType Directory -Path $source | Out-Null
            [IO.File]::WriteAllText((Join-Path $source '01.mp3'), 'generated')
            $sources += $source
        }
        $manifest = [ordered]@{
            schemaVersion = 1
            privateManifest = $true
            copyPermission = $true
            cases = @(
                [ordered]@{ caseId='case-01'; category='long-audiobook'; sourcePath=$sources[0]; copyPermission=$true; expectedOrder=@('01.mp3'); minimumAudioDurationSeconds=3600; order='natural' },
                [ordered]@{ caseId='case-02'; category='very-long-audiobook'; sourcePath=$sources[1]; copyPermission=$true; expectedOrder=@('01.mp3'); minimumAudioDurationSeconds=21600; order='natural' },
                [ordered]@{ caseId='case-03'; category='another-drive'; sourcePath=$sources[2]; copyPermission=$true; copyRoot=(Join-Path $testRoot 'other-volume'); sourceDriveClass='other-volume'; expectedOrder=@('01.mp3'); order='natural' },
                [ordered]@{ caseId='case-04'; category='mp3tag-roundtrip'; sourcePath=$sources[3]; copyPermission=$true; expectedOrder=@('01.mp3'); order='natural'; mp3tagFields=[ordered]@{ TITLE='Title'; ALBUM='Album'; ARTIST='Artist'; ALBUMARTIST='Album Artist' } }
            )
        }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 30))
        $validated = ConvertFrom-PrivateManifestBytes $bytes
        Assert-True (@($validated.cases).Count -eq 4) 'A valid threshold manifest did not pass.'

        $missingOrder = ($manifest | ConvertTo-Json -Depth 30) | ConvertFrom-Json -Depth 30
        [void]$missingOrder.cases[0].PSObject.Properties.Remove('expectedOrder')
        $missingBytes = [Text.UTF8Encoding]::new($false).GetBytes(($missingOrder | ConvertTo-Json -Depth 30))
        Assert-Throws { ConvertFrom-PrivateManifestBytes $missingBytes } 'expected.*order|requires' 'A manifest without expectedOrder was accepted.'

        $short = ($manifest | ConvertTo-Json -Depth 30) | ConvertFrom-Json -Depth 30
        $short.cases[0].minimumAudioDurationSeconds = 3599
        $shortBytes = [Text.UTF8Encoding]::new($false).GetBytes(($short | ConvertTo-Json -Depth 30))
        Assert-Throws { ConvertFrom-PrivateManifestBytes $shortBytes } 'duration.*floor|below' 'A short long-audiobook declaration was accepted.'
    }

    Invoke-Check 'root-relative order distinguishes duplicate leaf names' {
        $copiedRoot = Join-Path $testRoot 'duplicate-leaves'
        $first = Join-Path $copiedRoot 'disc-1\01.mp3'
        $second = Join-Path $copiedRoot 'disc-2\01.mp3'
        New-Item -ItemType Directory -Path (Split-Path $first -Parent),(Split-Path $second -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($first, 'one')
        [IO.File]::WriteAllText($second, 'two')
        $copy = [pscustomobject]@{ CopiedPath=$copiedRoot }
        $case = [pscustomobject]@{ expectedOrder=@('disc-1/01.mp3','disc-2/01.mp3') }
        $wrongAudit = [pscustomobject]@{ orderedSources=@($second,$first) }
        Assert-Throws { Assert-ExpectedOrder $wrongAudit $copy $case } 'order did not match' 'Duplicate leaf names hid a reversed relative-path order.'
        $rightAudit = [pscustomobject]@{ orderedSources=@($first,$second) }
        Assert-ExpectedOrder $rightAudit $copy $case
    }

    Invoke-Check 'measured duration floors reject short audio and accept thresholds' {
        $case = [pscustomobject]@{ category='long-audiobook'; minimumAudioDurationSeconds=3600 }
        $audit = [pscustomobject]@{ orderedSources=@('one'); analysis=[pscustomobject]@{ sourceSummaries=@([pscustomobject]@{ duration=3599 }) } }
        Assert-Throws { Assert-CaseDuration $audit $case } 'duration requirement' 'Measured short audio passed the long-audiobook floor.'
        $audit.analysis.sourceSummaries[0].duration = 3600
        Assert-CaseDuration $audit $case
        $case.category = 'very-long-audiobook'
        $case.minimumAudioDurationSeconds = 21600
        $audit.analysis.sourceSummaries[0].duration = 21600
        Assert-CaseDuration $audit $case
    }

    Invoke-Check 'CLI evidence rejects stale corpus and release fingerprints' {
        $releaseFingerprint = 'r' * 43
        $corpusFingerprint = 'c' * 43
        $path = Join-Path $artifactRoot ('private-valid-' + [guid]::NewGuid().ToString('N') + '-result.json')
        $createdArtifacts.Add($path)
        $rows = @()
        foreach ($category in @('long-audiobook','very-long-audiobook','another-drive')) {
            $rows += [ordered]@{
                caseId = 'case-' + ('{0:D2}' -f ($rows.Count + 1)); category=$category; status='passed'; sourceFileCount=1
                driveClass = if ($category -eq 'another-drive') { 'other-volume' } else { 'same-volume' }
                validationStatus='full'; auditPresent=$true; fullDecodePassed=$true; durationRequirementMet=$true
                strategy='Reencode'; sourceHashesUnchanged=$true; wallSeconds=1
            }
        }
        $result = [ordered]@{
            schemaVersion=1; mode='run'; status='passed'; gateComplete=$true; privateGatesComplete=$false
            releaseFingerprint=$releaseFingerprint; corpusFingerprint=$corpusFingerprint
            categories=@('long-audiobook','very-long-audiobook','another-drive'); cases=$rows; mp3tag=(New-NotRunMp3tagResult)
        }
        [IO.File]::WriteAllText($path, ($result | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
        $schemaPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'benchmarks\acceptance-results.schema.json'
        Assert-True (Test-Json -Json ([IO.File]::ReadAllText($path)) -SchemaFile $schemaPath) 'The result schema rejected valid completed CLI evidence.'
        $manifest = [pscustomobject]@{ cases=@($rows | ForEach-Object { [pscustomobject]@{ caseId=$_.caseId; category=$_.category } }) }
        [void](Read-CliGateResult $path $releaseFingerprint $corpusFingerprint $manifest)
        Assert-Throws { Read-CliGateResult $path ('x' * 43) $corpusFingerprint $manifest } 'did not match' 'Stale release evidence was accepted.'
        Assert-Throws { Read-CliGateResult $path $releaseFingerprint ('y' * 43) $manifest } 'did not match' 'Stale corpus evidence was accepted.'
        $rows[1].caseId = $rows[0].caseId
        [IO.File]::WriteAllText($path, ($result | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
        Assert-Throws { Read-CliGateResult $path $releaseFingerprint $corpusFingerprint $manifest } 'identities|manifest' 'Duplicate or mismatched CLI case identities were accepted.'
        $rows[1].caseId = 'case-02'
        $rows[0].driveClass = 'nonsense'
        [IO.File]::WriteAllText($path, ($result | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
        Assert-Throws { Read-CliGateResult $path $releaseFingerprint $corpusFingerprint $manifest } 'incomplete|schema' 'An invalid CLI drive class was accepted.'
        $rows[0].driveClass = 'same-volume'
        $result.status = 'failed'
        [IO.File]::WriteAllText($path, ($result | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
        $invalidStatusRejected = $false
        try { $invalidStatusRejected = -not (Test-Json -Json ([IO.File]::ReadAllText($path)) -SchemaFile $schemaPath) } catch { $invalidStatusRejected = $true }
        Assert-True $invalidStatusRejected 'The schema accepted status=failed with gateComplete=true.'
    }

    Invoke-Check 'Mp3tag state path and integrity protect cleanup' {
        Assert-Throws { Resolve-Mp3tagStatePath (Join-Path $testRoot 'mp3tag-outside.json') } 'artifacts.*acceptance|state' 'An Mp3tag state path outside artifacts was accepted.'
        $runDirectory = $null
        $copyDirectory = $null
        try {
            $original = Join-Path $testRoot 'state-original'
            New-Item -ItemType Directory -Path $original | Out-Null
            [IO.File]::WriteAllText((Join-Path $original '01.mp3'), 'source')
            $runDirectory = New-OwnedDirectory ([IO.Path]::GetTempPath())
            $copyDirectory = New-OwnedDirectory (Join-Path $runDirectory.OwnedRoot 'source')
            $copied = Join-Path $copyDirectory.OwnedRoot 'book'
            New-Item -ItemType Directory -Path $copied | Out-Null
            [IO.File]::WriteAllText((Join-Path $copied '01.mp3'), 'source')
            $output = Join-Path $runDirectory.OwnedRoot 'output'
            $saveRoot = Join-Path $runDirectory.OwnedRoot 'mp3tag-saved'
            New-Item -ItemType Directory -Path $output,$saveRoot | Out-Null
            $prepared = Join-Path $output 'prepared.m4b'
            [IO.File]::WriteAllText($prepared, 'prepared')

            $corpus = Join-Path $testRoot 'state-corpus.json'
            [IO.File]::WriteAllText($corpus, '{}')
            $releaseRoot = Join-Path $testRoot 'state-release'
            New-Item -ItemType Directory -Path $releaseRoot | Out-Null
            $dummyTool = Join-Path $releaseRoot 'tool.exe'
            [IO.File]::WriteAllText($dummyTool, 'tool')
            $releaseFingerprint = Get-ReleaseFingerprint $releaseRoot
            $corpusFingerprint = Get-BytesFingerprint ([IO.File]::ReadAllBytes($corpus))
            $statePath = Join-Path $artifactRoot ('mp3tag-generated-' + [guid]::NewGuid().ToString('N') + '.json')
            $resultPath = Join-Path $artifactRoot ('mp3tag-generated-' + [guid]::NewGuid().ToString('N') + '-result.json')
            $cliPath = Join-Path $artifactRoot ('private-generated-' + [guid]::NewGuid().ToString('N') + '-result.json')
            $createdArtifacts.Add($statePath)
            $createdArtifacts.Add($resultPath)
            $state = [ordered]@{
                schemaVersion=2; caseId='case-04'; releaseFingerprint=$releaseFingerprint; corpusFingerprint=$corpusFingerprint
                originalSourcePath=$original; originalSourceHashes=(Get-Hashes $original)
                copiedSourcePath=$copied; copiedSourceHashes=(Get-Hashes $copied)
                copyBaseRoot=$copyDirectory.BaseRoot; copyOwnedRoot=$copyDirectory.OwnedRoot; copyToken=$copyDirectory.Token
                runBaseRoot=$runDirectory.BaseRoot; runOwnedRoot=$runDirectory.OwnedRoot; runToken=$runDirectory.Token
                preparedOutputPath=$prepared; saveRoot=$saveRoot; decodedAudioSha256=('a' * 64); coverPayloadSha256=('b' * 64)
                priorValidationStatus='Full'; priorAuditPresent=$true
                priorFullDecodePassed=$true; priorStrategy='Reencode'
            }
            Write-Mp3tagState $statePath $state $runDirectory.OwnedRoot
            $case = [pscustomobject]@{ caseId='case-04'; sourcePath=$original; mp3tagFields=[pscustomobject]@{ TITLE='Title' } }
            [void](Read-TrustedMp3tagState $statePath $case $releaseFingerprint $corpusFingerprint)
            [IO.File]::AppendAllText($statePath, ' ')
            Assert-Throws { Read-TrustedMp3tagState $statePath $case $releaseFingerprint $corpusFingerprint } 'integrity' 'Tampered Mp3tag state was trusted.'
            $releasePaths = [pscustomobject]@{ Root=$releaseRoot; Ffmpeg=$dummyTool; Ffprobe=$dummyTool }
            Assert-Throws { Invoke-Mp3tagVerify $case ([pscustomobject]@{ cases=@() }) $releasePaths $corpus $statePath 'unused' $resultPath $cliPath $true $releaseFingerprint $corpusFingerprint } 'verification failed' 'Tampered state unexpectedly verified.'
            Assert-True (Test-Path -LiteralPath $runDirectory.OwnedRoot -PathType Container) 'Tampered state triggered run-tree cleanup.'
            Assert-True (Test-Path -LiteralPath $copyDirectory.OwnedRoot -PathType Container) 'Tampered state triggered copy-tree cleanup.'
            Assert-True (Test-Path -LiteralPath $statePath -PathType Leaf) 'Tampered state was not preserved.'
        }
        finally {
            if ($null -ne $copyDirectory) { try { Remove-OwnedDirectory $copyDirectory.BaseRoot $copyDirectory.OwnedRoot $copyDirectory.Token } catch { } }
            if ($null -ne $runDirectory) { try { Remove-OwnedDirectory $runDirectory.BaseRoot $runDirectory.OwnedRoot $runDirectory.Token } catch { } }
        }
    }

    Invoke-Check 'copy roots cannot overlap any manifest source' {
        $sourceOne = Join-Path $testRoot 'isolation-source-one'
        $sourceTwo = Join-Path $testRoot 'isolation-source-two'
        $copyRoot = Join-Path $sourceTwo 'scratch'
        $releaseRoot = Join-Path $testRoot 'isolation-release'
        New-Item -ItemType Directory -Path $sourceOne,$sourceTwo,$copyRoot,$releaseRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $sourceOne '01.mp3'), 'one')
        [IO.File]::WriteAllText((Join-Path $sourceTwo '01.mp3'), 'two')
        [IO.File]::WriteAllText((Join-Path $releaseRoot 'booksplice.exe'), 'release')
        $manifest = [pscustomobject]@{ cases=@(
            [pscustomobject]@{ sourcePath=$sourceOne; copyRoot=$copyRoot },
            [pscustomobject]@{ sourcePath=$sourceTwo }
        ) }
        Assert-Throws { Assert-ManifestPathIsolation $manifest $releaseRoot } 'copy root overlaps' 'A copy root inside another manifest source was accepted.'
        Assert-True (@(Get-ChildItem -LiteralPath $copyRoot -Force).Count -eq 0) 'Rejected cross-case isolation created content in the copy root.'
    }
    Invoke-Check 'CLI case failures preserve only an anonymous stage' {
        $originalProcess = (Get-Command Invoke-JsonProcess -CommandType Function).ScriptBlock
        $originalAudit = (Get-Command Get-Audit -CommandType Function).ScriptBlock
        try {
            $source = Join-Path $testRoot 'staged-failure-source'
            New-Item -ItemType Directory -Path $source | Out-Null
            [IO.File]::WriteAllText((Join-Path $source '01.mp3'), 'source')
            Set-Item Function:Invoke-JsonProcess {
                [pscustomobject]@{
                    ExitCode = 5
                    Stdout = '{"schemaVersion":1,"event":"final","exitCode":5,"status":"ExecutionFailed"}'
                    Stderr = 'private diagnostic text'
                }
            }
            Set-Item Function:Get-Audit {
                [pscustomobject]@{
                    orderedSources = @('source-1', 'source-2')
                    diagnostics = @([pscustomobject]@{ code='validation.failed'; severity='Error'; message='private audit text' })
                    validation = [pscustomobject]@{
                        checks = @(
                            [pscustomobject]@{ code='audio.codec'; passed=$true; required=$true },
                            [pscustomobject]@{ code='duration.expected'; passed=$false; required=$true }
                        )
                        warnings = @('private validation warning')
                        errors = @('private validation error')
                        outputFacts = [pscustomobject]@{
                            chapters = @(
                                [pscustomobject]@{ id=0; startTime=0; endTime=2 },
                                [pscustomobject]@{ id=1; startTime=1; endTime=3 }
                            )
                        }
                    }
                }
            }
            $case = [pscustomobject]@{ caseId='case-01'; category='long-audiobook'; sourcePath=$source; order='natural' }
            $releasePaths = [pscustomobject]@{ Cli='booksplice'; ToolDirectory='tools' }

            $failure = $null
            try { Invoke-Case $case $releasePaths | Out-Null } catch { $failure = $_ }
            Assert-True ($null -ne $failure) 'The generated failing CLI control unexpectedly passed.'
            $row = New-FailedCaseResult $case $failure
            Assert-True ([string]$row.failureStage -eq 'terminal-status') 'The failing CLI status stage was not preserved.'
            Assert-True ([string]$row.failureCode -eq 'case-failed') 'The anonymous failure code changed.'
            Assert-True ([int]$row.processExitCode -eq 5 -and [int]$row.terminalExitCode -eq 5) 'The bounded exit codes were not preserved.'
            Assert-True ([string]$row.terminalStatus -eq 'ExecutionFailed') 'The bounded terminal status was not preserved.'
            Assert-True (@($row.diagnosticCodes).Count -eq 1 -and [string]$row.diagnosticCodes[0] -eq 'validation.failed') 'The generic audit diagnostic code was not preserved.'
            Assert-True (@($row.failedValidationCodes).Count -eq 1 -and [string]$row.failedValidationCodes[0] -eq 'duration.expected') 'The failed validation check code was not preserved.'
            Assert-True ([int]$row.validationWarningCount -eq 1 -and [int]$row.validationErrorCount -eq 1) 'The bounded validation message counts were not preserved.'
            Assert-True ([int]$row.expectedChapterCount -eq 2 -and [int]$row.actualChapterCount -eq 2) 'The anonymous chapter counts were not preserved.'
            Assert-True ([int]$row.nonPositiveChapterCount -eq 0 -and [int]$row.overlappingChapterCount -eq 1) 'The anonymous chapter-shape counts were not preserved.'
            Assert-True ([int]$row.distinctChapterIdCount -eq 2) 'The anonymous distinct chapter ID count was not preserved.'
            Assert-True (($row | ConvertTo-Json -Compress) -notmatch 'private diagnostic|private audit|staged-failure-source') 'The failed row exposed diagnostic text or a source name.'
        }
        finally {
            Set-Item Function:Invoke-JsonProcess $originalProcess
            Set-Item Function:Get-Audit $originalAudit
        }
    }
    Invoke-Check 'source hashes detect mutations and owned trees clean up' {
        $runDirectory = $null
        $copy = $null
        try {
            $original = Join-Path $testRoot 'hash-original'
            New-Item -ItemType Directory -Path $original | Out-Null
            [IO.File]::WriteAllText((Join-Path $original '01.mp3'), 'source')
            $originalBaseline = Get-Hashes $original
            $runDirectory = New-OwnedDirectory ([IO.Path]::GetTempPath())
            $overlapCase = [pscustomobject]@{ sourcePath=$original; copyRoot=(Join-Path $original 'scratch') }
            Assert-Throws { Copy-Case $overlapCase $runDirectory } 'overlaps' 'A disposable copy root inside the original was accepted.'
            Assert-True (Test-HashMapsEqual $originalBaseline (Get-Hashes $original)) 'A rejected overlap changed the generated original.'
            $case = [pscustomobject]@{ sourcePath=$original }
            $copy = Copy-Case $case $runDirectory
            Assert-True (Test-SourcesUnchanged $copy) 'Unchanged generated sources failed the preservation check.'
            [IO.File]::AppendAllText((Join-Path $copy.CopiedPath '01.mp3'), 'changed')
            Assert-True (-not (Test-SourcesUnchanged $copy)) 'A disposable-source mutation was not detected.'
            Assert-True (Test-HashMapsEqual $originalBaseline (Get-Hashes $original)) 'A disposable-copy failure changed the generated original.'
        }
        finally {
            if ($null -ne $copy) { try { Remove-OwnedDirectory $copy.BaseRoot $copy.OwnedRoot $copy.Token } catch { } }
            if ($null -ne $runDirectory) { try { Remove-OwnedDirectory $runDirectory.BaseRoot $runDirectory.OwnedRoot $runDirectory.Token } catch { } }
        }
        if ($null -ne $copy) { Assert-True (-not (Test-Path -LiteralPath $copy.OwnedRoot)) 'The generated copy tree remained after cleanup.' }
        if ($null -ne $runDirectory) { Assert-True (-not (Test-Path -LiteralPath $runDirectory.OwnedRoot)) 'The generated run tree remained after cleanup.' }
    }
    Invoke-Check 'final source recheck controls Mp3tag verdict' {
        $functionNames = @('Read-TrustedMp3tagState','Read-CliGateResult','Assert-EvidenceInputsUnchanged','Get-Hashes','Get-DecodedAudioHash','Get-CoverPayloadHash','Invoke-JsonProcess','Remove-OwnedDirectory','Write-AnonymousResult')
        $originalFunctions = @{}
        foreach ($name in $functionNames) { $originalFunctions[$name] = (Get-Command $name -CommandType Function).ScriptBlock }
        try {
            $saveRoot = Join-Path $testRoot 'final-check-save'
            New-Item -ItemType Directory -Path $saveRoot -Force | Out-Null
            $prepared = Join-Path $testRoot 'final-check-prepared.m4b'
            $saved = Join-Path $saveRoot 'saved.m4b'
            [IO.File]::WriteAllText($prepared, 'prepared')
            [IO.File]::WriteAllText($saved, 'saved')
            $baseline = [ordered]@{ '01.mp3'='same' }
            $script:finalCheckState = [pscustomobject]@{
                originalSourcePath='original'; originalSourceHashes=$baseline; copiedSourcePath='copy'; copiedSourceHashes=$baseline
                saveRoot=$saveRoot; preparedOutputPath=$prepared; decodedAudioSha256=('a' * 64); coverPayloadSha256=('b' * 64)
                priorValidationStatus='Full'; priorAuditPresent=$true; priorFullDecodePassed=$true; priorStrategy='Reencode'
                copyBaseRoot='copy-base'; copyOwnedRoot='copy-owned'; copyToken='copy-token'
                runBaseRoot='run-base'; runOwnedRoot='run-owned'; runToken='run-token'
            }
            $script:finalHashCall = 0
            $script:mutateFinalSource = $false
            $script:preparedMediaMutated = $false
            $script:capturedFinalResult = $null
            $script:trustedStatePath = Join-Path $testRoot ('.missing-state-' + [guid]::NewGuid().ToString('N') + '.json')
            Set-Item Function:Read-TrustedMp3tagState { [pscustomobject]@{ State=$script:finalCheckState; StatePath=$script:trustedStatePath } }
            Set-Item Function:Read-CliGateResult { [pscustomobject]@{} }
            Set-Item Function:Assert-EvidenceInputsUnchanged { }
            Set-Item Function:Get-Hashes {
                $script:finalHashCall++
                if ($script:mutateFinalSource -and $script:finalHashCall -eq 3) { return [ordered]@{ '01.mp3'='changed' } }
                return [ordered]@{ '01.mp3'='same' }
            }
            Set-Item Function:Get-DecodedAudioHash { if ($script:preparedMediaMutated) { 'x' * 64 } else { 'a' * 64 } }
            Set-Item Function:Get-CoverPayloadHash { 'b' * 64 }
            Set-Item Function:Invoke-JsonProcess { [pscustomobject]@{ ExitCode=0; Stdout='{"format":{"tags":{"TITLE":"Title"}},"streams":[]}' } }
            Set-Item Function:Remove-OwnedDirectory { }
            Set-Item Function:Write-AnonymousResult { param($Path,$Result) $script:capturedFinalResult = $Result }

            $case = [pscustomobject]@{ caseId='case-04'; mp3tagFields=[pscustomobject]@{ TITLE='Title' } }
            $releasePaths = [pscustomobject]@{ Root='release'; Ffmpeg='ffmpeg'; Ffprobe='ffprobe' }
            Invoke-Mp3tagVerify $case ([pscustomobject]@{ cases=@() }) $releasePaths 'corpus' 'state' $saved 'result' 'cli' $true ('r' * 43) ('c' * 43)
            Assert-True ([string]$script:capturedFinalResult.status -eq 'passed') 'The unchanged positive control did not pass Mp3tag verification.'
            Assert-True ($script:capturedFinalResult.gateComplete -eq $true) 'The unchanged positive control did not complete the gate.'

            $script:capturedFinalResult = $null
            $script:preparedMediaMutated = $true
            Assert-Throws { Invoke-Mp3tagVerify $case ([pscustomobject]@{ cases=@() }) $releasePaths 'corpus' 'state' $saved 'result' 'cli' $true ('r' * 43) ('c' * 43) } 'verification failed' 'Changed prepared and saved media detached from the sealed preparation baseline.'
            Assert-True ($script:capturedFinalResult.mp3tag.audioEssencePreserved -eq $false) 'Changed prepared media matched the sealed audio baseline.'

            $script:preparedMediaMutated = $false
            $script:mutateFinalSource = $true
            $script:finalHashCall = 0
            $script:capturedFinalResult = $null
            Assert-Throws { Invoke-Mp3tagVerify $case ([pscustomobject]@{ cases=@() }) $releasePaths 'corpus' 'state' $saved 'result' 'cli' $true ('r' * 43) ('c' * 43) } 'verification failed' 'A failed final source recheck produced a passing verdict.'
            Assert-True ([string]$script:capturedFinalResult.status -eq 'failed') 'The final source mutation did not mark the result failed.'
            Assert-True ($script:capturedFinalResult.gateComplete -eq $false) 'The final source mutation completed the gate.'
            Assert-True ($script:capturedFinalResult.mp3tag.sourceHashesUnchanged -eq $false) 'The final source mutation was not recorded.'
        }
        finally {
            foreach ($name in $functionNames) { Set-Item ("Function:$name") $originalFunctions[$name] }
        }
    }
    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
        throw "$($failures.Count) acceptance harness safety test(s) failed; $passed passed."
    }
    "Acceptance harness safety tests passed: $passed."
}
finally {
    if (Test-Path -LiteralPath $functionsPath -PathType Leaf) { Remove-Item -LiteralPath $functionsPath -Force }
    foreach ($artifact in $createdArtifacts) {
        if (Test-Path -LiteralPath $artifact -PathType Leaf) { Remove-Item -LiteralPath $artifact -Force }
    }
    if ($null -ne $ownedTestRoot) {
        Remove-OwnedDirectory $ownedTestRoot.BaseRoot $ownedTestRoot.OwnedRoot $ownedTestRoot.Token
    }
}
