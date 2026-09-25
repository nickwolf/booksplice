# Acceptance status

The earlier unpublished `0.1.0-acceptance.4` candidate passed the automated suite and all four private release-gate cases with the pinned `autobuild-2026-09-18-13-22` LGPL FFmpeg build. That evidence applies only to the earlier package and corpus.

The source-built `0.1.0-acceptance.5` candidate passed 381 generated tests with no skips, an extracted-package smoke test, and two byte-identical ZIP builds. It is superseded because its minimal FFmpeg build lacked the raw decoded-audio output required by the Mp3tag verifier. Its single private CLI case passed, but its complete three-case CLI run failed one case after that private corpus entry no longer contained its expected supported files. Neither result transfers to a later package.

The corrected source-built FFmpeg binaries from two independent builds are byte identical and match `tools/ffmpeg/manifest.json`. The `0.1.0-acceptance.6` ZIPs were byte identical and passed the extracted-package smoke test. Its schema-valid single private CLI case passed with full decode and unchanged source hashes. That package is superseded by the chapter-timing fix from local commit `f3ce2d1`; its private result does not transfer.

As of 2026-09-25, the Release suite passes 383 generated tests with no skips using the corrected tool and chapter fix. The chapter regression failed before the fix and passed afterward. Two `0.1.0-acceptance.7` ZIP builds are byte identical, and the generated extracted-package smoke test passes. Formatting, diff, inventory, parser, and privacy checks passed before the chapter fix; formatting and diff checks passed again after it. The new private corpus is schema-valid and its expected source files exist. The exact acceptance.7 package passed its single-case and complete three-case private CLI gates with schema-valid matching 43-character fingerprints, full decode, unchanged source hashes, and zero owned acceptance trees. Its manual Mp3tag save and reopen also passed: the schema-valid result has `privateGatesComplete=true`, matching release and corpus fingerprints, preserved decoded audio, cover, and required fields, unchanged source hashes, and completed owned-tree cleanup.

The GUI contract tests cover per-book option parity between preview and conversion, stream-copy preview wording, keyboard access keys, UI Automation names, minimum-window layout, a 1920 by 1080 work area at 200% scaling, and the per-monitor V2 manifest declaration. They do not emulate moving a running window between monitors with different DPI settings.

## Specification matrix

| Category | Current evidence | State |
| --- | --- | --- |
| 1. Normal multi-MP3 audiobook | Generated multi-file CLI conversion and source hashes | Automated pass |
| 2. Long audiobook | Exact acceptance.7 package: private disposable-copy conversion, full decode, and unchanged source hashes | Current private CLI pass |
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
| 20. Source on another drive | Exact acceptance.7 package: private disposable copy on a verified different physical disk, full decode, and unchanged source hashes | Current private CLI pass |
| 21. Cancellation | Live FFmpeg process-tree cancellation, audit, cleanup, and source hashes | Automated pass |
| 22. Very long audiobook | Exact acceptance.7 package: replacement private source passed its duration floor, full decode, and unchanged source hashes | Current private CLI pass |
| 23. Existing M4B | Exact acceptance.7 package: manual Mp3tag save and reopen with preserved audio, cover, required fields, and source hashes | Current private pass |

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

The earlier `0.1.0-acceptance.4` package passed all three private CLI cases and the Mp3tag verifier with matching fingerprints, preserved media and fields, unchanged source hashes, and a verified different physical disk for the another-drive case. Those results cannot establish a pass for the corrected source-built package. A new private manifest now points to available sources for all four gates, including a replacement very-long book and a covered book for a fresh manual Mp3tag save and reopen. Do not publish private source details or raw results. The exact acceptance.7 package now has a schema-valid complete three-case CLI pass with matching single-case fingerprints, full decode, unchanged source hashes, and zero owned trees. The exact acceptance.7 package also has a schema-valid manual Mp3tag pass with matching 43-character fingerprints, preserved audio, cover, and required fields, unchanged source hashes, and zero owned trees.

The third-party inventory for the source-built FFmpeg is generated at `licenses/FFmpeg-components.md` by `scripts/Get-FFmpegLicenseInventory.ps1`. The package includes the exact FFmpeg and zlib source archives, build recipe, and licenses. The project owner approved proceeding with the 0.1.0 release after review of the unresolved codec patent question; no patent clearance or legal opinion is claimed. Record only anonymous outcomes. Do not commit titles, source paths, media, or command output containing private paths.

The final clean-machine gate uses the unpublished draft GitHub release. On a Windows x64 machine without the development SDK, authenticate to GitHub and download the draft ZIP, checksum, and `Test-Release.ps1` assets. Run the downloaded script against the downloaded ZIP and checksum, compare its digest with the workflow result, then publish the draft only after the gate passes.
