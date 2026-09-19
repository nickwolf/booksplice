# Third-party notices

## TagLibSharp

- Project: [TagLibSharp](https://github.com/mono/taglib-sharp)
- Version: `2.3.0`
- License: GNU Lesser General Public License, version 2.1 only

The managed dependency writes native iTunes MP4 atoms and iTunes freeform atoms for the local Mp3tag metadata profile.

The release includes the LGPL 2.1 text at `licenses/LGPL-2.1.txt`. Corresponding source for the packaged version is available from the [TagLibSharp 2.3.0 tag](https://github.com/mono/taglib-sharp/tree/2.3.0).

BookSplice can acquire and use the following third-party components. The downloaded binaries are build artifacts and are not committed to this repository.

## FFmpeg

- Project: [FFmpeg](https://ffmpeg.org/)
- Version: `n9.0.1-84-g946fcce07b`
- Build source: [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
- Build release: `autobuild-2026-09-18-13-22`
- Build variant: `win64-lgpl`
- Runtime license: GNU Lesser General Public License, version 3 or later

The pinned executable reports that it is an LGPL version 3 or later build. Its configuration does not enable FFmpeg's GPL or nonfree build options.

The release includes the license file from the exact binary archive at `licenses/FFmpeg-LICENSE.txt`, plus the LGPL 3.0 and GPL 3.0 texts at `licenses/LGPL-3.0.txt` and `licenses/GPL-3.0.txt`. FFmpeg source is available at [commit `946fcce07b`](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b). The [BtbN build tag](https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-09-18-13-22) records the build scripts, dependency revisions, and source repository URLs used for this artifact.

FFmpeg includes separately maintained libraries. Those components remain subject to their respective license terms. The exact enabled-component list comes from `ffmpeg -buildconf`; dependency revisions and source locations come from the pinned BtbN build tag above.

## BtbN FFmpeg-Builds

- Project: [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
- Copyright: 2020-2021 BtbN
- Build-script license: MIT License

The acquisition script verifies the archive against its pinned SHA-256 digest before extraction. It also checks the executable versions and the capabilities required by BookSplice.
