# Acceptance status

The earlier unpublished `0.1.0-acceptance.4` candidate passed the automated suite and all four private release-gate cases with the pinned `autobuild-2026-09-18-13-22` LGPL FFmpeg build. That evidence applies only to the earlier package and corpus.

The source-built `0.1.0-acceptance.5` candidate passed 381 generated tests with no skips, an extracted-package smoke test, and two byte-identical ZIP builds. It is superseded because its minimal FFmpeg build lacked the raw decoded-audio output required by the Mp3tag verifier. Its single private CLI case passed, but its complete three-case CLI run failed one case after that private corpus entry no longer contained its expected supported files. Neither result transfers to a later package.

The corrected source-built FFmpeg binaries from two independent builds are byte identical and match `tools/ffmpeg/manifest.json`. The `0.1.0-acceptance.6` ZIPs were byte identical and passed the extracted-package smoke test. Its schema-valid single private CLI case passed with full decode and unchanged source hashes. That package is superseded by the chapter-timing fix from local commit `f3ce2d1`; its private result does not transfer.

As of 2026-09-25, the Release suite passes 383 generated tests with no skips using the corrected tool and chapter fix. The chapter regression failed before the fix and passed afterward. Two `0.1.0-acceptance.7` ZIP builds are byte identical, and the generated extracted-package smoke test passes. Formatting, diff, inventory, parser, and privacy checks passed before the chapter fix; formatting and diff checks passed again after it. The new private corpus is schema-valid and its expected source files exist. The exact acceptance.7 package passed its single-case and complete three-case private CLI gates with schema-valid matching 43-character fingerprints, full decode, unchanged source hashes, and zero owned acceptance trees. Its manual Mp3tag save and reopen also passed: the schema-valid result has `privateGatesComplete=true`, matching release and corpus fingerprints, preserved decoded audio, cover, and required fields, unchanged source hashes, and completed owned-tree cleanup.

The local `0.1.0` release candidate was built twice into separate directories with byte-identical ZIPs (SHA-256 `571a85d5dc807496f3312728e508f4b5fe8d16b9727abe25bed4c6e2542f7a72`). Its extracted-package smoke test and exact-package private CLI and Mp3tag gates passed on 2026-10-02. The unpublished draft-release ZIP has a different SHA-256 (`591c1cb45ebb7c7774fcee0d155c13fed581694486192d7d1fbff1494b822a2d`), so those private results cannot authorize it. The draft ZIP's own single-case and complete three-case CLI results are schema-valid, have matching 43-character release and corpus fingerprints, and report full decode, audit, duration, and unchanged source hashes for all three cases. Its manual Mp3tag save and reopen passed on 2026-10-03 with a schema-valid `privateGatesComplete=true` result, matching draft CLI fingerprints, preserved decoded audio, cover, required fields, and source hashes, and zero owned trees. The downloaded draft assets passed the clean-machine smoke on 2026-10-03 in GitHub-hosted Windows 2022 run `37129308832`: preinstalled .NET SDKs were removed and verified absent, all four draft asset digests matched, and the downloaded `Test-Release.ps1` passed its WPF and CLI checks. The ZIP SHA-256 matched the release workflow attestation. The release remains unpublished. This draft is superseded because the public build must omit a personal metadata profile. Its private and clean-machine results apply only to that draft ZIP; a replacement package requires fresh gates.

The profile-free `v0.1.1` release workflow passed in run `37151248692` and created a separate unpublished four-asset draft. The downloaded ZIP has SHA-256 `9c7237d6b2487e11f7f2d9dcb017ecdb7123e769be2de8f84edd3be43b98b73c`; all four downloaded asset digests matched the draft release API. GitHub build-provenance verification bound the ZIP digest to the tagged merge commit and release workflow run. The packaged CLI lists `GenericMp4` and does not list the personal profile. GitHub-hosted Windows 2022 run `37151830432` removed the runner's installed .NET SDKs, verified their absence, downloaded and checked the exact draft assets, and passed the downloaded WPF and CLI smoke script. The generated suite passed 383 tests with no skips, and the acceptance harness safety suite passed 16 checks. The exact `v0.1.1` ZIP did not complete the private three-case CLI gate or a fresh manual Mp3tag save and reopen. Those real-media results from `v0.1.0` do not transfer to this package. On 2026-10-03, the project owner chose to proceed with public release based on the generated suite, package verification, and exact-draft clean-machine smoke while leaving those private-media checks unverified. This decision does not establish `privateGatesComplete=true` or codec patent clearance.

The GUI contract tests cover per-book option parity between preview and conversion, stream-copy preview wording, strategy explanations and storage estimates, keyboard access keys, UI Automation names, minimum-window layout, a 1920 by 1080 work area at 200% scaling, and the per-monitor V2 manifest declaration. They do not emulate moving a running window between monitors with different DPI settings.

## Specification matrix

| Category | Current evidence | State |
| --- | --- | --- |
| 1. Normal multi-MP3 audiobook | Generated multi-file CLI conversion and source hashes | Automated pass |
| 2. Long audiobook | Exact draft `0.1.0` ZIP: private disposable-copy conversion, full decode, and unchanged source hashes | Current private CLI pass |
| 3. Many short MP3 tracks | Generated multi-source strategy and chapter tests | Automated pass |
| 4. Many-track audiobook | Segmented strategy selection and generated-media FFmpeg execution | Automated pass |
| 5. Mixed MP3 bitrates | Stream-copy rejection and AAC transcode planning | Automated pass |
| 6. Mono audiobook | Generated forced-mono conversion with full validation | Automated pass |
| 7. Stereo audiobook | Generated forced-stereo conversion with full validation | Automated pass |
| 8. AAC/M4A eligible for stream copy | Generated multi-M4A CLI conversion and audit evidence | Automated pass |
| 9. Inconsistent metadata | Metadata conflict aggregation and GUI editing tests | Automated pass |
| 10. Incomplete metadata | Missing and partially missing metadata tests | Automated pass |
| 11. Embedded artwork | Generated GUI conversion and payload validation | Automated pass |
| 12. External folder artwork | Generated CLI conversion with external JPEG | Automated pass |
| 13. Multiple artwork candidates | Deterministic cover ranking and selection tests | Automated pass |
| 14. No artwork | Generated AAC conversion without artwork | Automated pass |
| 15. Corrupt file | CLI failure without final publication | Automated pass |
| 16. Ambiguous ordering | CLI and GUI decision-required workflows | Automated pass |
| 17. Existing destination file | Collision-safe repeated conversion and overwrite fault tests | Automated pass |
| 18. Path containing spaces | Generated CLI conversion from a spaced path | Automated pass |
| 19. Unicode filenames | Generated CLI and FFmpeg integration paths | Automated pass |
| 20. Source on another drive | Exact draft `0.1.0` ZIP: private disposable copy on a verified different physical disk, full decode, and unchanged source hashes | Current private CLI pass |
| 21. Cancellation | Live FFmpeg process-tree cancellation, audit, cleanup, and source hashes | Automated pass |
| 22. Very long audiobook | Exact draft `0.1.0` ZIP: replacement private source passed its duration floor, full decode, and unchanged source hashes | Current private CLI pass |
| 23. Existing M4B | Exact draft `0.1.0` ZIP: manual Mp3tag save and reopen preserved audio, cover, required fields, and source hashes | Current private pass |

## Private release-gate runner

`scripts/Invoke-Acceptance.ps1` requires PowerShell 7 and an extracted release package. It covers the four private gates that remain after the generated acceptance suite: a representative long book, a very long book, a disposable source copy on another volume, and a manual Mp3tag save and reopen. It does not claim broader GUI coverage.

Keep the private manifest, state, and raw results under `artifacts\acceptance`. Those names are ignored by Git. The runner accepts only anonymous case IDs such as `case-01`, hashes the original before copying, verifies the copy, runs BookSplice only against a GUID-named disposable directory, rehashes the original and copy after conversion, and removes only directories carrying the matching ownership token. It rejects overlapping paths and reparse points. The prepare step binds the state bytes and exact owned-directory layout to an integrity record inside the owned run tree. Verification refuses untrusted or moved state without cleaning any referenced tree.

A full manifest contains exactly one CLI case for each of `long-audiobook`, `very-long-audiobook`, and `another-drive`, plus one `mp3tag-roundtrip` case. Every case requires `expectedOrder`, whose entries are paths relative to the copied source root. Use the full relative path, such as `disc-1\track-01.mp3`, so repeated leaf filenames in different directories remain unambiguous. The long-audiobook case must set `minimumAudioDurationSeconds` to at least 3600, and the very-long-audiobook case must set it to at least 21600. These values are acceptance thresholds. Do not record the measured duration of private media in the manifest, result, or tracked documentation. The another-drive case must set `copyRoot` to a pre-existing empty dedicated directory on the second volume and `sourceDriveClass` to `other-volume`. The runner proves a distinct volume root; the operator remains responsible for confirming that the selected volume satisfies the physical-drive test. Use the private schema at `benchmarks/acceptance-corpus.schema.json`. Empty Mp3tag field values require the field to exist; nonempty values require an exact match.

```powershell
$release = Resolve-Path .\artifacts\release-extracted
$corpus = Resolve-Path .\artifacts\acceptance\private-manifest.json
$cliResult = Join-Path $PWD 'artifacts\acceptance\private-cli-result.json'

pwsh -NoProfile -File .\scripts\Invoke-Acceptance.ps1 `
  -Mode run `
  -Corpus $corpus `
  -Release $release `
  -ResultPath $cliResult
```

A successful full CLI run sets `gateComplete` to `true` and leaves `privateGatesComplete` as `false` until Mp3tag verification passes. A run with `-CaseId` is useful while preparing a corpus, but it cannot complete the gate.

```powershell
$state = Join-Path $PWD 'artifacts\acceptance\mp3tag-state.json'

pwsh -NoProfile -File .\scripts\Invoke-Acceptance.ps1 `
  -Mode prepare-mp3tag `
  -Corpus $corpus `
  -Release $release `
  -StatePath $state `
  -CaseId case-04
```

The prepare command prints the prepared M4B and its owned save directory. Copy the prepared M4B to a unique file in that directory before opening Mp3tag. Open only that disposable copy in Mp3tag, make a metadata-only change outside the required fields, save it in place, close it, and reopen that same copy. Leave the prepared baseline untouched. The runner does not automate Mp3tag.

```powershell
pwsh -NoProfile -File .\scripts\Invoke-Acceptance.ps1 `
  -Mode verify-mp3tag `
  -Corpus $corpus `
  -Release $release `
  -StatePath $state `
  -CaseId case-04 `
  -Mp3tagOutputPath <saved-m4b-path> `
  -CliResultPath $cliResult `
  -ResultPath .\artifacts\acceptance\mp3tag-result.json `
  -ConfirmedReopened
```

Verification requires the four core mapped fields, any additional fields declared by the private manifest, identical full decoded-audio and cover-payload SHA-256 values, unchanged original and copied source hashes, the earlier full-decode audit evidence, and the operator's explicit reopen confirmation. Preparation seals decoded-audio and cover hashes into the integrity-bound state. Verification recomputes both the prepared and saved files and requires each to match those baselines. It cleans the owned disposable tree after either a pass or a failed comparison, but preserves an untrusted state and its referenced trees for safe diagnosis. The anonymous result follows `benchmarks/acceptance-results.schema.json`; inspect it before copying any approved outcome into tracked documentation. The `corpusFingerprint` binds the manifest bytes and deterministic source-file SHA-256 inventories. The raw CLI and Mp3tag results must contain identical `releaseFingerprint` and `corpusFingerprint` values before they can be treated as evidence from the same release and corpus.

## Manual release gates

The earlier `0.1.0-acceptance.4`, `0.1.0-acceptance.7`, and local `0.1.0` packages passed their private CLI and Mp3tag gates, but their evidence does not transfer to the byte-different draft ZIP. The exact `v0.1.0` draft ZIP has its own schema-valid complete three-case CLI pass, with matching 43-character fingerprints across its single-case and full results, full decode, unchanged source hashes, and zero owned trees after the CLI run. Its manual Mp3tag result is schema-valid with `privateGatesComplete=true`, matching draft CLI fingerprints, preserved audio, cover, required fields, and source hashes, and completed owned-tree cleanup. The exact downloaded `v0.1.0` draft assets passed the SDK-free Windows 2022 smoke in run `37129308832`; the release remains unpublished. Do not publish private source details or raw results.

The third-party inventory for the source-built FFmpeg is generated at `licenses/FFmpeg-components.md` by `scripts/Get-FFmpegLicenseInventory.ps1`. The package includes the exact FFmpeg and zlib source archives, build recipe, and licenses. The project owner approved proceeding with the 0.1.0 release after review of the unresolved codec patent question; no patent clearance or legal opinion is claimed. Record only anonymous outcomes. Do not commit titles, source paths, media, or command output containing private paths.

The final clean-machine gate uses the unpublished draft GitHub release. On a Windows x64 machine without the development SDK, authenticate to GitHub and download the draft ZIP, checksum, `Test-Release.ps1`, and `ReleaseOwnedDirectory.ps1` assets into one directory. Run the downloaded script against the downloaded ZIP and checksum, then compare its digest with the release workflow result. The 2026-10-03 hosted Windows 2022 runs `37129308832` and `37151830432` passed this gate for the exact `v0.1.0` and `v0.1.1` drafts, respectively, after removing and verifying the absence of preinstalled .NET SDKs. The `v0.1.1` private CLI and Mp3tag gates were not completed for that exact ZIP; the owner accepted that coverage gap for publication.

## Updater evidence (issue #77)

Evidence for the updater on branch `codex/updater`, recorded 2026-10-22. Every package below is a `Test` flavor build signed with a disposable key. None of it covers a production-signed package.

| Gate | Evidence | State |
| --- | --- | --- |
| Generated test suite | `dotnet test BookSplice.slnx -c Release --no-build -m:1` with both FFmpeg directories set: 800 passed, 0 skipped (Core 162, Gui 101, Acceptance 2, Cli 70, Updates 335, FFmpeg 130), twice in a row. `-m:1` runs the test projects one at a time: the updater opens every ancestor of the folders it guards with read-only sharing, and while it does, other test projects running in parallel fail to rename files in the shared temporary folder (12 FFmpeg, 4 GUI and 4 updater failures in the first hosted CI run) | Local automated pass |
| Formatting | `dotnet format BookSplice.slnx --verify-no-changes --no-restore` exit 0 | Local automated pass |
| Acceptance harness and update tooling | `Test-AcceptanceHarness.ps1` and `Test-UpdateTooling.ps1` exit 0 | Local automated pass |
| Test package | `Build-Release.ps1 -Version 0.2.0 -Flavor Test` and `Test-Release.ps1` exit 0, ZIP SHA-256 `037949858c7b7387b77f05f0239f0ba6f986dd6368604aa6e415f0e83d464629` | Local automated pass |
| Packaged update rehearsal | `Test-UpdateRehearsal.ps1` on an interactive Windows desktop: 6 of 6 scenarios passed (upgrade, rollback, no-ready, launcher-killed, bad-release, cleanup), no stray processes, real user state unchanged. It built its own `Test` packages 0.9.0 (`17465e9501712539d86f963de19aa71e6c87ffba9c9ff94a2ff9ec0d98e27646`) and 0.9.1 (`d00c54f24d71b68d343eb5c147f35ab22ca4bb3cc44fad2b76c8e9b546544017`) | Run locally 2026-10-22, not in CI |
| Production-key package | `Build-Release.ps1 -Version 0.2.0 -Flavor Release` and `Test-Release.ps1` exit 0 on 2026-10-22, ZIP SHA-256 `c6fd7f300217e8f6c2d3c7051279f818157d2a39b94b99168f9e644c47b83724`, `VERSION.txt` reports `Build flavor: Release`. Manifest signing with the production key runs only in the release workflow | Local build pass, signing not yet run |
| Hosted CI on the branch | Branch not pushed | Not yet run |
| Clean-machine run of the signed draft | Needs a published draft with a production signature | Not yet run |
| Manual DPI, keyboard and screen-reader checks of the Updates window | Automation names and access keys are set in XAML; nobody has used the window with those tools | Not yet run |
