# Metadata profiles

`MetadataAggregator` consumes the ordered `SourceFile.ProbeResult` values already produced during discovery. It does not probe media again.

Values compare after Unicode NFC normalization and collapsed whitespace. The selected value retains its original spelling and records its source file and input key. `Missing` means no source supplied a value, `Consistent` means every source supplied the same normalized value, `PartiallyMissing` means the supplied values agree but at least one source omitted one, and `Conflicting` means supplied values differ. Per-track `TITLE`, `TRACK`, and `DISC` values are not book-level conflict inputs.

Book title selection is a consistent `ALBUM`, then a credible shared `TITLE`, then the source-root name. Author selection prefers `ALBUMARTIST` and then `ARTIST`. Narrator selection uses only `COMPOSER`.

## MP4 representation

The repository ffprobe output represents common iTunes/MP4 atoms with lowercase keys such as `title`, `album`, `artist`, `album_artist`, `composer`, `comment`, `description`, `genre`, `sort_album`, `date`, `media_type`, and `gapless_playback`. It preserves the original spelling of custom keys, including `ASIN`, `SERIES`, `SERIES-PART`, `WWWAUDIOFILE`, `RELEASETIME`, `RATING WMP`, `COPYRIGHT`, `PUBLISHER`, `ISBN`, and `LANGUAGE`.

`ALBUM` is the durable book-title source. `TITLE` may be a chapter or container title. `YEAR` and `RELEASETIME` are distinct. `SERIES` and `SERIES-PART` are optional.

## Profiles

Profiles are public, immutable Core objects with a name and version. A caller can construct another `MetadataProfile` from `MetadataFieldMapping` values without adding FFmpeg-specific behavior to Core.

| Semantic value | GenericMp4 v1 | NickMp3tag v1 |
| --- | --- | --- |
| Book title | `title`, `album` | `TITLE`, `ALBUM` |
| Author | `artist`, `album_artist` | `ARTIST`, `ALBUMARTIST` |
| Narrator | `composer` | `COMPOSER` |
| Series and position | Preserved if present | `SERIES`, `SERIES-PART` |
| Subtitle and album sort | Preserved if present | `SUBTITLE`, `ALBUMSORT` |
| Genre and years | `genre`, `date` | `GENRE`, `YEAR`, `RELEASETIME` |
| Description and rights | `comment`, `description`, `copyright` | `COMMENT`, `DESCRIPTION`, `PUBLISHER`, `COPYRIGHT` |
| Identifiers and media type | Preserved if present | `ASIN`, `WWWAUDIOFILE`, `ISBN`, `LANGUAGE`, `ITUNESMEDIATYPE` |

The local profile also maps `RATING WMP`, `CONTENTGROUP`, `MOVEMENTNAME`, `MOVEMENT`, `ITUNESGAPLESS`, `AUDIBLE_ASIN`, `AUDIBLE_ALBUMARTISTID`, `AUDIBLE_ACR`, `AUDIBLE_LOCALE`, `FORMAT`, `EXPLICIT`, and `RATING` when those values are resolved.

Input tags not mapped by a profile are retained using their original key spelling. A mapped profile value wins only when its output key collides with a preserved input key. Conflicting semantic values are not emitted by a mapping. This is a best-effort metadata rule, not a promise that every container or external tag editor can retain every custom MP4 atom.

`SERIESPART` is an observed compatibility alias. It is not written. `DISCNUMBER` is not part of either profile contract and is not written.

## FFmetadata

`FFmetadataWriter` emits the `;FFMETADATA1` header and escapes `\\`, `=`, `;`, `#`, CR, and LF. A CRLF pair becomes one escaped LF, matching FFmpeg's line-continuation syntax. This writer stays in the FFmpeg project so Core remains format-independent.

## Mp3tag round-trip

Desktop Mp3tag 3.36.1 was opened with a synthetic M4B under disposable APPDATA and LOCALAPPDATA directories. A controlled GUI Ctrl+S preserved its standard `title`, `album`, `artist`, `album_artist`, and `composer` tags, but ffprobe no longer reported the synthetic custom `SERIES`, `SERIES-PART`, `ASIN`, `WWWAUDIOFILE`, `PUBLISHER`, or `ITUNESMEDIATYPE` tags. The attempted isolation also changed the live Mp3tag profile inventory, so the round-trip is not accepted. No original media was opened or written. Custom-field support and audio-essence hash equality remain unverified.
