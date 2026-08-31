# Conversion benchmark evidence

This evidence is intentionally anonymous. Raw source locations, source names, hashes, clips, FFmpeg logs, and output files are held in a private workspace outside Git. The tracked CSV has anonymous case IDs only.

## Environment and method

The campaign ran on the local Windows development host with the bundled FFmpeg build reporting version `n9.0.1-11-ge47273f4d9-20260829`. The native `aac` and MediaFoundation `aac_mf` encoders were available. Hardware inventory could not be collected in this restricted session, so host CPU and memory figures are intentionally omitted. Per-process child CPU time is recorded where available; system CPU, memory, and disk counters are not.

Three copied real excerpts were used: 600 seconds of 64 kbps mono MP3, 419 seconds of 96 kbps mono MP3, and 600 seconds of stereo AAC in M4A. A 120 second synthetic 96 kbps MP3 set with 12 short files exercised concatenation. Six hundred seconds was chosen to make process startup a small fraction of the transcode time while keeping the matrix bounded. Each selected original was SHA-256 hashed before copying, its private copy was checked against that hash, and the originals are rechecked after the campaign.

The confirmed traits cover 64 kbps, 96 kbps, mono, stereo, long tracks, AAC/M4A, and a many-short-files supplement. No selection was labeled male, female, music, 128 kbps, or high-bitrate after inspection, so those traits remain exclusions. The CSV does not imply listening-quality conclusions; that work belongs to Task 6.

## Results and recommendation

The quality pass measures native AAC-LC at 48, 56, 64, 72, 80, 96, 128, and 160 kbps on the 600 second 64 kbps mono MP3 case. The planned repetition count is 3 per bitrate. Results are appended and flushed one row at a time. On this source, the lower settings completed substantially faster than 128 and 160 kbps. There is no perceptual conclusion from this timing evidence. Until listening tests are available, 64 kbps AAC-LC is the conservative spoken-word timing candidate, with a configurable higher-quality profile retained for users who need it.

| AAC-LC kbps | Repetitions | Mean wall seconds | Mean realtime factor |
| --- | ---: | ---: | ---: |
| 48 | 3 | 5.48 | 109.57 |
| 56 | 3 | 4.96 | 120.86 |
| 64 | 3 | 5.57 | 109.09 |
| 72 | 3 | 5.70 | 105.24 |
| 80 | 3 | 5.68 | 105.74 |
| 96 | 3 | 6.41 | 93.82 |
| 128 | 3 | 10.66 | 56.46 |
| 160 | 3 | 13.48 | 44.90 |

On the copied AAC/M4A case, compatible stream copy averaged 0.21 seconds and 2,956.90 realtime factor. The forced-transcode control averaged 14.24 seconds and 42.17 realtime factor. These results support stream copy only when compatibility checks permit it.

Compatible AAC stream copy is measured separately from a forced AAC transcode control. Stream copy is the preferred reliable path when container, stream, timestamp, chapter, and metadata requirements are compatible. The runner records stream-copy and forced-transcode rows distinctly rather than treating them as interchangeable.

Lightweight validation probes stream presence, duration tolerance, and non-empty output. Full validation additionally decodes the completed output to null. Both modes are recorded separately. Lightweight validation is the default recommendation pending a fuller failure corpus because it verified every completed row in this bounded pass and avoids an additional full read of the output. Full decode remains available for diagnostic or high-assurance use.

Filter concat and segmented AAC transcode have runner option names reserved but were not measured in this campaign. They must not be represented as benchmarked until their execution implementations and result rows exist.

## Concurrency and storage

This campaign has only measured concurrency 1. Concurrency 2, 3, and 4, storage-source comparison, aggregate throughput, reliable disk and memory counters, and manual responsiveness remain unmeasured. Therefore no automatic concurrency boundary is claimed, and 6 or 8 concurrent jobs were not run. Manual responsiveness is explicitly `not-measured` in every row.

## AudioBookConverter 6.6.8

No installed AudioBookConverter entry was found in the standard program directories, user configuration directories, or Windows uninstall registrations. A portable 6.6.8 copy was not available during this run. Because reproducible noninteractive CLI operation was not established and unattended GUI automation is excluded, the comparison disposition is `not-practical`, not a performance claim. The schema permits a future anonymous completed comparison without changing the public evidence format.

## Limits

This is bounded operational evidence, not a universal encoder ranking. Results can vary with source complexity, cache state, storage topology, and hardware. Missing trait coverage, incomplete concurrency and storage measurements, unavailable ABC comparison, and no listening test are material limitations.
