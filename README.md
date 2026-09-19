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

BookSplice does not modify source files. Existing M4B files can be inspected but are not conversion inputs in version 0.1.0.

## Install on Windows

1. Download `BookSplice-0.1.0-win-x64.zip` and `BookSplice-0.1.0-win-x64.zip.sha256` from the GitHub release.
2. Verify the archive in PowerShell:

```powershell
$expected = (Get-Content .\BookSplice-0.1.0-win-x64.zip.sha256 -Raw).Split()[0]
$actual = (Get-FileHash .\BookSplice-0.1.0-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'BookSplice checksum mismatch.' }
```

3. Extract the ZIP to a folder you can keep, such as `%LOCALAPPDATA%\Programs\BookSplice`.
4. Run `BookSplice.Gui.exe`.

The package is self-contained and does not require the .NET SDK. It includes the pinned LGPL FFmpeg tools. Windows may show a SmartScreen warning because version 0.1.0 is not code signed.

## First launch and settings

First launch asks where completed M4B files should go, which audio quality profile to use, and whether to create chapters from source-file boundaries. Advanced settings expose channel handling, parallel jobs, metadata profile, lightweight or full-decode validation, and logging level.

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
5. Use **Preview output** to check the destination and selected audio strategy.
6. Convert the selected book or all ready books. A completed item exposes its published output folder. Use **Copy diagnostics** to copy a support summary with recognized local paths redacted.

Changing source membership or playback order after review stops conversion and requires analysis again. Closing the application while work is active is blocked until cancellation and cleanup finish.

## Command line

```text
booksplice <source> [options]
  --output <folder>                 Existing output folder or saved setting
  --quality <profile>               efficient, balanced, high-quality, preserve-more
  --bitrate <32-320>                Custom AAC bitrate in kbps
  --jobs <1-32>                     Parallel segment encoders
  --order <natural|metadata>        Explicit source ordering candidate
  --metadata-profile <name>         GenericMp4 or NickMp3tag
  --validation <lightweight|full>   Output validation level
  --channels <preserve|mono|stereo> Output channel handling
  --chapters | --no-chapters        Chapter creation
  --overwrite                       Replace an existing output after validation
  --dry-run                         Plan without output, temporary files, or audit records
  --json                            Newline-delimited JSON; never prompts
```

An interactive terminal prompts when ordering needs a decision. Redirected input and JSON mode never prompt. Command-line overrides apply to that run and are not saved.

Exit codes are 0 for success, 1 for unexpected failure, 2 for usage error, 3 for invalid input or configuration, 4 for an unresolved ordering decision, 5 for conversion failure, 6 for validation failure, 7 for publication failure, and 8 for cancellation.

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

The repository requires the .NET 10 SDK selected by [`global.json`](global.json), PowerShell, and Git.

```powershell
$toolRoot = Join-Path $PWD 'artifacts\tools\ffmpeg'
.\scripts\Get-MediaTools.ps1 -ManifestPath .\tools\ffmpeg\manifest.json -DestinationRoot $toolRoot
$release = (Get-Content .\tools\ffmpeg\manifest.json | ConvertFrom-Json).release
$env:BOOKSPLICE_FFMPEG_DIR = Join-Path $toolRoot $release

dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes --no-restore
```

The tests use generated fixtures and disposable directories. They do not require private audiobook media. See [`ARCHITECTURE.md`](ARCHITECTURE.md), [`docs/METADATA.md`](docs/METADATA.md), [`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md), [`BENCHMARK.md`](BENCHMARK.md), and [`docs/RELEASE.md`](docs/RELEASE.md) for the implementation and release contracts.

## License

BookSplice is available under the [MIT License](LICENSE). Bundled dependency and media-tool terms are documented in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
