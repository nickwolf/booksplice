# Architecture

BookSplice separates audiobook analysis and conversion from its Windows and command-line interfaces.

- `BookSplice.Core` owns discovery contracts, deterministic ordering, metadata aggregation, cover ranking, chapter planning, settings, immutable conversion plans, and conversion orchestration. It does not start processes or depend on WPF.
- `BookSplice.FFmpeg` owns media probing, FFmpeg command construction, process-tree cancellation, metadata writing, output validation, and embedded-artwork extraction.
- `BookSplice.Gui` provides first-run setup, persistent settings, drag-and-drop analysis, ordering review, metadata and chapter editing, cover selection, output preview, and a bounded conversion queue.
- `BookSplice.Cli` exposes the same services for terminal and automation use. JSON mode never prompts.

The main pipeline is:

```text
discover -> probe -> resolve order -> aggregate -> plan -> convert -> validate -> publish
```

Source paths are read only. A conversion writes into a per-job directory under `%LOCALAPPDATA%\BookSplice\temp`, validates that temporary output, then atomically publishes it to the configured output directory. Cancellation and failures clean controlled temporary files and never expose a partial file as a completed book.

The release includes a pinned LGPL FFmpeg build. `tools/ffmpeg/manifest.json` records its source, version, variant, and SHA-256 digest. The acquisition script rejects version drift and missing capabilities. Release ZIP entries use fixed timestamps and sorted paths. Two local builds using the pinned toolchain produced the same ZIP digest; cross-machine reproducibility has not been established.
