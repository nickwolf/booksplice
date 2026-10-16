<img src="assets/booksplice-icon.svg" alt="BookSplice icon" width="96">

# BookSplice

[![CI](https://github.com/nickwolf/booksplice/actions/workflows/ci.yml/badge.svg)](https://github.com/nickwolf/booksplice/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

BookSplice turns folders of audiobook tracks into validated, chaptered M4B files on Windows. It preserves useful metadata and cover art, resolves playback order before conversion, and publishes the finished book only after validation succeeds.

## Highlights

- Drag folders into the Windows application, review the detected order and metadata, then convert.
- Resolve ambiguous natural-path and track-metadata ordering before conversion can begin.
- Edit book metadata and chapter titles, choose or omit artwork, and preview the output path and audio strategy.
- Copy compatible AAC streams when possible and otherwise transcode once to AAC-LC using one of four quality profiles.
- Validate container structure, duration, chapters, artwork, and metadata before atomic publication. Full-decode validation is optional.
- Queue multiple books with bounded concurrency, progress, cancellation, retry, and independent failure handling.
- Use the same engine from the `booksplice` CLI with dry runs and newline-delimited JSON events.

BookSplice does not modify source files. Existing M4B files can be inspected but are not conversion inputs in version 0.1.1.

## Install on Windows

1. Download `BookSplice-0.1.1-win-x64.zip` and `BookSplice-0.1.1-win-x64.zip.sha256` from the [GitHub release](https://github.com/nickwolf/booksplice/releases/latest).
2. Verify the archive in PowerShell:

```powershell
$expected = (Get-Content .\BookSplice-0.1.1-win-x64.zip.sha256 -Raw).Split()[0]
$actual = (Get-FileHash .\BookSplice-0.1.1-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'BookSplice checksum mismatch.' }
```

3. Extract the ZIP to a folder you can keep, such as `%LOCALAPPDATA%\Programs\BookSplice`.
4. Run `BookSplice.Gui.exe`.

The package is self-contained and does not require the .NET SDK. It includes the pinned LGPL FFmpeg tools. Windows may show a SmartScreen warning because version 0.1.1 is not code signed.

## Updates

Check for updates opens the Updates window, which compares the running version with the stable releases on GitHub. Each release carries a signed update manifest, and BookSplice installs only a ZIP that matches the manifest and verifies against the public key built into the app. Prereleases and drafts are ignored. A daily check is on by default and can be turned off in the same window.

Install and restart is available only while the queue is empty, because the app restarts and the queue is not saved. BookSplice downloads and verifies the release, unpacks it into a new folder next to the current one, checks that the new version starts, and then restarts into it. If the new version does not report that it started within 60 seconds, the previous version is started again and the Updates window reports the failure. Roll back to previous version switches back to the version that was running before the last update, with the same empty-queue rule. Settings are not changed by an update or a rollback.

A build from before the updater existed cannot update itself. Replace it once by hand with the first release that includes the updater. After that, updates are applied from the Updates window.

The first update creates a managed folder named `.booksplice-managed-<id>` beside the folder you extracted, holding a small launcher (`BookSplice.Launcher.exe`), the installed versions, and the update records. The folder you extracted is never modified or deleted, so it stays as a manual fallback. BookSplice adds a Start Menu shortcut named `BookSplice` that starts the launcher. If a shortcut with that name already exists and BookSplice did not create it, it is left alone and the Updates window shows the launcher path to start instead. Update preferences are stored in `%LOCALAPPDATA%\BookSplice\updates`.

## First launch and settings

First launch asks where completed M4B files should go, which audio quality profile to use, and whether to create chapters from source-file boundaries. Advanced settings expose channel handling, parallel jobs, metadata profile, lightweight or full-decode validation, and logging level.

Each queued book starts with the saved settings. The **Output** tab can override the destination, audio quality, channel handling, metadata profile, and validation level for the selected book without changing the saved defaults.

Settings are stored in `%LOCALAPPDATA%\BookSplice\settings.json`. BookSplice does not silently replace a corrupt settings file or a file from a newer schema version.

The quality profiles use native AAC-LC when transcoding:

| Profile | Bitrate | Intended use |
| --- | ---: | --- |
| Efficient | 48 kbps | Smaller spoken-word files |
| Balanced | 80 kbps | General audiobook listening |
| High quality | 96 kbps | Higher-quality listening; default |
| Preserve more | 128 kbps | More conservative compression |

Compatible AAC input can use stream copy when channel handling preserves the source. Forcing mono or stereo uses AAC-LC transcoding.

## GUI workflow

1. Drop one or more audiobook folders onto the main window, or use **Add folder**.
2. Select a queued book and review its file count, duration, codec summary, metadata, chapters, and cover.
3. If BookSplice finds more than one credible order, inspect each complete track list and choose one. Conversion remains disabled until an order is selected.
4. Edit metadata or chapter titles, select different artwork, or choose not to embed artwork.
5. Open **Output** to review or change that book's conversion settings, then use **Preview output** to check the planned output filename and selected audio strategy. Stream-copy previews do not present the unused transcode bitrate.
6. Convert the selected book or all ready books. The result card shows whether conversion completed, completed with warnings, was cancelled, or failed, along with the published output path and elapsed time. It names the lightweight checks and full audio decode separately. Warnings remain visible; expand **Validation checks** or **Diagnostics** for details. **Open output folder** is available when the published file exists. Use **Copy diagnostics** to copy a support summary and tool output with recognized local paths redacted.

During conversion, the queue names the active operation, such as reanalyzing sources, encoding audio, assembling audio, checking output, validating full audio, or publishing output. The percentage and **Phase remaining** estimate apply only to the current measurable phase and can reset when the phase changes. An operation without a reliable measurement shows **Numeric progress unavailable** and an indeterminate bar. The estimate needs at least 3 seconds of advancing samples from a recent 30-second window; it disappears when the last numeric advance is more than 10 seconds old. After 30 seconds without an operation report, the queue says how long it has been since an update. If reports continue but an expected measurement does not advance for 30 seconds, it says that instead. These are activity advisories, not timeouts: they do not cancel conversion or decide whether validation passed. Cancel remains available while work is active; cleanup or an atomic publication step may finish before the request settles. The result card, including its validation evidence and warnings, remains the final outcome.

Changing source membership or playback order after review stops conversion and requires analysis again. Closing the application while work is active is blocked until cancellation and cleanup finish.

The Windows application declares per-monitor V2 DPI awareness and exposes keyboard access keys and UI Automation names for routine controls. Its 760 by 520 minimum window fits a 1920 by 1080 work area at 200% scaling.

## Command line

```text
booksplice <source> [options]
  --output <folder>                 Existing output folder or saved setting
  --quality <profile>               efficient, balanced, high-quality, preserve-more
  --bitrate <32-320>                Custom AAC bitrate in kbps
  --jobs <1-32>                     Parallel segment encoders
  --order <natural|metadata>        Explicit source ordering candidate
  --metadata-profile <name>         Metadata profile ID (see below)
  --validation <lightweight|full>   Output validation level
  --channels <preserve|mono|stereo> Output channel handling
  --chapters | --no-chapters        Chapter creation
  --overwrite                       Replace an existing output after validation
  --dry-run                         Plan without output, temporary files, or audit records
  --json                            Newline-delimited JSON; never prompts
```

An interactive terminal prompts when ordering needs a decision. Redirected input and JSON mode never prompt. Command-line overrides apply to that run and are not saved.

`booksplice --help` lists the metadata profiles that are installed, and any profile files that were skipped with the reason.

Exit codes are 0 for success, 1 for unexpected failure, 2 for usage error, 3 for invalid input or configuration, 4 for an unresolved ordering decision, 5 for conversion failure, 6 for validation failure, 7 for publication failure, and 8 for cancellation.

With `--json`, each line is a schema version 1 `started`, `progress`, or `final` event. A `progress` event retains the execution fields `displayPercent`, `speed`, `stageIndex`, `sourceIndex`, and `state`. These fields keep their execution meaning and can be `null` for service phases. An execution `state` of `completed` or `displayPercent` of 100 does not establish that validation or publication completed. The additive fields are `phase`, `phasePercent`, `phaseSpeed`, `measurementScope` (`"phase"`), `jobId`, `sequence`, `phaseOccurrenceId`, `expectsMeasurableUpdates`, `completedSources`, and `totalSources`. Phase names are `Analysis`, `Planning`, `Preparation`, `Encoding`, `StreamCopy`, `Assembly`, `MetadataWriting`, `ValidationChecks`, `FullDecode`, `Publication`, `Cleanup`, and `AuditWriting`; only phases reached by a job appear. `phasePercent` and `phaseSpeed` can be `null` when the current phase has no reliable numeric evidence. `jobId` identifies one conversion attempt, `sequence` increases across its accepted operation reports, and `phaseOccurrenceId` distinguishes entries into phases. A `progress` event can also carry `advisory` (`noUpdate`, `noAdvance`, or `none`), `lastReportAgeSeconds`, `lastAdvanceAgeSeconds`, and `observationElapsedSeconds`. Advisory timer events reuse the latest operation identity and sequence; they do not count as new operation reports. Consumers should accept extra fields and nullable measurements. The `final` event retains its status, exit code, job ID, and published path and is the authoritative outcome; no progress event follows it.

## Local data and privacy

BookSplice stores local state under `%LOCALAPPDATA%\BookSplice`:

| Path | Contents |
| --- | --- |
| `settings.json` | Persistent GUI and CLI defaults |
| `logs\` | Atomic JSON audit records for non-dry-run jobs |
| `temp\` | Per-job temporary files removed after success, failure, or cancellation |

Audit records contain local source paths, imported and final metadata, artwork selection, timings, and command evidence. Treat them as private. Public diagnostics redact recognized source paths and command arguments. Review the summary before sharing because arbitrary identifiers in tool or operating-system messages may remain.

`BOOKSPLICE_FFMPEG_DIR` can point to a directory containing `ffmpeg.exe` and `ffprobe.exe`. `BOOKSPLICE_LOCAL_APP_DATA` overrides the Local AppData root for isolated runs and tests.

## Build from source

The repository requires the .NET 10 SDK selected by [`global.json`](global.json), PowerShell, Git, and Docker with Linux containers for the pinned FFmpeg source build. Use a fresh build directory for each run. The broad BtbN binary is used only to generate MP3 test fixtures and is not included in the release package.

```powershell
$toolBuild = Join-Path $PWD ('artifacts\tools\ffmpeg\source-build-' + [guid]::NewGuid().ToString('N'))
.\scripts\Build-MediaTools.ps1 -DestinationDirectory $toolBuild
$env:BOOKSPLICE_FFMPEG_DIR = Join-Path $toolBuild 'output'
$fixtureRoot = Join-Path $PWD 'artifacts\tools\fixtures'
.\scripts\Get-MediaTools.ps1 -ManifestPath .\tools\ffmpeg\fixture-generator-manifest.json -DestinationRoot $fixtureRoot
$fixtureRelease = (Get-Content .\tools\ffmpeg\fixture-generator-manifest.json | ConvertFrom-Json).release
$env:BOOKSPLICE_FIXTURE_FFMPEG_DIR = Join-Path $fixtureRoot $fixtureRelease

dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes --no-restore
```

The tests use generated fixtures and disposable directories. They do not require private audiobook media. See [`ARCHITECTURE.md`](ARCHITECTURE.md), [`docs/METADATA.md`](docs/METADATA.md), [`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md), [`BENCHMARK.md`](BENCHMARK.md), and [`docs/RELEASE.md`](docs/RELEASE.md) for the implementation and release contracts.

## License

BookSplice is available under the [MIT License](LICENSE). Bundled dependency and media-tool terms are documented in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
