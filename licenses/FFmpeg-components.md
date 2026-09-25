# Pinned FFmpeg component inventory

This inventory describes the source-built tools in the Windows release package. The exact executable checksum and configure flags are checked by `scripts/Get-FFmpegLicenseInventory.ps1`.

- FFmpeg version: ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers
- ffmpeg.exe SHA-256: c32286d635810fc187f174de0657f05f82e26d19ed745ec8e885969e4b43a2c4
- ffprobe.exe SHA-256: 160222bb2d1c74eb9d1c8d3e332ede1acd14acff2141e6dc67b5a0e1105b4706
- Configure SHA-256: b95057c63f5d17d6de51a6532c86158032069a2514a6b8216acea0a1499e2484
- FFmpeg source archive SHA-256: 86f82e82456005881d1b97e9ce02f8a40bf2d181c8c958f8ae736886fdba2a34
- zlib source archive SHA-256: b99a0b86c0ba9360ec7e78c4f1e43b1cbdf1e6936c8fa0f6835c0cd694a495a1

## External components

| Component | Version | Terms | Source |
| --- | --- | --- | --- |
| FFmpeg | 946fcce07b6dcd0331c8cc609192aeff5e1924f8 | LGPL 2.1 or later with component-specific exceptions in the upstream source | [FFmpeg source](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b6dcd0331c8cc609192aeff5e1924f8) |
| zlib | 1.3.2 | zlib License | [zlib source](https://github.com/madler/zlib/releases/tag/v1.3.2) |

The build disables automatic dependency detection, GPL and nonfree components. zlib is the only enabled external library. The package includes the corresponding source archives and build recipe. Codec patent questions are separate from the copyright terms.

## Exact configure flags

```text
--target-os=mingw32
--arch=x86_64
--enable-cross-compile
--cross-prefix=x86_64-w64-mingw32-
--disable-autodetect
--disable-x86asm
--disable-doc
--disable-debug
--disable-network
--disable-devices
--disable-programs
--enable-ffmpeg
--enable-ffprobe
--enable-static
--disable-shared
--disable-gpl
--disable-nonfree
--disable-everything
--enable-zlib
--extra-cflags=-I/tmp/zlib-src
--extra-ldflags='-L/tmp/zlib-src -Wl,--no-insert-timestamp'
--enable-protocol='file,pipe'
--enable-indev=lavfi
--enable-demuxer='aac,asf,concat,ffmetadata,flac,image2,mp3,mov,ogg,wav'
--enable-muxer='adts,pcm_f32le,ffmetadata,hash,image2,image2pipe,ipod,mov,mp3,mp4,null,wav'
--enable-decoder='aac,alac,flac,mjpeg,mp3,mp3float,opus,pcm_s16le,png,vorbis,wmav1,wmav2,wmapro,wmavoice,wrapped_avframe'
--enable-encoder='aac,mjpeg,pcm_f32le,pcm_s16le,png'
--enable-parser='aac,aac_latm,flac,mjpeg,mpegaudio,opus,png,vorbis'
--enable-filter='abuffer,abuffersink,anull,anullsink,aresample,asetpts,buffer,buffersink,color,concat,format,null,scale,sine'
--enable-bsf='aac_adtstoasc,extract_extradata'
```

