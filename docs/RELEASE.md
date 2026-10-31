# Release process

BookSplice releases are self-contained Windows x64 ZIP archives. They include the WPF application, CLI, .NET runtime, pinned `ffmpeg.exe` and `ffprobe.exe`, license, notices, README, and version manifest.

CI is the gate. A release is a tag, the draft that `release.yml` builds from it, release notes, and publishing. The only manual check is the [updater rehearsal](#updater-rehearsal), and only when a release changes the updater or launcher. The steps are under [Publication](#publication).

## What CI checks

`ci.yml` runs on every pull request and on `main`. It builds the pinned FFmpeg from source, runs the tests with `-m:1`, checks formatting, runs `Test-Release.ps1` on a `Test` package, runs `Test-UpdateTooling.ps1`, and scans for secrets with gitleaks. A release is cut only from a `main` commit with a green CI run.

`release.yml` runs on the tag. It rebuilds the pinned FFmpeg, reruns the tests and format check, builds the `Release` package, runs `Test-Release.ps1`, signs and verifies the update manifest, creates the build-provenance attestation, builds and verifies `SHA256SUMS`, and creates the draft.

## Reproduce CI locally (optional)

Use this block to chase a CI failure or to check a change before pushing. A release does not require it, and there is no local double build or hash comparison.

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
dotnet test --configuration Release --no-build -m:1
dotnet format --verify-no-changes --no-restore

.\scripts\Build-Release.ps1 -Version 0.2.1 -MediaToolDirectory $toolDirectory
.\scripts\Test-Release.ps1 -ArchivePath .\artifacts\release\BookSplice-0.2.1-win-x64.zip -ChecksumPath .\artifacts\release\BookSplice-0.2.1-win-x64.zip.sha256
```

`Test-Release.ps1` verifies the checksum, archive structure, required license files, exact pinned FFmpeg license digest, and packaged GUI startup. It then runs the packaged CLI, converts generated audio with full-decode validation, probes the M4B, checks its chapter and audio stream, confirms an audit record exists, and confirms the source hash did not change.

## Updater packaging and signing

`Build-Release.ps1 -Flavor` selects the build flavor, and the ZIP's `VERSION.txt` records it. The default is `Test`, which embeds no production trust: such a package can run the updater against a disposable key but can never install a production release. `Release` embeds the public key from `src\BookSplice.Updates\Trust\production-public-key.pem` and fails if that file is missing. The production public key was added on 2026-10-22; its SubjectPublicKeyInfo SHA-256 is `8eff20ea18367ce3309deb850051fca376f600112da2af803e6e154020b89b32`. The matching private key lives only in the `release-signing` environment secret and the owner's password manager. A `Release` updater also accepts a `Test` flavor line in `VERSION.txt`, because the manifest signature is the guard.

The release workflow runs the `publish` job in the `release-signing` environment and reads the private key from the `BOOKSPLICE_UPDATE_SIGNING_KEY` secret. It stops before building if the secret or the public key file is absent. After `Test-Release.ps1` it runs `scripts\booksplice\New-UpdateManifest.ps1` to write `BookSplice-<version>-win-x64.update.json` and `.update.sig.json`, then `scripts\booksplice\Test-UpdateManifest.ps1` to verify them against the archive and the public key. Both files are attached to the draft release.

After signing, the workflow writes a `SHA256SUMS` file that lists the SHA-256 of every other asset, recomputes each hash to check it, and requires the asset set to be exactly the expected nine files before it creates the draft. The updater needs only the ZIP, its checksum, and the two signed update files, and ignores other assets.

Tags must be plain `vX.Y.Z`. The workflow trigger and the manifest version check reject prerelease suffixes, so a prerelease cannot be signed or offered as an update.

`scripts\booksplice\Test-UpdateTooling.ps1` checks the manifest scripts and signing helpers with a throwaway key, including refusal when key material is missing. It needs no desktop and runs in CI.

`scripts\booksplice\Test-UpdateRehearsal.ps1 -MediaToolDirectory <dir> -OutputDirectory <new dir under artifacts>` builds two `Test` packages, signs the newer one with a disposable key, and drives the real GUI and launcher through upgrade, rollback, a candidate that never reports ready, a killed launcher, rejected releases, and cleanup. It needs an interactive Windows desktop, so hosted CI cannot run it. It does not exercise the production key or a real GitHub release.

### Updater rehearsal

`Test-UpdateRehearsal.ps1` is required only when a release changes the updater or launcher code: `src\BookSplice.Updates`, `src\BookSplice.Launcher`, or `src\BookSplice.Gui\Updates`. Run it on an interactive desktop and put its result in a short line in the release body. A broken updater can't be repaired by a later update.

## Media-tool review

This review applies when the pinned FFmpeg or zlib changes. Other releases reuse it.

The release build uses FFmpeg source commit `946fcce07b6dcd0331c8cc609192aeff5e1924f8` and zlib 1.3.2. `tools/ffmpeg/manifest.json` pins both source archives, the Debian base and signed package snapshot, the exact configure output, executable hashes, and license hashes. The generated component inventory verifies the pinned binary and configuration. The build disables automatic dependency detection, GPL and nonfree options, and unused components; zlib is the only enabled external library. PE import inspection of both executables found only `bcrypt.dll`, `KERNEL32.dll`, `msvcrt.dll`, and `SHELL32.dll`. The ZIP includes the matching source archives, build recipe, FFmpeg LGPL 2.1 text, and zlib license.

The [FFmpeg legal checklist](https://ffmpeg.org/legal.html) calls for corresponding source, build instructions, and review of external libraries. Those materials are included in the candidate ZIP. Codec patent exposure is not resolved by the copyright license or the executable configuration, so the patent and final third-party review disposition remains a publication gate. The project owner approved proceeding with the 0.1.0 release on 2026-09-25 after this review. This records a publication decision, not a patent clearance or legal opinion.

## Publication

1. Merge to `main` through a pull request with CI green.
2. Create and push an annotated plain `v<version>` tag on that commit.
3. `release.yml` rebuilds and retests the package, signs and verifies the update manifest, creates a build-provenance attestation, builds `SHA256SUMS`, and attaches the ZIP, checksum, signed update manifest and signature, `SHA256SUMS`, the smoke-test and manifest-check scripts, and their helpers to an unpublished draft release. Existing same-named assets are never overwritten.
   If the tag-triggered run fails before creating a draft, fix the workflow on `main` and dispatch `release.yml` with the existing annotated tag. The retry checks out that tag and verifies its object and commit before building. Do not move the tag or overwrite draft assets.
4. Write the release notes in the draft body. The in-app updater shows the published release body as the release notes, so they must be written before publishing. The `docs/releases` directory holds the notes for v0.1.0 through v0.2.1 as history; later releases keep notes only in the release body.
5. If the release changes the updater or launcher, run the [updater rehearsal](#updater-rehearsal) and add its result to the release body.
6. Publish the draft, then close the version's milestone.

Do not publish a release from an uncommitted tree. Do not replace a published asset under the same tag.
