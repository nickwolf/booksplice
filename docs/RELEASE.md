# Release process

BookSplice releases are self-contained Windows x64 ZIP archives. They include the WPF application, CLI, .NET runtime, pinned `ffmpeg.exe` and `ffprobe.exe`, license, notices, README, and version manifest.

## Local verification

```powershell
$toolRoot = Join-Path $PWD 'artifacts\tools\ffmpeg'
.\scripts\Get-MediaTools.ps1 -ManifestPath .\tools\ffmpeg\manifest.json -DestinationRoot $toolRoot
$toolRelease = (Get-Content .\tools\ffmpeg\manifest.json | ConvertFrom-Json).release
$toolDirectory = Join-Path $toolRoot $toolRelease
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
