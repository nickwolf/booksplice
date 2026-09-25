# Release process

BookSplice releases are self-contained Windows x64 ZIP archives. They include the WPF application, CLI, .NET runtime, pinned `ffmpeg.exe` and `ffprobe.exe`, license, notices, README, and version manifest.

## Local verification

```powershell
$toolBuild = Join-Path $PWD ('artifacts\tools\ffmpeg\source-build-' + [guid]::NewGuid().ToString('N'))
.\scripts\Build-MediaTools.ps1 -DestinationDirectory $toolBuild
$toolDirectory = Join-Path $toolBuild 'output'
$fixtureRoot = Join-Path $PWD 'artifacts\tools\fixtures'
.\scripts\Get-MediaTools.ps1 -ManifestPath .\tools\ffmpeg\fixture-generator-manifest.json -DestinationRoot $fixtureRoot
$fixtureRelease = (Get-Content .\tools\ffmpeg\fixture-generator-manifest.json | ConvertFrom-Json).release
$env:BOOKSPLICE_FIXTURE_FFMPEG_DIR = Join-Path $fixtureRoot $fixtureRelease
.\scripts\Get-FFmpegLicenseInventory.ps1 -MediaToolDirectory $toolDirectory -Check

dotnet restore
$env:BOOKSPLICE_FFMPEG_DIR = $toolDirectory
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes --no-restore

.\scripts\Build-Release.ps1 -Version 0.1.0 -MediaToolDirectory $toolDirectory
.\scripts\Test-Release.ps1 -ArchivePath .\artifacts\release\BookSplice-0.1.0-win-x64.zip -ChecksumPath .\artifacts\release\BookSplice-0.1.0-win-x64.zip.sha256
```

`Test-Release.ps1` verifies the checksum, archive structure, required license files, exact pinned FFmpeg license digest, and packaged GUI startup. It then runs the packaged CLI, converts generated audio with full-decode validation, probes the M4B, checks its chapter and audio stream, confirms an audit record exists, and confirms the source hash did not change.

Build the same version twice into different output directories and compare SHA-256 hashes before tagging. A release tag must have a matching `docs/releases/<tag>.md` file.

## Media-tool review

The release build uses FFmpeg source commit `946fcce07b6dcd0331c8cc609192aeff5e1924f8` and zlib 1.3.2. `tools/ffmpeg/manifest.json` pins both source archives, the Debian base and signed package snapshot, the exact configure output, executable hashes, and license hashes. The generated component inventory verifies the pinned binary and configuration. The build disables automatic dependency detection, GPL and nonfree options, and unused components; zlib is the only enabled external library. PE import inspection of both executables found only `bcrypt.dll`, `KERNEL32.dll`, `msvcrt.dll`, and `SHELL32.dll`. The ZIP includes the matching source archives, build recipe, FFmpeg LGPL 2.1 text, and zlib license.

The [FFmpeg legal checklist](https://ffmpeg.org/legal.html) calls for corresponding source, build instructions, and review of external libraries. Those materials are included in the candidate ZIP. Codec patent exposure is not resolved by the copyright license or the executable configuration, so the patent and final third-party review disposition remains a publication gate. The project owner approved proceeding with the 0.1.0 release on 2026-09-25 after this review. This records a publication decision, not a patent clearance or legal opinion. The clean-machine draft-release smoke remains required before publication.

## Private acceptance verification

Run the private release-gate harness with PowerShell 7 against the extracted package after local package verification. The private corpus and raw results stay under the ignored `artifacts\acceptance` directory. Follow `docs/ACCEPTANCE.md` for manifest fields, disposable-copy rules, the another-volume case, and the manual Mp3tag save and reopen.

Run `.\scripts\Test-AcceptanceHarness.ps1` first. It uses generated files only and exercises the harness safeguards without reading the private corpus.

```powershell
pwsh -NoProfile -File .\scripts\Invoke-Acceptance.ps1 -Mode run -Corpus .\artifacts\acceptance\private-manifest.json -Release .\artifacts\release-extracted -ResultPath .\artifacts\acceptance\private-cli-result.json
```

The source-built FFmpeg binaries are a material package change. Earlier private CLI and Mp3tag passes against the BtbN-based candidate do not apply to a source-built candidate. Build a new unique ZIP, verify its digest and source bundle, then rerun the private gates for that exact package. Do not reuse an Mp3tag reopen confirmation unless the saved file is uniquely verified against the new candidate. The codec patent review and clean-machine draft-release smoke remain open.

The CLI result must report `status: passed` and `gateComplete: true`. The later `verify-mp3tag` result must report `status: passed`, `gateComplete: true`, and `privateGatesComplete: true` after reading that CLI result. The ignored raw CLI and Mp3tag results must have identical `releaseFingerprint` and `corpusFingerprint` values. Do not combine evidence from different packages or corpus manifests, and do not tag from a subset run.

## Publication

1. Confirm `main` is clean and matches the reviewed release commit.
2. Run the local gates above.
3. Review the release notes and public repository diff for private paths, media, credentials, and machine-specific data.
4. Complete and record the manual and third-party-license gates in `docs/ACCEPTANCE.md` using disposable copies and the exact pinned media-tool artifact.
5. Create and push an annotated `v<version>` tag.
6. The release workflow rebuilds and tests the package, creates a build-provenance attestation, and attaches the ZIP, checksum, and smoke-test script to an unpublished draft release. Existing same-named assets are never overwritten.
7. On a clean Windows x64 machine, authenticate to GitHub, download the draft assets, run the downloaded script against the ZIP and checksum, and compare the downloaded artifact digest with the workflow result.
8. Publish the draft release only after the clean-machine gate passes.

Do not publish a release from an uncommitted tree. Do not replace a published asset under the same tag.
