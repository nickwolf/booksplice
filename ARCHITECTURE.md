# Architecture

BookSplice separates audiobook analysis and conversion from its Windows and command-line interfaces.

- `BookSplice.Core` owns discovery contracts, deterministic ordering, metadata aggregation, cover ranking, chapter planning, settings, immutable conversion plans, and conversion orchestration. It does not start processes or depend on WPF.
- `BookSplice.FFmpeg` owns media probing, FFmpeg command construction, process-tree cancellation, metadata writing, output validation, and embedded-artwork extraction.
- `BookSplice.Gui` provides first-run setup, persistent settings, drag-and-drop analysis, ordering review, metadata and chapter editing, cover selection, output preview, and a bounded conversion queue.
- `BookSplice.Cli` exposes the same services for terminal and automation use. JSON mode never prompts.
- `BookSplice.Updates` owns release selection, signed-manifest verification, safe archive extraction, the managed installation layout and its ownership records, the update journal, and the launcher-side handover and recovery logic. It has no WPF dependency.
- `BookSplice.Launcher` is the stable launcher. It lives in the managed folder and is the only thing the Start Menu shortcut starts. It resolves which installed version to run, applies a staged update, and falls back to the previous version.

The main pipeline is:

```text
discover -> probe -> resolve order -> aggregate -> plan -> convert -> validate -> publish
```

Source paths are read only. A conversion writes into a per-job directory under `%LOCALAPPDATA%\BookSplice\temp`, validates that temporary output, then atomically publishes it to the configured output directory. Cancellation and failures clean controlled temporary files and never expose a partial file as a completed book.

An update uses close-then-launch, so only one GUI writes settings or runs jobs at a time. The GUI holds update admission, rechecks that the queue is empty, downloads, verifies and stages the release, runs the candidate's self-check, and starts the launcher in apply mode before exiting. The launcher waits for that exact GUI process to exit, starts the candidate, and waits up to 60 seconds for an authenticated Ready message over a current-user named pipe. Ready is sent only after the main window has rendered, settings were read without writing, and the bundled tools initialized. Only then does the launcher write the new current and previous pointers and send Commit. If Ready does not arrive, the launcher stops the candidate, leaves the pointers alone, and starts the version they name. Recovery after a crash finalizes the journal under update admission and launches whatever the pointers name. See `docs/planning/specs/2026-10-15-booksplice-updater-design.md` for the full contract.

The release includes a pinned LGPL FFmpeg build. `tools/ffmpeg/manifest.json` records its source, version, variant, and SHA-256 digest. The acquisition script rejects version drift and missing capabilities. Release ZIP entries use fixed timestamps and sorted paths. Two local builds using the pinned toolchain produced the same ZIP digest; cross-machine reproducibility has not been established.
