# Acceptance status

As of 2026-09-24, the unpublished 0.1.0 acceptance candidate passed the automated suite and all four private release-gate cases with the pinned `autobuild-2026-09-18-13-22` LGPL FFmpeg build. Automated tests use generated media. Private checks use disposable copies and verify the original source file hashes are unchanged.

The Release build currently passes 381 tests with no skips when `BOOKSPLICE_FFMPEG_DIR` points to the pinned tools. The extracted ZIP smoke test starts the packaged GUI, runs the packaged CLI, converts a generated MP3 with full-decode validation, probes the M4B, checks its audio stream and chapter, confirms an audit record, and verifies the source SHA-256 is unchanged. Three isolated builds of this candidate produced byte-identical ZIP archives, and all three checksum files match their archives.

The GUI contract tests cover per-book option parity between preview and conversion, stream-copy preview wording, keyboard access keys, UI Automation names, minimum-window layout, a 1920 by 1080 work area at 200% scaling, and the per-monitor V2 manifest declaration. They do not emulate moving a running window between monitors with different DPI settings.

## Specification matrix

| Category | Current evidence | State |
| --- | --- | --- |
| 1. Normal multi-MP3 audiobook | Generated multi-file CLI conversion and source hashes | Automated pass |
| 2. Long audiobook | Private disposable-copy conversion, full decode, and unchanged source hashes | Private acceptance pass |
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
| 20. Source on another drive | Private disposable copy on another volume; Windows partition mapping confirmed a different physical disk | Private acceptance pass |
| 21. Cancellation | Live FFmpeg process-tree cancellation, audit, cleanup, and source hashes | Automated pass |
| 22. Very long audiobook | Private disposable-copy conversion, full decode, and unchanged source hashes | Private acceptance pass |
| 23. Existing M4B | Inspection-only analysis and conversion rejection; a previously saved and reopened M4B uniquely matched this candidate by decoded audio, cover, and required fields | Private acceptance pass |

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

The prepare command prints the M4B to open and the directory where the operator must save a separate file. Open the prepared file in Mp3tag, save a separate M4B inside the printed save directory, close it, and reopen the saved file. The runner does not automate Mp3tag.

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

The unpublished acceptance candidate passed all three private CLI cases and the Mp3tag verifier. The saved M4B had been manually reopened in Mp3tag for an older package. A read-only check found one unique external file matching this candidate's sealed decoded-audio, cover, and required-field baselines; the same file predated the earlier verifier result and remained unchanged. Its byte-identical copy in the owned save directory passed the current verifier. The anonymous CLI and Mp3tag results are schema-valid with identical release and corpus fingerprints; the complete CLI result reports `gateComplete: true`, and the Mp3tag result reports `privateGatesComplete: true`, preserved audio and cover, verified fields, and unchanged source hashes. The another-drive copy used a different physical disk. This evidence applies only to the exact candidate package and private manifest; it does not establish a pass for the final tagged package.

The third-party binary license inventory for the pinned FFmpeg build is generated at `licenses/FFmpeg-components.md` by `scripts/Get-FFmpegLicenseInventory.ps1`. Its vendor, platform, transitive-dependency, patent, and custom-license review items remain open before publication. Record only anonymous outcomes. Do not commit titles, source paths, media, or command output containing private paths.

The final clean-machine gate uses the unpublished draft GitHub release. On a Windows x64 machine without the development SDK, authenticate to GitHub and download the draft ZIP, checksum, and `Test-Release.ps1` assets. Run the downloaded script against the downloaded ZIP and checksum, compare its digest with the workflow result, then publish the draft only after the gate passes.
