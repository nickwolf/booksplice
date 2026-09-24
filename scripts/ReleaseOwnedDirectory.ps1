Set-StrictMode -Version Latest

function Assert-ReleaseNoReparse([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release directory has a reparse-point ancestor.' }
        $parent = [IO.Path]::GetDirectoryName($item.FullName.TrimEnd('\', '/'))
        if ([string]::IsNullOrWhiteSpace($parent) -or [string]::Equals($parent, $item.FullName, [StringComparison]::OrdinalIgnoreCase)) { break }
        $item = Get-Item -LiteralPath $parent -Force
    }
}

function New-ReleaseOwnedDirectory([string]$Parent, [string]$Leaf) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    Assert-ReleaseNoReparse $parentFull
    if ([IO.Path]::GetFileName($Leaf) -ne $Leaf -or $Leaf -in @('.', '..')) { throw 'Invalid release owned-directory leaf.' }
    $owned = [IO.Path]::GetFullPath((Join-Path $parentFull $Leaf))
    if (Test-Path -LiteralPath $owned) { throw 'Release owned-directory target already exists.' }
    New-Item -ItemType Directory -Path $owned | Out-Null
    $token = [guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $owned '.booksplice-release-owner'), $token, [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ Parent = $parentFull; Path = $owned; Leaf = $Leaf; Token = $token }
}

function Remove-ReleaseOwnedDirectory($Owned) {
    $parentFull = [IO.Path]::GetFullPath([string]$Owned.Parent).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath([string]$Owned.Path).TrimEnd('\', '/')
    $expected = [IO.Path]::GetFullPath((Join-Path $parentFull ([string]$Owned.Leaf))).TrimEnd('\', '/')
    if (-not [string]::Equals($target, $expected, [StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith($parentFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release cleanup target escaped its exact owned parent.'
    }
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { return }
    Assert-ReleaseNoReparse $target
    if (@(Get-ChildItem -LiteralPath $target -Recurse -Force -Attributes ReparsePoint).Count -ne 0) { throw 'Release cleanup refused a reparse point.' }
    $marker = Join-Path $target '.booksplice-release-owner'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        -not [string]::Equals([IO.File]::ReadAllText($marker).Trim(), [string]$Owned.Token, [StringComparison]::Ordinal)) {
        throw 'Release cleanup refused a missing or mismatched ownership token.'
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}
