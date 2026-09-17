# Project design

BookSplice is a Windows application and command-line tool that converts supported local audiobook inputs into validated M4B files for a user-selected staging directory.

## Architecture

```text
Core: domain records and pure planning
FFmpeg: media probing, process execution, metadata emission, and validation
CLI and GUI: adapters over the shared application services
Benchmarks and fixture generator: repeatable development tools
Generated fixtures and tests: verification without source media
```

`Core` owns the domain model and has no dependency on process execution or presentation projects.

## Dependency boundaries

- `Core` contains domain records and planning logic.
- `FFmpeg` references `Core` and contains media-tool integration.
- `CLI` and `GUI` reference `Core` and `FFmpeg`.
- Benchmarks reference `Core` and `FFmpeg`.
- Tests reference only the production projects they exercise.

## Processing model

```text
Discovered -> Probed -> NeedsDecision or Ready -> Converting -> Validating -> Publishing -> Complete
```

Sources are read-only. Conversion plans are immutable once execution begins. A failed or cancelled conversion does not publish a completed file.

## Metadata model

A conversion plan carries normalized book metadata, source provenance, chapter data, a selected cover candidate, output settings, and validation policy. The default public profile maps common semantic fields to standard MP4 metadata. Optional compatibility profiles are data-driven and are verified before they become part of a supported contract.

## Privacy

The application accepts user-selected paths and stores local settings outside source control. Documentation, generated fixtures, benchmarks, diagnostics, and repository defaults must not contain personal paths, credentials, private media, or library-specific metadata.
