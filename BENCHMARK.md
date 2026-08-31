# Conversion benchmark evidence

This evidence is intentionally anonymous. Raw source locations, source names, hashes, clips, FFmpeg logs, and output files are held in a private workspace outside Git. The tracked CSV has anonymous case IDs only.

## Environment and method

The campaign ran on the local Windows development host with 8 logical processors reported by the runtime and the bundled FFmpeg build reporting version `n9.0.1-11-ge47273f4d9-20260829`. The native `aac` and MediaFoundation `aac_mf` encoders were available. The host's usable memory and reliable system-wide CPU and disk counters could not be collected in this restricted session. Per-process child CPU time is recorded where available; peak working set is not recorded because no reliable sampler was available.

Three copied real excerpts were used: 600 seconds of 64 kbps mono MP3, 419 seconds of 96 kbps mono MP3, and 600 seconds of stereo AAC in M4A. A 120 second synthetic 96 kbps MP3 set with 12 short files exercised concatenation. Six hundred seconds was chosen to make process startup a small fraction of the transcode time while keeping the matrix bounded. Each selected original was SHA-256 hashed before copying, its private copy was checked against that hash, and the originals are rechecked after the campaign.

The confirmed traits cover 64 kbps, 96 kbps, 128 kbps, mono, stereo, long tracks, AAC/M4A, and a real 12-file set. A synthetic 12-file supplement remains separately labeled. Technical probing cannot establish narrator gender or music content reliably. Those are explicit Task 6 listening dimensions unless private metadata proves them, and the CSV does not imply listening-quality conclusions.

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

The copied real 12-file case received 3 repetitions for each actual strategy below. Direct concat uses FFmpeg's concat demuxer before AAC encoding. Filter concat opens all tracks and uses `concat=n=12:v=0:a=1` in `-filter_complex`. Segmented AAC encodes each copied track separately, then performs a final concat-demuxer AAC stream copy. All rows use 64 kbps native AAC, preserved channels, lightweight validation, and a local output.

| Strategy | Repetitions | Mean wall seconds | Mean realtime factor | Mean child CPU seconds | Mean output bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Direct concat transcode | 3 | 7.16 | 71.40 | 7.97 | 4,280,660 |
| Filter concat transcode | 3 | 6.86 | 74.46 | 7.55 | 4,283,422 |
| Segmented AAC transcode plus final concat | 3 | 8.57 | 59.69 | 0.20 for final concatenate only | 4,288,016 |

Filter concat was the fastest measured strategy on this one real compatible set. The difference from direct concat is small, so direct concat remains a reasonable simpler fallback. Segmented AAC was slower and its final-process CPU figure does not include segment processes, so it is not a CPU comparison. It remains useful only where isolated segment failure handling is worth its additional work.

## Concurrency and storage

The concurrency matrix used the copied 419 second 96 kbps MP3 case, 64 kbps AAC direct transcode, local output, and 2 rounds at 1 through 4 concurrent jobs. Aggregate realtime factor is total source duration divided by the slowest job wall time in a round. Child CPU is per job. Manual responsiveness is `not-measured`, and peak working set is not available.

| Concurrent jobs | Rounds | Aggregate realtime factor | Mean child CPU seconds | Errors |
| --- | ---: | ---: | ---: | ---: |
| 1 | 3 | 74.75 | 6.35 | 0 |
| 2 | 2 | 148.17 | 6.29 | 0 |
| 3 | 2 | 210.96 | 6.45 | 0 |
| 4 | 2 | 245.03 | 7.14 | 0 |
| 6 | 1 | 329.14 | 6.87 | 0 |
| 8 | 1 | 338.32 | 7.04 | 0 |

The 4-job result improved aggregate throughput by 16.1 percent over 3 jobs, exceeding the campaign's 10 percent material-improvement threshold, so 6 jobs was run. Six improved by 34.3 percent over 4, so 8 was run. Eight improved by 2.8 percent over 6, below the threshold. The automatic boundary recommendation for this host is therefore 6 concurrent conversions, with a manual override.

The storage comparison used the same hash-verified 419 second real MP3 source and local output, with 3 repetitions per class. Reading directly from the read-only source storage averaged 5.62 seconds and 74.58 realtime factor. Reading its hash-identical local copy averaged 5.61 seconds and 74.75 realtime factor. The difference is within this campaign's timing noise, so local copying is a safety and isolation choice rather than a measured throughput requirement on this host.

## AudioBookConverter 6.6.8

No installed AudioBookConverter entry was found in standard program directories, user configuration directories, or Windows uninstall registrations. The official [6.6.8 release](https://github.com/yermak/AudioBookConverter/releases) provides a portable Windows asset, and the official [installation page](https://github.com/yermak/AudioBookConverter/wiki/Installation) documents a desktop application and portable extraction but no reproducible conversion CLI. A portable asset was not downloaded because a noninteractive interface could not be established, and unattended GUI automation is excluded. The comparison disposition in `benchmarks/abc-comparison.json` is `not-practical`, not a performance claim. A future manual comparison should use a disposable copied configuration and record the GUI settings, output metadata, chapters, cover behavior, warnings, and validation alongside the same anonymous copied case.

## Limits

This is bounded operational evidence, not a universal encoder ranking. Results can vary with source complexity, cache state, storage topology, and hardware. Task 6 must cover gender, music, and listening quality. A future campaign should add a confirmed higher-than-128 kbps real source, broader mixed-codec and chapter cases, repeated 6 and 8 job rounds, memory and disk sampling, and a manual ABC comparison if its settings can be captured reproducibly.
