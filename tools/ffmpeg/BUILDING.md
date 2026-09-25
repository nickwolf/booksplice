# Rebuild the bundled media tools

The release ZIP includes the exact FFmpeg and zlib source archives used for its Windows executables. `manifest.json` records their SHA-256 values and the expected executable and license hashes. `Dockerfile` pins the Debian base image and signed Debian package snapshot. `build-minimal.sh` records the complete configure line and build commands. No source patches are applied.

From PowerShell in this `sources` directory, verify the archives and build with Docker running Linux containers:

```powershell
$manifest = Get-Content .\manifest.json -Raw | ConvertFrom-Json
foreach ($source in $manifest.sourceArchives) {
    $actual = (Get-FileHash $source.fileName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $source.sha256) { throw "Source archive mismatch: $($source.fileName)" }
}
docker build --platform linux/amd64 -t booksplice-ffmpeg-builder:20260921 .
docker run --rm --platform linux/amd64 --memory=4g --cpus=2 -v "${PWD}:/work" booksplice-ffmpeg-builder:20260921 sh /work/build-minimal.sh
foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
    $expected = if ($tool -eq 'ffmpeg.exe') { $manifest.ffmpegSha256 } else { $manifest.ffprobeSha256 }
    $actual = (Get-FileHash (Join-Path .\output $tool) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $expected) { throw "Executable mismatch: $tool" }
}
```

Use a fresh extracted directory for each build. The script writes to `output` and keeps build logs in the same directory. The packaged application invokes FFmpeg as a separate process. The source archive contains FFmpeg's `LICENSE.md` with component-specific terms, and the ZIP includes the LGPL 2.1 and zlib license texts in `licenses`.