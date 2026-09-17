# BookSplice

[![CI](https://github.com/nickwolf/booksplice/actions/workflows/ci.yml/badge.svg)](https://github.com/nickwolf/booksplice/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

BookSplice turns folders of audiobook tracks into validated, chaptered M4B files on Windows. It preserves useful metadata and cover art, orders tracks deterministically, and publishes the finished book only after validation succeeds.

BookSplice is currently a source-built command-line application. The WPF GUI project is a scaffold, and packaged releases are not available yet.

## Highlights

- Discovers MP3, AAC, M4A, M4B, FLAC, OGG, Opus, and WMA files recursively without changing the source files.
- Uses track, disc, path, and filename evidence to choose a deterministic order. Ambiguous ordering stops the current CLI; resolve the conflicting evidence before rerunning.
- Creates chapters, carries forward common audiobook and Mp3tag metadata, and selects embedded or external cover art.
- Copies compatible AAC streams when possible and otherwise transcodes to AAC-LC with a pinned LGPL FFmpeg build.
- Validates the output before an atomic publish. Existing files are preserved unless `--overwrite` is supplied.
- Supports dry runs, machine-readable JSON events, cancellation, bounded parallel work, and local audit records.

M4B files are accepted for inspection, but the current planner does not convert an existing M4B file.

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), matching [`global.json`](global.json)
- PowerShell and Git

The repository pins an FFmpeg build in [`tools/ffmpeg/manifest.json`](tools/ffmpeg/manifest.json). The acquisition script verifies its SHA-256 digest, version, and required capabilities before use.

## Quick start

Clone the repository and install the pinned media tools:

```powershell
git clone https://github.com/nickwolf/booksplice.git
cd booksplice

$toolRoot = Join-Path $PWD "artifacts\tools\ffmpeg"
.\scripts\Get-MediaTools.ps1 `
    -ManifestPath .\tools\ffmpeg\manifest.json `
    -DestinationRoot $toolRoot

$release = (Get-Content .\tools\ffmpeg\manifest.json | ConvertFrom-Json).release
$env:BOOKSPLICE_FFMPEG_DIR = Join-Path $toolRoot $release
```

Preview a conversion without writing output:

```powershell
New-Item -ItemType Directory -Force "C:\Audiobooks\Staging" | Out-Null

dotnet run --project .\src\BookSplice.Cli -- `
    "C:\Audiobooks\My Book" `
    --output "C:\Audiobooks\Staging" `
    --dry-run
```

Remove `--dry-run` to perform the conversion. BookSplice writes the resulting M4B to the staging directory and leaves the source tree unchanged.

## Command line

```text
booksplice <source> [--output <directory>] [--quality <profile>] [--bitrate <kbps>] [--jobs <count>] [--chapters | --no-chapters] [--overwrite] [--dry-run] [--json]
```

| Option | Purpose |
| --- | --- |
| `<source>` | One audio file or a directory to scan recursively. |
| `--output <directory>` | Fully qualified, existing output directory. Required by the current CLI. |
| `--quality <profile>` | Select `efficient`, `balanced`, `high-quality`, or `preserve-more`. |
| `--bitrate <kbps>` | Override the profile bitrate with a value from 32 through 320. |
| `--jobs <count>` | Limit simultaneous segment transcodes to a value from 1 through 32. |
| `--chapters`, `--no-chapters` | Override chapter creation for this run. |
| `--overwrite` | Replace the requested output path after validation. The default chooses a collision-free name. |
| `--dry-run` | Analyze and print the planned strategy without creating output, temporary files, or audit records. |
| `--json` | Emit newline-delimited JSON events for automation. Diagnostics remain on standard error. |

Quality profiles use native AAC-LC when transcoding:

| Profile | Bitrate |
| --- | ---: |
| `efficient` | 48 kbps |
| `balanced` | 80 kbps |
| `high-quality` | 96 kbps |
| `preserve-more` | 128 kbps |

`high-quality` is the default. Compatible AAC input may use stream copy instead of the selected transcode profile.

Default validation checks the container, AAC stream, duration, final packet region, chapters, cover, and metadata. Full decode validation is available through application settings but is not yet exposed as a command-line option. The current CLI also does not expose metadata-profile selection.

## Local data

BookSplice stores its local files under `%LOCALAPPDATA%\BookSplice`:

| Path | Contents |
| --- | --- |
| `settings.json` | Application settings. The current CLI reads this file but does not provide a settings command. Command-line overrides are not persisted. |
| `logs\` | Atomic JSON audit records for non-dry-run jobs. They retain local paths, imported and final metadata, selected-cover details, timings, and command evidence. Source paths in embedded diagnostics and command arguments are redacted. Treat these files as private. |
| `temp\` | Per-job temporary files removed after success, failure, or cancellation. |

`BOOKSPLICE_FFMPEG_DIR` can point to a directory containing `ffmpeg.exe` and `ffprobe.exe`. `BOOKSPLICE_LOCAL_APP_DATA` overrides the Local AppData root for isolated runs and tests.

## Exit codes

| Code | Meaning |
| ---: | --- |
| 0 | Success or a completed dry run |
| 1 | Unexpected failure |
| 2 | Command-line usage error |
| 3 | Invalid input or configuration |
| 4 | Source ordering needs a decision; the current CLI cannot select a candidate |
| 5 | Media tool or conversion failure |
| 6 | Output validation failure |
| 7 | Atomic publication failure |
| 8 | Cancellation |

## Development

```powershell
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes --no-restore
```

The test suite creates synthetic fixtures and does not require private audiobook media. Tests that exercise the pinned FFmpeg binaries run when `BOOKSPLICE_FFMPEG_DIR` is set.

The implementation is split into a process-independent Core library, FFmpeg adapters, CLI and GUI adapters, and development tools. See [`docs/PROJECT-DESIGN.md`](docs/PROJECT-DESIGN.md), [`docs/METADATA.md`](docs/METADATA.md), and [`BENCHMARK.md`](BENCHMARK.md) for the design and validation contracts.

## License

BookSplice is available under the [MIT License](LICENSE). Bundled dependency and media-tool terms are documented in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
