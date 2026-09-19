# Acceptance status

On 2026-09-18, the automated 0.1.0 release-candidate checks passed with the pinned `autobuild-2026-09-18-13-22` LGPL FFmpeg build. The automated suite uses generated media and disposable directories. It does not read or modify a private audiobook library.

The Release build currently passes 366 tests with no skips when `BOOKSPLICE_FFMPEG_DIR` points to the pinned tools. The extracted ZIP smoke test starts the packaged GUI, runs the packaged CLI, converts a generated MP3 with full-decode validation, probes the M4B, checks its audio stream and chapter, confirms an audit record, and verifies the source SHA-256 is unchanged. Two local package builds using the same source and pinned tool directory produced the same ZIP digest.

## Specification matrix

| Category | Current evidence | State |
| --- | --- | --- |
| 1. Normal multi-MP3 audiobook | Generated multi-file CLI conversion and source hashes | Automated pass |
| 2. Long audiobook | Recorded 600-second copied-media benchmark and duration validation | Full copied-book confirmation pending |
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
| 20. Source on another drive | No private or secondary-drive corpus was used | Manual confirmation pending |
| 21. Cancellation | Live FFmpeg process-tree cancellation, audit, cleanup, and source hashes | Automated pass |
| 22. Very long audiobook | Duration arithmetic and bounded copied-media benchmark evidence | Full copied-book confirmation pending |
| 23. Existing M4B | Inspection-only analysis and conversion rejection | Automated pass; Mp3tag round-trip pending |

## Manual release gates

Private real-world acceptance remains incomplete. Before publishing 0.1.0, use disposable copies to confirm a representative long book, a very long book, a source on another physical drive, and an M4B open/save cycle in Mp3tag. Complete the third-party binary license inventory for the pinned FFmpeg build. Record only anonymous outcomes. Do not commit titles, source paths, media, or command output containing private paths.

The final clean-machine gate uses the unpublished draft GitHub release. On a Windows x64 machine without the development SDK, authenticate to GitHub and download the draft ZIP, checksum, and `Test-Release.ps1` assets. Run the downloaded script against the downloaded ZIP and checksum, compare its digest with the workflow result, then publish the draft only after the gate passes.
