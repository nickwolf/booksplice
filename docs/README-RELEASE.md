# BookSplice {{VERSION}}

BookSplice converts folders of audiobook tracks into validated, chaptered M4B files on Windows.

## Install

1. Keep this extracted folder together, including `tools\ffmpeg`, `licenses`, and `sources`.
2. Run `BookSplice.Gui.exe`.
3. Complete first-run setup and choose an existing output folder.

The package is self-contained and does not require the .NET SDK. Windows may show a SmartScreen warning because this release is not code signed.

## Use

Drop one or more audiobook folders onto the main window, review the detected order and metadata, then convert. Existing M4B files can be inspected but are not conversion inputs in BookSplice {{VERSION}}.

The `booksplice.exe` CLI supports the same conversion engine. Run `booksplice.exe --help` for its options.

BookSplice does not modify source files. It stores settings, job logs, and temporary files under `%LOCALAPPDATA%\BookSplice`. Audit logs contain local paths and metadata, so treat them as private. Review copied diagnostics before sharing because arbitrary identifiers in tool or operating-system messages may remain.

Project documentation, source code, and issue tracking are available at [github.com/nickwolf/booksplice](https://github.com/nickwolf/booksplice).

BookSplice is licensed under the MIT License in `LICENSE`. Third-party terms are listed in `THIRD-PARTY-NOTICES.md` and `licenses`. The corresponding FFmpeg and zlib source archives and rebuild instructions are in `sources`.
