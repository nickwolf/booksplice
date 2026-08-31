# Task 4 report: benchmark harness

Round 1 was incomplete. It reported a stale apphost lock and a failing 64 kbps MP3 smoke case. Round 2 rebuilt the Debug DLLs, regenerated fixtures with the pinned tools, and ran the scripts through their DLL entry points. The final clean smoke run has 17 `ok` rows, 1 deliberate corrupt-input `expected-failure` row, and 0 `failed` rows.

## Implementation

The fixture generator creates deterministic sine, pink-noise, silence, stereo, MP3 64/96/128/192 kbps, AAC mono/stereo, AAC 22050/32000/44100/48000 Hz, Unicode path, JPEG and PNG attached-picture, two-chapter, eight-track, and one-byte corrupt fixtures. `--long` remains opt-in and creates the 60 second WAV only when supplied. The catalog records each FFmpeg argument list and expected structural properties.

The benchmark runner validates source properties, converts audio streams only with `-map 0:a:0`, probes source and output, and records source duration, wall time, child CPU time, input and output codec/channel/sample-rate facts, output duration and bytes, realtime factor, status, and reason. Each attempted case is appended immediately through the write-through CSV writer. Run IDs are nonempty unique 32-character hexadecimal values.

The strict manifest loader rejects unknown fields, missing or empty required values, duplicate case IDs and collection values, malformed or duplicate expected-order names, and invalid case IDs. Its errors use generic wording and do not disclose manifest `sourcePath` values. The schema has the same required fields, unique collections, and filename-only expected-order contract. The committed example remains synthetic.

## Final verification

Commands run from the repository root:

```powershell
dotnet build tools/AudiobookConverter.FixtureGenerator/AudiobookConverter.FixtureGenerator.csproj -c Debug -p:NuGetAudit=false
dotnet build tools/AudiobookConverter.Benchmarks/AudiobookConverter.Benchmarks.csproj -c Debug -p:NuGetAudit=false
dotnet test tests/AudiobookConverter.FFmpeg.Tests/AudiobookConverter.FFmpeg.Tests.csproj -c Debug --filter "FullyQualifiedName~Benchmark" -p:NuGetAudit=false
./scripts/New-TestFixtures.ps1
Remove-Item ./artifacts/benchmarks/smoke.csv -ErrorAction SilentlyContinue
./scripts/Invoke-Benchmarks.ps1
dotnet build AudiobookConverter.slnx -c Release -p:NuGetAudit=false
dotnet test AudiobookConverter.slnx -c Release --no-build -p:NuGetAudit=false
dotnet format AudiobookConverter.slnx whitespace --include <Task 4 files> -p:NuGetAudit=false
git diff --check
```

The focused Debug benchmark suite passed 14 tests: 8 CSV writer tests and 6 manifest-loader tests. They cover invariant culture, RFC-style escaping, a header written once across reopen, immediate row visibility, preservation after a later thrown exception or cancellation, and unique run IDs. The Release solution build passed with 0 warnings and 0 errors. The full Release test suite passed 39 tests: FFmpeg 34, Core 1, CLI 1, GUI 1, and Acceptance 2.

The final smoke CSV has 18 rows. `case-mp3-mono-064` succeeds. The 17 successful rows have child CPU minus wall-time deviations from -0.0400946 seconds to -0.0059365 seconds, with a -0.0239068 second average across 17 samples. The CPU metric is process time and is visibly quantized for these short conversions.

## Fixture probes

The generated JPEG cover contains `aac,mjpeg` streams with 1 attached picture. The PNG cover contains `aac,png` streams with 1 attached picture. The chaptered AAC fixture has 2 chapters. The ordered track directory is `001.m4a` through `008.m4a`. The Unicode fixture exists at its generated Japanese filename. AAC probe results are mono AAC at 22050, 32000, and 44100 Hz plus stereo AAC at 48000 Hz.

## Checks and concerns

`git diff --check` passed. A scan of changed tracked content found no user-home or drive-pool path. Gitleaks was not installed or available on PATH, so no Gitleaks run was possible. The 60 second fixture wiring is covered by the `--long` generator option but was not generated in this smoke run. Short conversions make child CPU time quantization noticeable, so this smoke evidence is functional rather than a stable performance baseline.
