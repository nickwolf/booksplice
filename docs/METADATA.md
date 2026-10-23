# Metadata profiles

`MetadataAggregator` consumes the ordered `SourceFile.ProbeResult` values already produced during discovery. It does not probe media again.

Values compare after Unicode NFC normalization and collapsed whitespace. The selected value retains its original spelling and records its source file and input key. `Missing` means no source supplied a value, `Consistent` means every source supplied the same normalized value, `PartiallyMissing` means the supplied values agree but at least one source omitted one, and `Conflicting` means supplied values differ. Per-track `TITLE`, `TRACK`, and `DISC` values are not book-level conflict inputs.

Book title selection is a consistent `ALBUM`, then a credible shared `TITLE`, then the source-root name. Author selection prefers `ALBUMARTIST` and then `ARTIST`. Narrator selection uses only `COMPOSER`.

## MP4 representation

The repository ffprobe output represents common iTunes/MP4 atoms with lowercase keys such as `title`, `album`, `artist`, `album_artist`, `composer`, `comment`, `description`, `genre`, `sort_album`, `date`, `media_type`, and `gapless_playback`. It preserves the original spelling of custom keys, including `ASIN`, `SERIES`, `SERIES-PART`, `WWWAUDIOFILE`, `RELEASETIME`, `RATING WMP`, `COPYRIGHT`, `PUBLISHER`, `ISBN`, and `LANGUAGE`.

`ALBUM` is the durable book-title source. `TITLE` may be a chapter or container title. `YEAR` and `RELEASETIME` are distinct. `SERIES` and `SERIES-PART` are optional.

## Profiles

Profiles are public, immutable Core objects with a name and version. A caller can construct another `MetadataProfile` from `MetadataFieldMapping` values without adding FFmpeg-specific behavior to Core.

| Semantic value | GenericMp4 v1 |
| --- | --- |
| Book title | title, album |
| Author | artist, album_artist |
| Narrator | composer |
| Genre and year | genre, date |
| Description and rights | comment, description, copyright |
| Language and media type | language, media_type |

Input tags not mapped by a profile are retained using their original key spelling. A mapped profile value wins only when its output key collides with a preserved input key. Conflicting semantic values are not emitted by a mapping. This is a best-effort metadata rule, not a promise that every container or external tag editor can retain every custom MP4 atom.

`SERIESPART` is an observed compatibility alias. It is not written. `DISCNUMBER` is not part of either profile contract and is not written.

## Local profile files

BookSplice reads each `*.json` file in `%LOCALAPPDATA%\BookSplice\profiles\` once at startup, one profile per file. `BOOKSPLICE_LOCAL_APP_DATA` moves that root the same way it moves `settings.json`. Subfolders are not read. Loaded profiles are listed after `GenericMp4` in first-launch settings, the Settings window, and each book's output controls, and `--metadata-profile` accepts their names. `booksplice --help` prints the profile list, the folder, and the reason each skipped file was rejected. Files added while BookSplice is running are picked up on the next start.

```json
{
  "schemaVersion": 1,
  "name": "MyProfile",
  "version": "1",
  "mappings": [
    { "field": "BookTitle", "keys": ["TITLE", "ALBUM"] },
    { "field": "Author", "keys": ["ARTIST", "ALBUMARTIST"] },
    { "field": "Series", "keys": ["SERIES"] },
    { "field": "SeriesPart", "keys": ["SERIES-PART"] }
  ]
}
```

`name` is the profile ID: 1 to 64 letters, digits, dots, underscores, or hyphens. It must not match `GenericMp4` or another file's name, ignoring case. When two files use the same name, both are skipped. `version` is free text up to 32 characters and is recorded in the job audit log. `field` must be one of `BookTitle`, `Author`, `Narrator`, `Subtitle`, `AlbumSort`, `Series`, `SeriesPart`, `Genre`, `Year`, `ReleaseTime`, `Comment`, `Description`, `Publisher`, `Copyright`, `Asin`, `AudioFileUrl`, `Isbn`, `Language`, `ItunesMediaType`, `RatingWmp`, `ContentGroup`, `MovementName`, `Movement`, `ItunesGapless`, `AudibleAsin`, `AudibleAlbumArtistId`, `AudibleAcr`, `AudibleLocale`, `Format`, `Explicit`, `Rating`, `Track`, or `Disc`, spelled exactly. Each field may appear once. Each key is 1 to 64 characters with no control characters or leading or trailing spaces, and may appear once per profile ignoring case. `ALBUMARTIST` and `album_artist` count as the same key because both write the MP4 album-artist atom.

A file is skipped when it is not valid JSON, repeats a property, has a property not shown above, is larger than 64 KiB, or is a link or reparse point. A skipped file does not affect the other profiles. Unmapped input tags are preserved as described above, so a profile only lists the fields it writes.

`TITLE`, `ALBUM`, `ARTIST`, `ALBUMARTIST`, and `COMPOSER` are written as native MP4 fields and every other key as a `com.apple.iTunes` freeform atom. Full validation compares the output against the plan's profile. Because ffprobe reports the album-artist atom as `album_artist`, validation reads `ALBUMARTIST` under that name for every profile.

Each job keeps the profile it was planned with, so editing or removing a file never changes a job that is already running. If the profile saved in `settings.json` is missing at startup, the GUI keeps the file unchanged and opens settings to choose another profile. The CLI exits with code 3 and lists the available profiles. A valid `--metadata-profile` still works in that case. Public diagnostics print `Custom` instead of a local profile's name. Profile files live outside the install folder, so updates leave them in place, and release packages never contain them.

## FFmetadata

`FFmetadataWriter` emits the `;FFMETADATA1` header and escapes `\\`, `=`, `;`, `#`, CR, and LF. A CRLF pair becomes one escaped LF, matching FFmpeg's line-continuation syntax. This writer stays in the FFmpeg project so Core remains format-independent.

## Mp3tag round-trip

The original FFmpeg mdta save-through did not preserve the required custom fields. That result motivated the TagLibSharp 2.3.0 adapter. The adapter writes `TITLE`, `ALBUM`, `ARTIST`, `ALBUMARTIST`, and `COMPOSER` through native MP4 fields and writes `SERIES`, `SERIES-PART`, `ASIN`, `WWWAUDIOFILE`, `PUBLISHER`, and `ITUNESMEDIATYPE` as `com.apple.iTunes` freeform atoms. The standard fields and all six required freeform fields are structurally verified by the adapter tests and ffprobe inspection, and adapter tagging preserves the AAC audio-essence hash exactly.

A real Mp3tag save-through of adapter output is deliberately deferred to private manual acceptance issue #23. Loading, Audible matching, and saving are part of the normal user GUI workflow. GUI automation is not used because the earlier attempt modified profile state. This documentation does not claim that the adapter output has survived an Mp3tag save.
