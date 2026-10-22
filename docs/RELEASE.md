# Release process

The unpublished v0.1.0 draft is superseded by a public-profile scope change. Keep its tag and assets intact, and do not publish it. The profile-free `v0.1.1` replacement was built under a new tag. Its exact draft package passed the clean-machine smoke; its private real-media gates were not completed, as recorded in `docs/ACCEPTANCE.md`.

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

.\scripts\Build-Release.ps1 -Version 0.1.1 -MediaToolDirectory $toolDirectory
.\scripts\Test-Release.ps1 -ArchivePath .\artifacts\release\BookSplice-0.1.1-win-x64.zip -ChecksumPath .\artifacts\release\BookSplice-0.1.1-win-x64.zip.sha256
```

`Test-Release.ps1` verifies the checksum, archive structure, required license files, exact pinned FFmpeg license digest, and packaged GUI startup. It then runs the packaged CLI, converts generated audio with full-decode validation, probes the M4B, checks its chapter and audio stream, confirms an audit record exists, and confirms the source hash did not change.

Build the same version twice into different output directories and compare SHA-256 hashes before tagging. A release tag must have a matching `docs/releases/<tag>.md` file.

## Updater packaging and signing

`Build-Release.ps1 -Flavor` selects the build flavor, and the ZIP's `VERSION.txt` records it. The default is `Test`, which embeds no production trust: such a package can run the updater against a disposable key but can never install a production release. `Release` embeds the public key from `src\BookSplice.Updates\Trust\production-public-key.pem` and fails if that file is missing. The reviewed production key is not in the repository yet, so no `Release` package can be built until it is added. A `Release` updater also accepts a `Test` flavor line in `VERSION.txt`, because the manifest signature is the guard.

The release workflow runs the `publish` job in the `release-signing` environment and reads the private key from the `BOOKSPLICE_UPDATE_SIGNING_KEY` secret. It stops before building if the secret or the public key file is absent. After `Test-Release.ps1` it runs `scripts\booksplice\New-UpdateManifest.ps1` to write `BookSplice-<version>-win-x64.update.json` and `.update.sig.json`, then `scripts\booksplice\Test-UpdateManifest.ps1` to verify them against the archive and the public key. Both files are attached to the draft release.

Tags must be plain `vX.Y.Z`. The workflow trigger and the manifest version check reject prerelease suffixes, so a prerelease cannot be signed or offered as an update.

`scripts\booksplice\Test-UpdateTooling.ps1` checks the manifest scripts and signing helpers with a throwaway key, including refusal when key material is missing. It needs no desktop and runs in CI.

`scripts\booksplice\Test-UpdateRehearsal.ps1 -MediaToolDirectory <dir> -OutputDirectory <new dir under artifacts>` builds two `Test` packages, signs the newer one with a disposable key, and drives the real GUI and launcher through upgrade, rollback, a candidate that never reports ready, a killed launcher, rejected releases, and cleanup. It needs an interactive Windows desktop, so hosted CI cannot run it. Run it locally before a release that changes the updater and keep the aggregate JSON it prints as evidence. It does not exercise the production key or a real GitHub release.

## Media-tool review

The release build uses FFmpeg source commit `946fcce07b6dcd0331c8cc609192aeff5e1924f8` and zlib 1.3.2. `tools/ffmpeg/manifest.json` pins both source archives, the Debian base and signed package snapshot, the exact configure output, executable hashes, and license hashes. The generated component inventory verifies the pinned binary and configuration. The build disables automatic dependency detection, GPL and nonfree options, and unused components; zlib is the only enabled external library. PE import inspection of both executables found only `bcrypt.dll`, `KERNEL32.dll`, `msvcrt.dll`, and `SHELL32.dll`. The ZIP includes the matching source archives, build recipe, FFmpeg LGPL 2.1 text, and zlib license.

The [FFmpeg legal checklist](https://ffmpeg.org/legal.html) calls for corresponding source, build instructions, and review of external libraries. Those materials are included in the candidate ZIP. Codec patent exposure is not resolved by the copyright license or the executable configuration, so the patent and final third-party review disposition remains a publication gate. The project owner approved proceeding with the 0.1.0 release on 2026-09-25 after this review. This records a publication decision, not a patent clearance or legal opinion. The clean-machine draft-release smoke remains required before publication.

## Private acceptance verification

Run the private release-gate harness with PowerShell 7 against the extracted package after local package verification. The private corpus and raw results stay under the ignored `artifacts\acceptance` directory. Follow `docs/ACCEPTANCE.md` for manifest fields, disposable-copy rules, the another-volume case, and the manual Mp3tag save and reopen.

Run `.\scripts\Test-AcceptanceHarness.ps1` first. It uses generated files only and exercises the harness safeguards without reading the private corpus.

```powershell
pwsh -NoProfile -File .\scripts\Invoke-Acceptance.ps1 -Mode run -Corpus .\artifacts\acceptance\private-manifest.json -Release .\artifacts\release-extracted -ResultPath .\artifacts\acceptance\private-cli-result.json
```

The source-built FFmpeg binaries are a material package change. Earlier private CLI and Mp3tag passes against the BtbN-based candidate do not apply to a source-built candidate. Build a new unique ZIP, verify its digest and source bundle, then normally rerun the private gates for that exact package. Do not reuse an Mp3tag reopen confirmation unless the saved file is uniquely verified against the new candidate. For `v0.1.1`, the owner accepted release without a complete new private CLI gate or fresh Mp3tag save and reopen after the exact draft passed the generated suite and SDK-free Windows smoke. Record these as unverified, not passed. Codec patent clearance was not established.

The CLI result must report `status: passed` and `gateComplete: true`. The later `verify-mp3tag` result must report `status: passed`, `gateComplete: true`, and `privateGatesComplete: true` after reading that CLI result. The ignored raw CLI and Mp3tag results must have identical `releaseFingerprint` and `corpusFingerprint` values. Do not combine evidence from different packages or corpus manifests, or describe a subset run as a completed full gate.

## Publication

1. Confirm `main` is clean and matches the reviewed release commit.
2. Run the local gates above.
3. Review the release notes and public repository diff for private paths, media, credentials, and machine-specific data.
4. Complete and record the manual and third-party-license gates in `docs/ACCEPTANCE.md` using disposable copies and the exact pinned media-tool artifact. If the owner explicitly accepts an unrun private gate, record the missing coverage and decision without claiming a pass.
5. Create and push an annotated `v<version>` tag.
6. The release workflow rebuilds and tests the package, creates a build-provenance attestation, and attaches the ZIP, checksum, signed update manifest and signature, the smoke-test and manifest-check scripts, and their helpers to an unpublished draft release. Existing same-named assets are never overwritten.
   If the tag-triggered run fails before creating a draft, fix the workflow on `main` and dispatch `release.yml` with the existing annotated tag. The retry checks out that tag and verifies its object and commit before building. Do not move the tag or overwrite draft assets.
7. On a clean Windows x64 machine, authenticate to GitHub, download the draft assets into one directory, run the downloaded script against the ZIP and checksum, and compare the downloaded artifact digest with the workflow result.
8. Publish the draft release only after the clean-machine gate passes and the disposition of every other release gate is recorded.

Do not publish a release from an uncommitted tree. Do not replace a published asset under the same tag.
