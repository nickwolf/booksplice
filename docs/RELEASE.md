# Release process

The unpublished v0.1.0 draft is superseded by the profile-free `v0.1.1`. Keep the v0.1.0 tag and assets intact, and do not publish that draft.

BookSplice releases are self-contained Windows x64 ZIP archives. They include the WPF application, CLI, .NET runtime, pinned `ffmpeg.exe` and `ffprobe.exe`, license, notices, README, and version manifest.

CI is the gate. A normal release needs no local build and no extra evidence file. The steps are under [Publication](#publication). Every release gets the [real-book check](#real-book-check), and the two conditional gates are under [Updater rehearsal](#updater-rehearsal) and [Private acceptance verification](#private-acceptance-verification).

## What CI checks

`ci.yml` runs on every pull request and on `main`. It builds the pinned FFmpeg from source, runs the tests with `-m:1`, checks formatting, runs `Test-Release.ps1` on a `Test` package, runs `Test-UpdateTooling.ps1` and `Test-AcceptanceHarness.ps1`, and scans for secrets with gitleaks. A release is cut only from a `main` commit with a green CI run.

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

`Test-UpdateRehearsal.ps1` is required only when a release changes the updater, launcher, packaging or install layout: `src\BookSplice.Updates`, `src\BookSplice.Launcher`, `src\BookSplice.Gui\Updates`, `scripts\Build-Release.ps1`, or `release.yml`. Run it on an interactive desktop and put its result in a short line in the release body.

## Media-tool review

The release build uses FFmpeg source commit `946fcce07b6dcd0331c8cc609192aeff5e1924f8` and zlib 1.3.2. `tools/ffmpeg/manifest.json` pins both source archives, the Debian base and signed package snapshot, the exact configure output, executable hashes, and license hashes. The generated component inventory verifies the pinned binary and configuration. The build disables automatic dependency detection, GPL and nonfree options, and unused components; zlib is the only enabled external library. PE import inspection of both executables found only `bcrypt.dll`, `KERNEL32.dll`, `msvcrt.dll`, and `SHELL32.dll`. The ZIP includes the matching source archives, build recipe, FFmpeg LGPL 2.1 text, and zlib license.

The [FFmpeg legal checklist](https://ffmpeg.org/legal.html) calls for corresponding source, build instructions, and review of external libraries. Those materials are included in the candidate ZIP. Codec patent exposure is not resolved by the copyright license or the executable configuration, so the patent and final third-party review disposition remains a publication gate. The project owner approved proceeding with the 0.1.0 release on 2026-09-25 after this review. This records a publication decision, not a patent clearance or legal opinion. The draft check on a Windows machine remains required before publication.

## Real-book check

Every release gets one real multi-file book converted with the CLI from the extracted draft package, with full validation and a disposable copy of the source. Point `BOOKSPLICE_LOCAL_APP_DATA` at a scratch folder so the run doesn't add to the real history, and copy in any metadata profile the check uses. Then probe the output with the packaged `ffprobe.exe`:

- the tag fields the metadata profile maps are present under the profile's keys wherever the source had a value
- there is one chapter per source file, starting at each file's boundary, unless the release changes chapter rules
- the embedded cover is byte-identical to the chosen cover image (`ffmpeg -map 0:v -c copy -f image2pipe`)
- the output duration matches the sum of the source durations to within a second
- the audit record says `Succeeded` and the copied source hashes are unchanged

Put the result in a short line in the release body. Keep the book private; it never goes into the repository, tests or notes beyond its length.

## Private acceptance verification

These gates are required only when a release changes how the output file is written: metadata mapping or tag writing, FFmpeg command lines or muxing, chapter or cover embedding, `AtomicPublisher`, or the pinned media tools. Choosing which files, chapters or covers go into a book, or where the output is placed, is covered by the generated tests and the real-book check. Other releases skip these gates. The manual Mp3tag save and reopen checks that a tag editor's rewrite of the file keeps audio, cover and fields, which depends only on how the file is laid out. When one runs, put its result in a short line in the release body; `docs/ACCEPTANCE.md` is not extended per release.

Run the private release-gate harness with PowerShell 7 against the extracted package from the draft. The private corpus and raw results stay under the ignored `artifacts\acceptance` directory. Follow `docs/ACCEPTANCE.md` for manifest fields, disposable-copy rules, the another-volume case, and the manual Mp3tag save and reopen.

CI already runs `.\scripts\Test-AcceptanceHarness.ps1`. It uses generated files only and exercises the harness safeguards without reading the private corpus.

```powershell
pwsh -NoProfile -File .\scripts\Invoke-Acceptance.ps1 -Mode run -Corpus .\artifacts\acceptance\private-manifest.json -Release .\artifacts\release-extracted -ResultPath .\artifacts\acceptance\private-cli-result.json
```

The source-built FFmpeg binaries are a material package change. Earlier private CLI and Mp3tag passes against the BtbN-based candidate do not apply to a source-built candidate. Rerun the private gates for the exact package being released. Do not reuse an Mp3tag reopen confirmation unless the saved file is uniquely verified against the new candidate. If the owner accepts release without a complete gate, record the missing coverage as unverified, not passed. Codec patent clearance was not established.

The CLI result must report `status: passed` and `gateComplete: true`. The later `verify-mp3tag` result must report `status: passed`, `gateComplete: true`, and `privateGatesComplete: true` after reading that CLI result. The ignored raw CLI and Mp3tag results must have identical `releaseFingerprint` and `corpusFingerprint` values. Do not combine evidence from different packages or corpus manifests, or describe a subset run as a completed full gate.

## Publication

1. Merge to `main` through a pull request with CI green, and confirm `main` is clean and matches the reviewed release commit. Review the public repository diff for private paths, media, credentials, and machine-specific data.
2. Create and push an annotated plain `v<version>` tag.
3. `release.yml` rebuilds and retests the package, signs and verifies the update manifest, creates a build-provenance attestation, builds `SHA256SUMS`, and attaches the ZIP, checksum, signed update manifest and signature, `SHA256SUMS`, the smoke-test and manifest-check scripts, and their helpers to an unpublished draft release. Existing same-named assets are never overwritten.
   If the tag-triggered run fails before creating a draft, fix the workflow on `main` and dispatch `release.yml` with the existing annotated tag. The retry checks out that tag and verifies its object and commit before building. Do not move the tag or overwrite draft assets.
4. Write the release notes in the draft body. The draft starts with a short generic body, and the in-app updater shows the published release body as the release notes, so they must be written before publishing. The `docs/releases` directory holds the notes for v0.1.0 through v0.2.1 as history; later releases keep notes only in the release body.
5. Run the [real-book check](#real-book-check) and the conditional gates the release needs: the [updater rehearsal](#updater-rehearsal) and the [private acceptance verification](#private-acceptance-verification). Add a short line to the release body for each one that ran.
6. On a Windows x64 machine, download the draft assets into one directory and run the downloaded `Test-Release.ps1` against the ZIP and checksum, then the downloaded `Test-UpdateManifest.ps1` against the ZIP and the signed manifest and signature. Compare the ZIP digest with `SHA256SUMS` and the workflow result.
7. Publish the draft, then close the version's milestone.

Do not publish a release from an uncommitted tree. Do not replace a published asset under the same tag.
