[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MediaToolDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    # Development only: reuse the packages and signed metadata of an earlier rehearsal output directory.
    [string]$ReusePackagesFrom,
    [string[]]$Scenario = @('upgrade', 'rollback', 'no-ready', 'launcher-killed', 'bad-release', 'cleanup')
)

# Packaged update rehearsal. Needs an interactive Windows desktop: it runs real GUI and launcher
# processes from two Test-flavor packages signed with a disposable key. It only touches the new
# output directory (under artifacts\), and records the pending CI gate as "run locally on a desktop".
Set-StrictMode -Version Latest
$Scenario = @($Scenario | ForEach-Object { $_ -split ',' })
foreach ($name in $Scenario) { if ($name -notin 'upgrade', 'rollback', 'no-ready', 'launcher-killed', 'bad-release', 'cleanup') { throw "Unknown scenario '$name'." } }
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.IO.Compression.FileSystem

$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')) + '\'
if (-not $out.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The output directory must be under artifacts\.' }
if (Test-Path -LiteralPath $out) { throw 'The output directory already exists; use a fresh directory.' }
$tools = [IO.Path]::GetFullPath($MediaToolDirectory)
foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe') { if (-not (Test-Path -LiteralPath (Join-Path $tools $tool))) { throw "Media tool directory is missing $tool." } }
$Version1 = '0.9.0'; $Version2 = '0.9.1'
$ImageNames = 'BookSplice.Gui', 'BookSplice.Launcher', 'booksplice', 'ffmpeg', 'ffprobe', 'BookSplice.LeaseHost'

# ---- environment preflight ------------------------------------------------------------------------
function Get-ImageProcesses { @(Get-Process -Name $ImageNames -ErrorAction SilentlyContinue) }
# FFmpeg is only judged by image path under the output directory: other tools on the machine use it too.
if (@(Get-Process -Name BookSplice.Gui, BookSplice.Launcher, booksplice, BookSplice.LeaseHost -ErrorAction SilentlyContinue).Count -gt 0) { throw 'A BookSplice process is already running; close it first.' }
$explorerSessions = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | ForEach-Object SessionId | Sort-Object -Unique)
if ($explorerSessions.Count -eq 0 -or $explorerSessions -notcontains (Get-Process -Id $PID).SessionId) {
    throw 'No interactive desktop: this shell is not in the same session as explorer.exe (see Session 0 in the WSL lessons).'
}
$realPrograms = Join-Path ([Environment]::GetFolderPath('Programs')) 'BookSplice.lnk'
$realData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BookSplice'
function Get-RealStateFingerprint {
    $shortcut = if (Test-Path -LiteralPath $realPrograms) { (Get-FileHash -LiteralPath $realPrograms).Hash } else { 'absent' }
    $data = if (Test-Path -LiteralPath $realData) {
        (Get-ChildItem -LiteralPath $realData -Recurse -Force -File -ErrorAction SilentlyContinue | Sort-Object FullName |
            ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n"
    } else { 'absent' }
    $bytes = [Text.Encoding]::UTF8.GetBytes("$shortcut`n$data")
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}
$realStateBefore = Get-RealStateFingerprint

New-Item -ItemType Directory -Path $out | Out-Null
[IO.File]::WriteAllText((Join-Path $out '.rehearsal-owner'), "BookSplice update rehearsal`n")

# ---- helpers --------------------------------------------------------------------------------------
$results = [Collections.Generic.List[object]]::new()
$script:assertions = $null
function Assert-That([string]$Name, [bool]$Condition, [string]$Detail = '') {
    $script:assertions.Add([ordered]@{ name = $Name; passed = $Condition; detail = $Detail })
}
function Rel([string]$Path) { if ($Path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase)) { $Path.Substring($out.Length).TrimStart('\') } else { '(outside output directory)' } }
function Sha([string]$Path) { if (Test-Path -LiteralPath $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() } else { 'absent' } }

function Get-OwnedProcesses {
    # Anything started from inside the output directory, found by image path (PIDs change across restarts).
    @(Get-ImageProcesses | Where-Object { try { $_.Path -and $_.Path.StartsWith($out + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
}
function Stop-OwnedProcesses { Get-OwnedProcesses | ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } } }
function Wait-Until([scriptblock]$Condition, [int]$Seconds, [int]$PollMs = 500) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) { $value = & $Condition; if ($value) { return $value }; Start-Sleep -Milliseconds $PollMs }
    $null
}

$ae = [Windows.Automation.AutomationElement]
function Get-TopWindows([string]$AutomationId, [string]$Name, [int[]]$ProcessIds) {
    $children = $ae::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $children) {
        $c = $w.Current
        if ($ProcessIds -and $ProcessIds -notcontains $c.ProcessId) { continue }
        if (($AutomationId -and $c.AutomationId -eq $AutomationId) -or ($Name -and $c.Name -eq $Name)) { $w }
    }
}
function Find-Child($Parent, [string]$AutomationId, [string]$Name) {
    $cond = if ($AutomationId) { [Windows.Automation.PropertyCondition]::new($ae::AutomationIdProperty, $AutomationId) }
            else { [Windows.Automation.PropertyCondition]::new($ae::NameProperty, $Name) }
    $Parent.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
}
function Invoke-Element($Element) { $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Get-MainWindow { @(Get-TopWindows 'BookSpliceMainWindow' $null (Get-OwnedProcesses | ForEach-Object Id)) | Select-Object -First 1 }
function Wait-MainWindow([int]$Seconds = 60) {
    $w = Wait-Until { Get-MainWindow } $Seconds
    if ($w) { Start-Sleep -Milliseconds 500 }   # let the first render settle before driving it
    $w
}
function Get-DialogText {
    # A modal launcher/GUI message box owned by one of our processes.
    $pids = Get-OwnedProcesses | ForEach-Object Id
    foreach ($w in $ae::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        if ($pids -contains $w.Current.ProcessId -and $w.Current.ClassName -eq '#32770') { return $w }
    }
}
function Dismiss-Dialog($Dialog) {
    $texts = @($Dialog.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_ -notin 'OK', 'Close' })
    $ok = $Dialog.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new($ae::NameProperty, 'OK'),
        [Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
    $closed = $false
    if ($ok) { try { Invoke-Element $ok; $closed = $true } catch { } }
    if (-not $closed) { try { $Dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() } catch { } }
    ($texts -join ' ')
}

function Set-Environment($Sandbox, [string]$ReleaseDir, [hashtable]$Extra = @{}) {
    foreach ($name in 'BOOKSPLICE_FFMPEG_DIR', 'BOOKSPLICE_TEST_NO_READY') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    $env:BOOKSPLICE_LOCAL_APP_DATA = $Sandbox.Data
    $env:BOOKSPLICE_TEST_RELEASE_DIR = $ReleaseDir
    $env:BOOKSPLICE_TEST_PROGRAMS_DIR = $Sandbox.Programs
    foreach ($key in $Extra.Keys) { Set-Item "Env:$key" $Extra[$key] }
}
function Clear-Environment {
    foreach ($name in 'BOOKSPLICE_LOCAL_APP_DATA', 'BOOKSPLICE_TEST_RELEASE_DIR', 'BOOKSPLICE_TEST_PROGRAMS_DIR', 'BOOKSPLICE_TEST_NO_READY') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
}

function New-Sandbox([string]$Name) {
    $root = Join-Path $out $Name
    $s = [ordered]@{ Root = $root; Apps = "$root\apps"; Data = "$root\data"; Programs = "$root\programs"; Books = "$root\books" }
    foreach ($d in $s.Apps, "$($s.Data)\BookSplice", $s.Programs, $s.Books) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    $s.Original = "$($s.Apps)\BookSplice-$Version1-win-x64"
    [IO.Compression.ZipFile]::ExtractToDirectory("$out\packages\BookSplice-$Version1-win-x64.zip", $s.Original)
    $settings = [ordered]@{ schemaVersion = 1; outputDirectory = $s.Books; qualityProfileId = 'high-quality'; channelPolicy = 'PreserveSourceChannels'
        createChapters = $true; conversionJobs = $null; collisionPolicy = 'AvoidCollision'; metadataProfileId = 'GenericMp4'; validationLevel = 'Lightweight'; logLevel = 'Information' } | ConvertTo-Json
    [IO.File]::WriteAllText("$($s.Data)\BookSplice\settings.json", $settings, [Text.UTF8Encoding]::new($false))
    $s.SettingsPath = "$($s.Data)\BookSplice\settings.json"
    $s.SettingsSha = Sha $s.SettingsPath
    # Unowned folders that must survive: one beside the app, named like a managed version.
    $s.Sentinel = "$($s.Apps)\BookSplice-$Version2-win-x64"
    New-Item -ItemType Directory -Path $s.Sentinel | Out-Null
    [IO.File]::WriteAllText("$($s.Sentinel)\keep.txt", "not owned by BookSplice`n")
    $s.SentinelSha = Sha "$($s.Sentinel)\keep.txt"
    [pscustomobject]$s
}
function Get-ManagedRoot($S) { Get-ChildItem -LiteralPath $S.Apps -Directory -Force | Where-Object Name -like '.booksplice-managed-*' | Select-Object -First 1 -ExpandProperty FullName }
function Read-Records($S) {
    $root = Get-ManagedRoot $S
    if (-not $root) { return $null }
    $read = { param($n) $p = "$root\records\$n"; if (Test-Path -LiteralPath $p) { Get-Content -LiteralPath $p -Raw | ConvertFrom-Json } }
    [pscustomobject]@{ Root = $root; Current = (& $read 'current.json'); Journal = (& $read 'journal.json') }
}
function Get-Target($Target) { if ($Target) { "$($Target.kind)/$($Target.version)" } else { 'none' } }

function Start-Gui([string]$Path, $S) {
    $p = Start-Process -FilePath $Path -PassThru
    $p
}
function Open-UpdatesWindow {
    $main = Wait-MainWindow 60
    if (-not $main) { throw 'The BookSplice main window did not appear.' }
    Invoke-Element (Find-Child $main 'UpdatesButton' $null)
    # The Updates window is owned by the main window, so UI Automation lists it beneath it.
    $w = Wait-Until { Find-Child $main 'BookSpliceUpdatesWindow' $null } 20
    if (-not $w) { throw 'The Updates window did not open.' }
    Start-Sleep -Milliseconds 500
    $w
}
# The status text is not exposed (its automation name is a fixed label), so outcomes are read from what
# the window offers: the Install button exists only while a verified release is available.
function Save-Shot($Window, [string]$Name) {
    try {
        Add-Type -AssemblyName System.Drawing
        $r = $Window.Current.BoundingRectangle
        $bmp = [Drawing.Bitmap]::new([int]$r.Width, [int]$r.Height)
        [Drawing.Graphics]::FromImage($bmp).CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
        New-Item -ItemType Directory -Path "$out\evidence" -Force | Out-Null
        $bmp.Save("$out\evidence\$Name.png"); $bmp.Dispose()
    } catch { }
}
function Invoke-Check($Window, [string]$ShotName) {
    Invoke-Element (Find-Child $Window 'Check' $null)
    Start-Sleep -Seconds 2
    [void](Wait-Until { (Find-Child $Window 'Check' $null).Current.IsEnabled } 60)
    Start-Sleep -Milliseconds 800
    if ($ShotName) { Save-Shot $Window $ShotName }
    $null -ne (Find-Child $Window 'Install' $null)
}
function Assert-OneResponsiveGui([string]$ExpectedDirectory) {
    $gui = @(Get-OwnedProcesses | Where-Object { $_.ProcessName -eq 'BookSplice.Gui' })
    Assert-That 'exactly one BookSplice GUI is running' ($gui.Count -eq 1) "found $($gui.Count)"
    if ($gui.Count -eq 1) {
        Assert-That 'the running GUI is the expected version folder' ($gui[0].Path.StartsWith($ExpectedDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) (Rel $gui[0].Path)
        Assert-That 'the GUI runs in the interactive session' ($gui[0].SessionId -eq (Get-Process -Id $PID).SessionId) "session $($gui[0].SessionId)"
        $main = Wait-MainWindow 30
        $name = if ($main) { $main.Current.Name } else { $null }
        Assert-That 'the main window answers UI Automation' ($name -eq 'BookSplice') "name '$name'"
    }
}
function Assert-Intact($S, [string]$Settings = $null) {
    Assert-That 'settings bytes are unchanged' ((Sha $S.SettingsPath) -eq $S.SettingsSha)
    Assert-That 'unowned sentinel folder is untouched' ((Sha "$($S.Sentinel)\keep.txt") -eq $S.SentinelSha)
}
function Describe-Process($Process) {
    $windows = @($ae::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ProcessId -eq $Process.Id } | ForEach-Object { "$($_.Current.ClassName):$($_.Current.Name)" })
    $line = (Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)").CommandLine -replace [regex]::Escape($out), '<out>'
    "$($Process.ProcessName) windows=[$($windows -join '|')] $line"
}
function Close-AllGui {
    foreach ($p in @(Get-OwnedProcesses | Where-Object { $_.ProcessName -eq 'BookSplice.Gui' })) {
        try { [void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000) } catch { }
    }
    [void](Wait-Until { $d = Get-DialogText; if ($d) { [void](Dismiss-Dialog $d) }; @(Get-OwnedProcesses).Count -eq 0 } 20)
    $left = @(Get-OwnedProcesses)
    Assert-That 'no BookSplice or FFmpeg process remains after closing' ($left.Count -eq 0) (($left | ForEach-Object { Describe-Process $_ }) -join '; ')
    Stop-OwnedProcesses
}

function Invoke-Scenario([string]$Name, [scriptblock]$Body) {
    $script:assertions = [Collections.Generic.List[object]]::new()
    $started = [DateTime]::UtcNow
    $status = 'passed'; $error_ = $null
    try { & $Body } catch { $status = 'failed'; $error_ = $_.Exception.Message
        try { $w = Get-MainWindow; if ($w) { $u = Find-Child $w 'BookSpliceUpdatesWindow' $null; if ($u) { Save-Shot $u "failure-$Name" } } } catch { } $script:assertions.Add([ordered]@{ name = 'scenario completed without an error'; passed = $false; detail = $error_ }) }
    finally { try { Stop-OwnedProcesses } catch { }; Clear-Environment }
    if (@($script:assertions | Where-Object { -not $_.passed }).Count -gt 0) { $status = 'failed' }
    $results.Add([ordered]@{ scenario = $Name; status = $status; seconds = [int]([DateTime]::UtcNow - $started).TotalSeconds; assertions = @($script:assertions) })
    Write-Host "[$status] $Name"
}

# ---- packages -------------------------------------------------------------------------------------
$packageInfo = @()
if ($ReusePackagesFrom) {
    $from = [IO.Path]::GetFullPath($ReusePackagesFrom)
    if (-not (Test-Path -LiteralPath (Join-Path $from '.rehearsal-owner'))) { throw 'ReusePackagesFrom is not a rehearsal output directory.' }
    Copy-Item -LiteralPath "$from\packages" -Destination "$out\packages" -Recurse
    Copy-Item -LiteralPath "$from\signed" -Destination "$out\signed" -Recurse
}
else {
    $keys = New-Item -ItemType Directory -Path "$out\keys"
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        [IO.File]::WriteAllText("$keys\test.key.pem", $rsa.ExportPkcs8PrivateKeyPem())
        [IO.File]::WriteAllText("$keys\test.pub.pem", $rsa.ExportSubjectPublicKeyInfoPem())
    }
    finally { $rsa.Dispose() }
    try {
        foreach ($v in $Version1, $Version2) {
            & (Join-Path $repositoryRoot 'scripts\Build-Release.ps1') -Version $v -MediaToolDirectory $tools -OutputDirectory "$out\packages" `
                -Flavor Test -TestPublicKeyPath "$keys\test.pub.pem" | Out-Null
        }
        & (Join-Path $PSScriptRoot 'New-UpdateManifest.ps1') -ArchivePath "$out\packages\BookSplice-$Version2-win-x64.zip" -Version $Version2 `
            -SigningKeyPath "$keys\test.key.pem" -PublicKeyPath "$keys\test.pub.pem" -OutputDirectory "$out\signed" | Out-Null
    }
    finally { Remove-Item -LiteralPath "$keys\test.key.pem" -Force -ErrorAction SilentlyContinue }
}
foreach ($v in $Version1, $Version2) {
    $zip = "$out\packages\BookSplice-$v-win-x64.zip"
    $flavor = 'unknown'
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.GetEntry('VERSION.txt')
        $reader = [IO.StreamReader]::new($entry.Open())
        try { if ($reader.ReadToEnd() -match '(?m)^Build flavor: (\w+)') { $flavor = $Matches[1] } } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
    $packageInfo += [ordered]@{ version = $v; flavor = $flavor; sha256 = Sha $zip }
}

# Local release directories: one subdirectory per tag, as TestReleaseSource expects.
function New-ReleaseDirectory([string]$Name, [string]$Tag, [string]$AssetVersion, [switch]$FlipSignature) {
    $dir = New-Item -ItemType Directory -Path "$out\releases\$Name\$Tag" -Force
    $pairs = @{ "BookSplice-$Version2-win-x64.zip" = "packages\BookSplice-$Version2-win-x64.zip"; "BookSplice-$Version2-win-x64.zip.sha256" = "packages\BookSplice-$Version2-win-x64.zip.sha256"
        "BookSplice-$Version2-win-x64.update.json" = "signed\BookSplice-$Version2-win-x64.update.json"; "BookSplice-$Version2-win-x64.update.sig.json" = "signed\BookSplice-$Version2-win-x64.update.sig.json" }
    foreach ($asset in $pairs.Keys) {
        Copy-Item -LiteralPath "$out\$($pairs[$asset])" -Destination "$dir\$($asset.Replace($Version2, $AssetVersion))"
    }
    if ($FlipSignature) {
        $path = "$dir\BookSplice-$AssetVersion-win-x64.update.sig.json"
        $text = Get-Content -LiteralPath $path -Raw
        $m = [regex]::Match($text, '"signature":"([A-Za-z0-9+/]{8})')
        $flipped = if ($m.Groups[1].Value[0] -eq 'A') { 'B' } else { 'A' }
        [IO.File]::WriteAllText($path, $text.Remove($m.Groups[1].Index, 1).Insert($m.Groups[1].Index, $flipped), [Text.UTF8Encoding]::new($false))
    }
    "$out\releases\$Name"
}
$goodRelease = New-ReleaseDirectory 'good' "v$Version2" $Version2
$badSigRelease = New-ReleaseDirectory 'bad-signature' "v$Version2" $Version2 -FlipSignature
$wrongVersionRelease = New-ReleaseDirectory 'wrong-version' 'v0.9.2' '0.9.2'

# ---- scenarios ------------------------------------------------------------------------------------
function Install-ThroughGui($S, [string]$ReleaseDir, [hashtable]$Extra = @{}) {
    Set-Environment $S $ReleaseDir $Extra
    $gui = Start-Gui "$($S.Original)\BookSplice.Gui.exe" $S
    $session = (Get-Process -Id $gui.Id).SessionId
    $explorer = @(Get-Process -Name explorer | ForEach-Object SessionId)
    Assert-That 'the GUI started in the interactive desktop session' ($explorer -contains $session) "session $session"
    $updates = Open-UpdatesWindow
    $offered = Invoke-Check $updates
    Assert-That 'the check offers the signed release' $offered
    Invoke-Element (Find-Child $updates 'Install' $null)
}

function Wait-NewVersionRunning($S) {
    $root = Get-ManagedRoot $S
    Wait-Until {
        $g = @(Get-OwnedProcesses | Where-Object { $_.ProcessName -eq 'BookSplice.Gui' -and $_.Path -like '*\versions\*' })
        if ($g.Count -eq 1 -and (Get-MainWindow) -and "$((Read-Records $S).Journal.phase)" -eq '3') { $true } else { $d = Get-DialogText; if ($d) { throw "A dialog appeared: $(Dismiss-Dialog $d)" } }
    } 150
}

$s1 = $null
if ($Scenario -contains 'upgrade' -or $Scenario -contains 'rollback') {
    Invoke-Scenario 'upgrade' {
        $script:s1 = New-Sandbox 'upgrade'
        $S = $script:s1
        Install-ThroughGui $S $goodRelease
        Assert-That 'the new version started after the update' ([bool](Wait-NewVersionRunning $S))
        $rec = Read-Records $S
        $script:s1Records = $rec
        Assert-That 'journal committed' ($rec.Journal -and "$($rec.Journal.phase)" -in 'Committed', '3') "phase $($rec.Journal.phase)"
        Assert-That 'current is the managed new version' ((Get-Target $rec.Current.current) -like "Managed/$Version2*" -or (Get-Target $rec.Current.current) -like "*/$Version2") (Get-Target $rec.Current.current)
        Assert-That 'previous is the original version' ((Get-Target $rec.Current.previous) -like "*/$Version1") (Get-Target $rec.Current.previous)
        Assert-That 'stable launcher exists' (Test-Path "$($rec.Root)\BookSplice.Launcher.exe")
        Assert-That 'managed root is beside the original folder' ((Split-Path $rec.Root) -eq $S.Apps)
        $shortcut = "$($S.Programs)\BookSplice.lnk"
        Assert-That 'shortcut was created in the isolated programs directory' ([bool](Wait-Until { Test-Path -LiteralPath $shortcut } 30)) (Rel $shortcut)
        Assert-OneResponsiveGui (Split-Path $rec.Current.current.executablePath)
        Assert-Intact $S
        # Generated-audio conversion with the new version's bundled tools.
        $dir = Split-Path $rec.Current.current.executablePath
        $ffmpeg = "$dir\tools\ffmpeg\ffmpeg.exe"
        $source = "$($S.Root)\generated-source.m4a"
        & $ffmpeg -hide_banner -loglevel error -f lavfi -i 'sine=frequency=440:duration=2' -metadata album=Rehearsal -metadata artist=BookSplice -c:a aac -y $source
        $cliData = New-Item -ItemType Directory -Path "$($S.Root)\cli-data"
        $cliOut = New-Item -ItemType Directory -Path "$($S.Root)\cli-output"
        $savedData = $env:BOOKSPLICE_LOCAL_APP_DATA
        try {
            $env:BOOKSPLICE_LOCAL_APP_DATA = $cliData.FullName
            & "$dir\booksplice.exe" $source --output $cliOut.FullName --validation full --metadata-profile GenericMp4 | Out-Null
            Assert-That 'CLI conversion with the new bundled tools exited 0' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
        }
        finally { $env:BOOKSPLICE_LOCAL_APP_DATA = $savedData }
        $m4b = @(Get-ChildItem -LiteralPath $cliOut.FullName -Filter '*.m4b' -File)
        Assert-That 'conversion produced one validated audiobook' ($m4b.Count -eq 1) "found $($m4b.Count)"
        if ($m4b.Count -eq 1) {
            $probe = & "$dir\tools\ffmpeg\ffprobe.exe" -v error -show_streams -show_chapters -of json $m4b[0].FullName | Out-String | ConvertFrom-Json
            Assert-That 'output has one audio stream and one chapter' ((@($probe.streams | Where-Object codec_type -eq 'audio').Count -eq 1) -and (@($probe.chapters).Count -eq 1))
        }
        Assert-That 'conversion left no FFmpeg process behind' ((@(Get-OwnedProcesses | Where-Object { $_.ProcessName -in 'ffmpeg', 'ffprobe', 'booksplice' })).Count -eq 0)
        # leave the 0.9.1 GUI running for the rollback scenario when it is requested
        if ($Scenario -notcontains 'rollback') { Close-AllGui }
    }
}

if ($Scenario -contains 'rollback') {
    if ($s1 -and ($results | Where-Object scenario -eq 'upgrade').status -eq 'passed') {
        Invoke-Scenario 'rollback' {
            $S = $script:s1
            Set-Environment $S $goodRelease
            $before = Read-Records $S
            Start-Process -FilePath "$($before.Root)\BookSplice.Launcher.exe" | Out-Null
            $updates = Open-UpdatesWindow
            $rollbackButton = Find-Child $updates $null 'Roll back to previous version'
            Assert-That 'the rollback button is enabled (a previous version is recorded)' ([bool](Wait-Until { $rollbackButton.Current.IsEnabled } 20))
            # Rollback asks for confirmation first; the box is owned by the Updates window, so UI Automation lists it beneath it.
            try { Invoke-Element $rollbackButton } catch { }
            $confirm = Wait-Until { Find-Child $updates $null 'Roll back BookSplice' } 20
            Assert-That 'the rollback confirmation appeared' ($null -ne $confirm)
            if ($confirm) {
                # Match may be the title text: use the dialog window that contains it.
                while ($confirm -and $confirm.Current.ControlType -ne [Windows.Automation.ControlType]::Window) { $confirm = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($confirm) }
                $buttons = @($confirm.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
                $yes = $buttons | Where-Object { $_.Current.Name -match '^&?Yes$' } | Select-Object -First 1
                if ($yes) { Invoke-Element $yes }
                else {
                    # A standard Yes/No MessageBox: focus it and answer with the Y accelerator.
                    Add-Type -AssemblyName System.Windows.Forms
                    try { $confirm.SetFocus() } catch { }
                    Start-Sleep -Milliseconds 500
                    [Windows.Forms.SendKeys]::SendWait('y')
                }
            }
            $started = Wait-Until { $g = @(Get-OwnedProcesses | Where-Object { $_.ProcessName -eq 'BookSplice.Gui' -and $_.Path -like "$($S.Original)\*" }); if ($g.Count -eq 1 -and (Get-MainWindow) -and (Read-Records $S).Journal.id -ne $before.Journal.id -and "$((Read-Records $S).Journal.phase)" -eq '3') { $true } else { $d = Get-DialogText; if ($d) { throw "A dialog appeared: $(Dismiss-Dialog $d)" } } } 150
            Assert-That 'the previous version started after the rollback' ([bool]$started)
            if (-not $started) { try { Save-Shot $updates 'rollback-not-restarted' } catch { } }
            $after = Read-Records $S
            Assert-That 'current is now the former previous (0.9.0)' ((Get-Target $after.Current.current) -eq (Get-Target $before.Current.previous) -and $after.Current.current.executablePath -eq $before.Current.previous.executablePath) (Get-Target $after.Current.current)
            Assert-That 'previous is now the former current (0.9.1)' ((Get-Target $after.Current.previous) -eq (Get-Target $before.Current.current) -and $after.Current.previous.executablePath -eq $before.Current.current.executablePath) (Get-Target $after.Current.previous)
            Assert-OneResponsiveGui $S.Original
            Assert-Intact $S
            Close-AllGui
        }
    }
    else { $results.Add([ordered]@{ scenario = 'rollback'; status = 'not run'; seconds = 0; assertions = @() }) }
}

$s2 = $null
if ($Scenario -contains 'no-ready' -or $Scenario -contains 'cleanup') {
    Invoke-Scenario 'no-ready' {
        $script:s2 = New-Sandbox 'no-ready'
        $S = $script:s2
        Install-ThroughGui $S $goodRelease @{ BOOKSPLICE_TEST_NO_READY = '1' }
        $dialog = Wait-Until { Get-DialogText } 150
        Assert-That 'the launcher reported the failed update' ($null -ne $dialog)
        if ($dialog) {
            $text = Dismiss-Dialog $dialog
            Assert-That 'the notice says the previous version was started' ($text -like '*previous version was started*') $text
        }
        [void](Wait-Until { @(Get-OwnedProcesses | Where-Object { $_.ProcessName -eq 'BookSplice.Launcher' }).Count -eq 0 } 30)
        [void](Wait-MainWindow 60)
        $rec = Read-Records $S
        Assert-That 'journal records the failure' ($rec.Journal -and "$($rec.Journal.phase)" -in 'Failed', '4') "phase $($rec.Journal.phase)"
        Assert-That 'mapping is unchanged (original 0.9.0, no previous)' ((Get-Target $rec.Current.current) -like "*/$Version1" -and -not $rec.Current.previous) "$(Get-Target $rec.Current.current) / $(Get-Target $rec.Current.previous)"
        Assert-OneResponsiveGui $S.Original
        Assert-Intact $S
        Close-AllGui
    }
}

if ($Scenario -contains 'launcher-killed') {
    Invoke-Scenario 'launcher-killed' {
        $S = New-Sandbox 'launcher-killed'
        Install-ThroughGui $S $goodRelease @{ BOOKSPLICE_TEST_NO_READY = '1' }
        $started = Wait-Until { $r = Read-Records $S; if ($r -and $r.Journal -and "$($r.Journal.phase)" -in 'Starting', '2' -and (Get-OwnedProcesses | Where-Object { $_.ProcessName -eq 'BookSplice.Launcher' })) { $r } } 90
        Assert-That 'the journal reached Starting with a live launcher' ($null -ne $started)
        $candidate = Wait-Until { Get-OwnedProcesses | Where-Object { $_.ProcessName -eq 'BookSplice.Gui' -and $_.Path -like '*\versions\*' } } 30
        Assert-That 'the candidate was started' ($null -ne $candidate)
        Get-OwnedProcesses | Where-Object { $_.ProcessName -eq 'BookSplice.Launcher' } | ForEach-Object { Stop-Process -Id $_.Id -Force }
        # The orphaned candidate gives up on its own (60 s readiness limit); a normal launch must wait for it.
        $gone = Wait-Until { @(Get-OwnedProcesses).Count -eq 0 } 100
        Assert-That 'the orphaned candidate exited on its own' ($null -ne $gone)
        Stop-OwnedProcesses
        $rec = Read-Records $S
        Set-Environment $S $goodRelease
        Start-Process -FilePath "$($rec.Root)\BookSplice.Launcher.exe" | Out-Null
        [void](Wait-MainWindow 60)
        $after = Read-Records $S
        Assert-That 'recovery marked the journal Failed' ("$($after.Journal.phase)" -in 'Failed', '4') "phase $($after.Journal.phase)"
        Assert-That 'mapping is unchanged (original 0.9.0, no previous)' ((Get-Target $after.Current.current) -like "*/$Version1" -and -not $after.Current.previous)
        Assert-OneResponsiveGui $S.Original
        Assert-Intact $S
        Close-AllGui
    }
}

if ($Scenario -contains 'bad-release') {
    Invoke-Scenario 'bad-release' {
        $S = New-Sandbox 'bad-release'
        foreach ($case in @(@('bad signature', $badSigRelease), @('wrong version', $wrongVersionRelease))) {
            Set-Environment $S $case[1]
            $gui = Start-Gui "$($S.Original)\BookSplice.Gui.exe" $S
            $updates = Open-UpdatesWindow
            $offered = Invoke-Check $updates ("refused-" + $case[0].Replace(' ', '-'))
            Assert-That "$($case[0]): the release is refused (no install offered)" (-not $offered)
            Close-AllGui
        }
        # Positive control: the same GUI and sandbox offer the properly signed release.
        Set-Environment $S $goodRelease
        $gui = Start-Gui "$($S.Original)\BookSplice.Gui.exe" $S
        $updates = Open-UpdatesWindow
        Assert-That 'control: the correctly signed release is offered' (Invoke-Check $updates 'control-offered')
        Close-AllGui
        Assert-That 'nothing was staged: no managed installation exists' ($null -eq (Get-ManagedRoot $S))
        Assert-Intact $S
    }
}

if ($Scenario -contains 'cleanup') {
    if ($s2 -and ($results | Where-Object scenario -eq 'no-ready').status -eq 'passed') {
        Invoke-Scenario 'cleanup' {
            $S = $script:s2
            $root = Get-ManagedRoot $S
            $ownership = Get-Content -LiteralPath "$root\records\ownership.json" -Raw | ConvertFrom-Json
            $eligible = @($ownership.entries | Where-Object { $_.kind -in 'version', 'stage', 'payload' } | ForEach-Object path | Where-Object { Test-Path -LiteralPath $_ })
            Assert-That 'the failed attempt left at least one eligible owned directory' ($eligible.Count -gt 0) "$($eligible.Count) present"
            $unownedVersion = New-Item -ItemType Directory -Path "$root\versions\$Version2" -Force
            [IO.File]::WriteAllText("$unownedVersion\keep.txt", "not owned`n")
            $keepSha = Sha "$unownedVersion\keep.txt"
            Set-Environment $S $goodRelease
            Start-Process -FilePath "$root\BookSplice.Launcher.exe" | Out-Null
            [void](Wait-MainWindow 60)
            [void](Wait-Until { (@($eligible | Where-Object { Test-Path -LiteralPath $_ })).Count -eq 0 } 30)
            $remaining = @($eligible | Where-Object { Test-Path -LiteralPath $_ })
            Assert-That 'the eligible owned directories were removed' ($remaining.Count -eq 0) "$($remaining.Count) remain"
            Assert-That 'the unowned folder named like a version survived' ((Sha "$unownedVersion\keep.txt") -eq $keepSha)
            Assert-That 'the original app folder survived' (Test-Path -LiteralPath "$($S.Original)\BookSplice.Gui.exe")
            Assert-OneResponsiveGui $S.Original
            Assert-Intact $S
            Close-AllGui
        }
    }
    else { $results.Add([ordered]@{ scenario = 'cleanup'; status = 'not run'; seconds = 0; assertions = @() }) }
}

# ---- aggregate ------------------------------------------------------------------------------------
$realStateAfter = Get-RealStateFingerprint
$strays = @(Get-OwnedProcesses)
$names = 'upgrade', 'rollback', 'no-ready', 'launcher-killed', 'bad-release', 'cleanup'
$ordered = foreach ($n in $names) {
    $r = $results | Where-Object scenario -eq $n | Select-Object -First 1
    if ($r) { $r } else { [ordered]@{ scenario = $n; status = 'not run'; seconds = 0; assertions = @() } }
}
$ran = @($ordered | Where-Object { $_.status -ne 'not run' })
$overall = if (@($ordered | Where-Object status -eq 'failed').Count -gt 0 -or $realStateBefore -ne $realStateAfter -or $strays.Count -gt 0) { 'failed' }
           elseif ($ran.Count -eq $names.Count) { 'passed' } else { 'incomplete' }
$aggregate = [ordered]@{
    schemaVersion = 1
    status = $overall
    gate = 'run locally on an interactive Windows desktop; hosted CI cannot show a real desktop'
    uiDriver = 'UI Automation from PowerShell'
    packages = $packageInfo
    scenarios = @($ordered)
    processCleanup = [ordered]@{ strayProcessesAfterRun = $strays.Count }
    realUserStateUnchanged = ($realStateBefore -eq $realStateAfter)
}
$json = $aggregate | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText("$out\rehearsal-result.json", $json, [Text.UTF8Encoding]::new($false))
$json
if ($overall -ne 'passed') { exit 1 }
