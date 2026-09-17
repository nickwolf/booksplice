# Third-party notices

## TagLibSharp

- Project: [TagLibSharp](https://github.com/mono/taglib-sharp)
- Version: `2.3.0`
- License: GNU Lesser General Public License, version 2.1 only

The managed dependency writes native iTunes MP4 atoms and iTunes freeform atoms for the local Mp3tag metadata profile.

BookSplice can acquire and use the following third-party components. The downloaded binaries are build artifacts and are not committed to this repository.

## FFmpeg

- Project: [FFmpeg](https://ffmpeg.org/)
- Version: `n9.0.1-11-ge47273f4d9`
- Build source: [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
- Build release: `autobuild-2026-08-29-13-12`
- Build variant: `win64-lgpl`
- Runtime license: GNU Lesser General Public License, version 3 or later

The pinned executable reports that it is an LGPL version 3 or later build. Its configuration does not enable FFmpeg's GPL or nonfree build options.

FFmpeg includes separately maintained libraries. The pinned build reports components including AOM, dav1d, fontconfig, FreeType, FriBidi, HarfBuzz, libass, libjxl, libplacebo, librist, libssh, libxml2, libzimg, OpenJPEG, Opus, SRT, SVT-AV1, Vorbis, VPX, and WebP. Those components remain subject to their respective license terms. The acquired archive and the output of `ffmpeg -version` provide the authoritative component list for this exact build.

## BtbN FFmpeg-Builds

- Project: [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
- Copyright: 2020-2021 BtbN
- Build-script license: MIT License

The acquisition script verifies the archive against its pinned SHA-256 digest before extraction. It also checks the executable versions and the capabilities required by BookSplice.
