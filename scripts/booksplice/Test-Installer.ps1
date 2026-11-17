[CmdletBinding()]
param(
    # BookSplice-<version>-win-x64-setup.exe from Build-Installer.ps1.
    [Parameter(Mandatory = $true)][string]$SetupPath,
    # The ZIP the setup was built from. The portable cases extract it.
    [Parameter(Mandatory = $true)][string]$ZipPath,
    # Scratch folders. Each must be new or empty. The data root is exported as BOOKSPLICE_LOCAL_APP_DATA to every child.
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    # Where a Test-flavor package writes BookSplice.lnk. Required for a Test package, ignored by a Release one.
    [string]$ProgramsDir,
    # A Release-flavor package ignores ProgramsDir and uses the real Start menu. Pass this only on a throwaway machine.
    [switch]$AllowRealStartMenu
)

# Packaged installer test. Runs the real setup and uninstaller silently against scratch folders.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$setup = [IO.Path]::GetFullPath($SetupPath)
$zip = [IO.Path]::GetFullPath($ZipPath)
foreach ($file in $setup, $zip) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Not found: $file" } }

# ---- flavor: decides where the shortcut lands -------------------------------------------------------
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.GetEntry('VERSION.txt')
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $versionText = $reader.ReadToEnd() } finally { $reader.Dispose() }
}
finally { $archive.Dispose() }
if ($versionText -notmatch '(?m)^Build flavor: (Test|Release)\s*$') { throw 'The ZIP has no build flavor in VERSION.txt.' }
$flavor = $Matches[1]
$setupDescription = (Get-Item -LiteralPath $setup).VersionInfo.FileDescription
$setupIsTest = $setupDescription -match 'test build'
if (($flavor -eq 'Test') -ne $setupIsTest) { throw "The setup ('$setupDescription') and the ZIP ($flavor flavor) are different flavors." }
if ($flavor -eq 'Release' -and -not $AllowRealStartMenu) {
    throw 'This is a Release-flavor package: it ignores BOOKSPLICE_TEST_PROGRAMS_DIR and would write the real Start menu. Pass -AllowRealStartMenu on a throwaway machine only.'
}
if ($flavor -eq 'Test' -and -not $ProgramsDir) { throw 'A Test-flavor package needs -ProgramsDir.' }

# ---- scratch folders --------------------------------------------------------------------------------
function Resolve-Scratch([string]$Path, [string]$Name) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "-$Name must be an absolute path." }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $local = [Environment]::GetFolderPath('LocalApplicationData').TrimEnd('\')
    $real = @($local, (Join-Path $local 'BookSplice'), (Join-Path $local 'Programs'), (Join-Path $local 'Programs\BookSplice'),
        [Environment]::GetFolderPath('Programs').TrimEnd('\'), [Environment]::GetFolderPath('UserProfile').TrimEnd('\'))
    if ($full.Length -lt 12 -or $full -in $real) { throw "-$Name '$full' is not a safe scratch folder." }
    if ((Test-Path -LiteralPath $full) -and @(Get-ChildItem -LiteralPath $full -Force).Count -gt 0) { throw "-$Name '$full' must be new or empty." }
    $full
}
$data = Resolve-Scratch $DataRoot 'DataRoot'
$install = Resolve-Scratch $InstallRoot 'InstallRoot'
# A portable copy keeps its managed folder beside it, so give it a home of its own.
$portableHome = Resolve-Scratch "$install-portable" 'InstallRoot (portable home)'
$portable = Join-Path $portableHome 'BookSplice'
$programs = if ($flavor -eq 'Test') { Resolve-Scratch $ProgramsDir 'ProgramsDir' } else { [Environment]::GetFolderPath('Programs') }
$managedData = Join-Path $data 'BookSplice'
$shortcutPath = Join-Path $programs 'BookSplice.lnk'
$scratch = @($data, $install, $portableHome) + $(if ($flavor -eq 'Test') { $programs })
if ($flavor -eq 'Release') {
    if (Test-Path -LiteralPath $shortcutPath) { throw "A BookSplice.lnk already exists in the real Start menu; refusing to run on this machine." }
}
$uninstallKey = if ($flavor -eq 'Test') { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C94E2B87-0D3A-4F6B-A1D5-7E88F20B3C46}_is1' }
else { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{3B7D5A4E-6F1C-4C58-9E0A-52D7B1F46A19}_is1' }
if (Test-Path -LiteralPath $uninstallKey) { throw 'The installer uninstall entry already exists on this machine; refusing to run.' }

# --unregister deletes the toast key under HKCU, which the GUI also owns, and Test builds cannot redirect it.
# Keep a copy of the real key and put it back at the end.
$toastKey = 'HKCU\Software\Classes\AppUserModelId\BookSplice.App'
$toastBackup = Join-Path ([IO.Path]::GetTempPath()) "booksplice-toast-key-$([guid]::NewGuid().ToString('N')).reg"
function Save-RegistryKey([string]$Key, [string]$File) {
    & reg.exe query $Key 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return $false }
    & reg.exe export $Key $File /y 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not back up $Key." }
    $true
}
function Restore-RegistryKey([string]$Key, [string]$File) {
    if (-not (Test-Path -LiteralPath $File)) { return }
    & reg.exe import $File 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restore $Key from $File." }
    Remove-Item -LiteralPath $File -Force
}
$toastSaved = Save-RegistryKey $toastKey $toastBackup

$saved = @{}
foreach ($name in 'BOOKSPLICE_LOCAL_APP_DATA', 'BOOKSPLICE_TEST_PROGRAMS_DIR') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$env:BOOKSPLICE_LOCAL_APP_DATA = $data
if ($flavor -eq 'Test') { $env:BOOKSPLICE_TEST_PROGRAMS_DIR = $programs }

# ---- helpers ----------------------------------------------------------------------------------------
$results = [Collections.Generic.List[object]]::new()
$script:case = ''
$script:failed = 0
function Assert-That([string]$Name, [bool]$Condition, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Case = $script:case; Check = $Name; Passed = $Condition })
    if (-not $Condition) { $script:failed++; Write-Host "  FAIL [$($script:case)] $Name $Detail" -ForegroundColor Red }
}
function Reset-Scratch {
    foreach ($dir in $scratch) {
        if (Test-Path -LiteralPath $dir) { Get-ChildItem -LiteralPath $dir -Force | Remove-Item -Recurse -Force }
        else { New-Item -ItemType Directory -Path $dir | Out-Null }
    }
}
function Wait-Process-Exit([Diagnostics.Process]$Process, [string]$What) {
    if (-not $Process.WaitForExit(300000)) { $Process.Kill($true); throw "$What did not finish in 5 minutes." }
    $Process.ExitCode
}
function Invoke-Setup([string[]]$Extra = @()) {
    $log = Join-Path ([IO.Path]::GetTempPath()) "booksplice-setup-$([guid]::NewGuid().ToString('N')).log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$install", "/LOG=$log") + $Extra
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $code = Wait-Process-Exit $process 'Setup'
    $script:lastSetupLog = $log
    $code
}
function Invoke-Uninstall {
    $uninstaller = Join-Path $install 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller)) { return $null }
    $process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -WindowStyle Hidden
    $code = Wait-Process-Exit $process 'Uninstall'
    # The uninstaller finishes from a temporary copy; give it a moment to delete itself.
    for ($i = 0; $i -lt 100 -and (Test-Path -LiteralPath $uninstaller); $i++) { Start-Sleep -Milliseconds 100 }
    $code
}
function Expand-Package([string]$Destination) { [IO.Compression.ZipFile]::ExtractToDirectory($zip, $Destination) }
function Invoke-Launcher([string]$Launcher, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Launcher -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    Wait-Process-Exit $process 'Launcher'
}
function Get-Bindings {
    $registry = Join-Path $managedData 'updates\registry.json'
    if (-not (Test-Path -LiteralPath $registry)) { return @() }
    @((Get-Content -LiteralPath $registry -Raw | ConvertFrom-Json).bindings)
}
function Get-InstallBinding {
    $executable = Join-Path $install 'app\BookSplice.Gui.exe'
    @(Get-Bindings | Where-Object { [string]::Equals($_.executablePath, $executable, 'OrdinalIgnoreCase') })
}
function Get-ManagedRoots { @(Get-ChildItem -LiteralPath $install -Directory -Force -Filter '.booksplice-managed-*' -ErrorAction SilentlyContinue) }
function Get-ShortcutTarget {
    if (-not (Test-Path -LiteralPath $shortcutPath)) { return $null }
    (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath).TargetPath
}
function Get-Snapshot {
    $files = if (Test-Path -LiteralPath $install) {
        (Get-ChildItem -LiteralPath $install -Recurse -Force -File | Sort-Object FullName | ForEach-Object { "$($_.FullName)|$($_.Length)" }) -join "`n"
    } else { 'absent' }
    $registry = Join-Path $managedData 'updates\registry.json'
    $reg = if (Test-Path -LiteralPath $registry) { (Get-FileHash -LiteralPath $registry).Hash } else { 'absent' }
    $link = if (Test-Path -LiteralPath $shortcutPath) { (Get-FileHash -LiteralPath $shortcutPath).Hash } else { 'absent' }
    "$files`n$reg`n$link"
}
function Test-InstallIntact([string]$Label) {
    $managed = @(Get-ManagedRoots)
    Assert-That "$Label`: app folder present" (Test-Path -LiteralPath (Join-Path $install 'app\BookSplice.Gui.exe'))
    Assert-That "$Label`: one managed root" ($managed.Count -eq 1)
    Assert-That "$Label`: registry binding present" (@(Get-InstallBinding).Count -eq 1)
}
function Test-Removed([string]$Label) {
    # Inno removes its own folder last; allow a moment for it.
    for ($i = 0; $i -lt 50 -and (Test-Path -LiteralPath $install) -and @(Get-ChildItem -LiteralPath $install -Force).Count -gt 0; $i++) { Start-Sleep -Milliseconds 100 }
    Assert-That "$Label`: app folder gone" (-not (Test-Path -LiteralPath (Join-Path $install 'app')))
    Assert-That "$Label`: managed root gone" (@(Get-ManagedRoots).Count -eq 0)
    Assert-That "$Label`: install folder empty or gone" (-not (Test-Path -LiteralPath $install) -or @(Get-ChildItem -LiteralPath $install -Force).Count -eq 0)
    Assert-That "$Label`: registry binding gone" (@(Get-InstallBinding).Count -eq 0)
    Assert-That "$Label`: no uninstall entry" (-not (Test-Path -LiteralPath $uninstallKey))
}
function Hold-GuiLease {
    New-Item -ItemType Directory -Path $managedData -Force | Out-Null
    $stream = [IO.FileStream]::new((Join-Path $managedData 'gui-instance.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    $stream.Lock(0, 1)
    $stream
}
function Release-GuiLease($Stream) { $Stream.Unlock(0, 1); $Stream.Dispose() }

$sentinel = Join-Path $managedData 'settings-sentinel.txt'
function Add-Sentinel { New-Item -ItemType Directory -Path $managedData -Force | Out-Null; Set-Content -LiteralPath $sentinel -Value 'keep' }

# ---- cases ------------------------------------------------------------------------------------------
try {
    Reset-Scratch

    $script:case = 'clean install'
    Write-Host "== $($script:case)"
    Add-Sentinel
    $code = Invoke-Setup
    Assert-That 'setup exits 0' ($code -eq 0) "exit $code"
    Test-InstallIntact 'installed'
    $managed = @(Get-ManagedRoots)
    if ($managed.Count -eq 1) {
        $current = Get-Content -LiteralPath (Join-Path $managed[0].FullName 'records\current.json') -Raw | ConvertFrom-Json
        Assert-That 'current.json points at the original' ($current.current.kind -in 'Original', 0) ($current.current | ConvertTo-Json -Depth 5 -Compress)
        Assert-That 'stable launcher exists' (Test-Path -LiteralPath (Join-Path $managed[0].FullName 'BookSplice.Launcher.exe'))
        Assert-That 'shortcut targets the stable launcher' ([string]::Equals((Get-ShortcutTarget), (Join-Path $managed[0].FullName 'BookSplice.Launcher.exe'), 'OrdinalIgnoreCase')) (Get-ShortcutTarget)
    }
    Assert-That 'uninstaller written' (Test-Path -LiteralPath (Join-Path $install 'unins000.exe'))
    Assert-That 'uninstall entry written' (Test-Path -LiteralPath $uninstallKey)

    $script:case = 'setup again'
    Write-Host "== $($script:case)"
    $before = Get-Snapshot
    $code = Invoke-Setup
    Assert-That 'setup refuses with a non-zero exit' ($code -ne 0) "exit $code"
    Assert-That 'nothing changed' ($before -ceq (Get-Snapshot))

    $script:case = 'busy uninstall'
    Write-Host "== $($script:case)"
    $before = Get-Snapshot
    $lease = Hold-GuiLease
    try {
        $code = Invoke-Uninstall
        Assert-That 'uninstall aborts with a non-zero exit' ($code -ne 0) "exit $code"
    }
    finally { Release-GuiLease $lease }
    Test-InstallIntact 'after the refused uninstall'
    Assert-That 'shortcut still present' ($null -ne (Get-ShortcutTarget))
    Assert-That 'uninstall entry kept' (Test-Path -LiteralPath $uninstallKey)

    $script:case = 'uninstall'
    Write-Host "== $($script:case)"
    $code = Invoke-Uninstall
    Assert-That 'uninstall exits 0' ($code -eq 0) "exit $code"
    Test-Removed 'uninstalled'
    Assert-That 'owned shortcut gone' (-not (Test-Path -LiteralPath $shortcutPath))
    Assert-That 'data root sentinel survives' ((Test-Path -LiteralPath $sentinel) -and (Get-Content -LiteralPath $sentinel) -eq 'keep')

    $script:case = 'reinstall after uninstall'
    Write-Host "== $($script:case)"
    $code = Invoke-Setup
    Assert-That 'setup exits 0' ($code -eq 0) "exit $code"
    Test-InstallIntact 'reinstalled'
    Assert-That 'shortcut present' ($null -ne (Get-ShortcutTarget))
    Assert-That 'uninstall exits 0' ((Invoke-Uninstall) -eq 0)
    Test-Removed 'after the reinstall'

    $script:case = 'busy setup'
    Write-Host "== $($script:case)"
    Reset-Scratch
    $lease = Hold-GuiLease
    try {
        $code = Invoke-Setup
        Assert-That 'setup fails with a non-zero exit while BookSplice runs' ($code -ne 0) "exit $code"
        Assert-That 'no binding written' (@(Get-InstallBinding).Count -eq 0)
        Write-Host "  left behind: app folder $(Test-Path -LiteralPath (Join-Path $install 'app')), uninstaller $(Test-Path -LiteralPath (Join-Path $install 'unins000.exe')), uninstall entry $(Test-Path -LiteralPath $uninstallKey)"
    }
    finally { Release-GuiLease $lease }
    $code = Invoke-Setup
    Assert-That 'setup run again recovers' ($code -eq 0) "exit $code"
    Test-InstallIntact 'recovered'
    Assert-That 'uninstall exits 0' ((Invoke-Uninstall) -eq 0)
    Test-Removed 'after the recovery'
    Reset-Scratch

    $script:case = 'interrupted install'
    Write-Host "== $($script:case)"
    Expand-Package (Join-Path $install 'app')
    Assert-That 'no binding before setup' (@(Get-InstallBinding).Count -eq 0)
    $code = Invoke-Setup
    Assert-That 'setup exits 0 over unbound files' ($code -eq 0) "exit $code"
    Test-InstallIntact 'after the interrupted install'
    Assert-That 'uninstall exits 0' ((Invoke-Uninstall) -eq 0)
    Test-Removed 'after the interrupted install'
    Reset-Scratch

    $script:case = 'portable at the install folder'
    Write-Host "== $($script:case)"
    Expand-Package $install
    $before = Get-Snapshot
    $code = Invoke-Setup
    Assert-That 'setup refuses with a non-zero exit' ($code -ne 0) "exit $code"
    Assert-That 'nothing changed' ($before -ceq (Get-Snapshot))
    Assert-That 'no uninstaller' (-not (Test-Path -LiteralPath (Join-Path $install 'unins000.exe')))
    Reset-Scratch

    $script:case = 'portable elsewhere'
    Write-Host "== $($script:case)"
    Expand-Package $portable
    $portableLauncher = Join-Path $portable 'BookSplice.Launcher.exe'
    $code = Invoke-Launcher $portableLauncher @('--register')
    Assert-That 'portable copy registers' ($code -eq 0) "exit $code"
    $portableTarget = Get-ShortcutTarget
    Assert-That 'portable shortcut targets a managed launcher' ($portableTarget -like '*\.booksplice-managed-*\BookSplice.Launcher.exe') $portableTarget
    $code = Invoke-Setup
    Assert-That 'setup without /REPLACESHORTCUT exits 0' ($code -eq 0) "exit $code"
    Test-InstallIntact 'installed beside the portable copy'
    Assert-That 'shortcut left with the portable copy' ([string]::Equals((Get-ShortcutTarget), $portableTarget, 'OrdinalIgnoreCase'))
    Assert-That 'uninstall exits 0' ((Invoke-Uninstall) -eq 0)
    Test-Removed 'first uninstall'
    Assert-That 'portable shortcut survives the uninstall' ([string]::Equals((Get-ShortcutTarget), $portableTarget, 'OrdinalIgnoreCase'))
    $code = Invoke-Setup @('/REPLACESHORTCUT')
    Assert-That 'setup with /REPLACESHORTCUT exits 0' ($code -eq 0) "exit $code"
    Test-InstallIntact 'second install'
    $managed = @(Get-ManagedRoots)
    if ($managed.Count -eq 1) {
        Assert-That 'shortcut taken over by the installed copy' ([string]::Equals((Get-ShortcutTarget), (Join-Path $managed[0].FullName 'BookSplice.Launcher.exe'), 'OrdinalIgnoreCase')) (Get-ShortcutTarget)
    }
    Assert-That 'uninstall exits 0' ((Invoke-Uninstall) -eq 0)
    Test-Removed 'second uninstall'
    Assert-That 'shortcut gone' (-not (Test-Path -LiteralPath $shortcutPath))
    # The portable copy unregisters from its own managed root, as a user would by deleting it.
    foreach ($root in @(Get-ChildItem -LiteralPath $portableHome -Directory -Force -Filter '.booksplice-managed-*')) {
        $code = Invoke-Launcher (Join-Path $root.FullName 'BookSplice.Launcher.exe') @('--unregister')
        Assert-That 'portable copy unregisters' ($code -eq 0) "exit $code"
    }
    Assert-That 'no bindings left' (@(Get-Bindings).Count -eq 0)
}
finally {
    # Always leave the machine as found: uninstall anything still installed, then drop the scratch folders.
    try { [void](Invoke-Uninstall) } catch { Write-Host "cleanup uninstall: $($_.Exception.Message)" }
    if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }
    if ($toastSaved) { Restore-RegistryKey $toastKey $toastBackup }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    foreach ($dir in $scratch) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

$results | Group-Object Case | ForEach-Object {
    $bad = @($_.Group | Where-Object { -not $_.Passed }).Count
    '{0,-34} {1,3} checks, {2}' -f $_.Name, $_.Count, $(if ($bad -eq 0) { 'passed' } else { "$bad FAILED" })
}
if ($script:failed -gt 0) { throw "$($script:failed) installer check(s) failed." }
Write-Host "All $($results.Count) installer checks passed."
