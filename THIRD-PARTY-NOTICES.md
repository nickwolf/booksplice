# Third-party notices

## TagLibSharp

- Project: [TagLibSharp](https://github.com/mono/taglib-sharp)
- Version: `2.3.0`
- License: GNU Lesser General Public License, version 2.1 only

The managed dependency writes native iTunes MP4 atoms and iTunes freeform atoms for the local Mp3tag metadata profile.

The release includes the LGPL 2.1 text at `licenses/LGPL-2.1.txt`. Corresponding source for the packaged version is available from the [TagLibSharp 2.3.0 tag](https://github.com/mono/taglib-sharp/tree/2.3.0).

## FFmpeg

- Project: [FFmpeg](https://ffmpeg.org/)
- Source commit: [`946fcce07b6dcd0331c8cc609192aeff5e1924f8`](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b6dcd0331c8cc609192aeff5e1924f8)
- Executable version: `9.0.2`
- License: GNU Lesser General Public License, version 2.1 or later, subject to component-specific terms in the source

BookSplice bundles standalone `ffmpeg.exe` and `ffprobe.exe` built from this source commit. The application invokes those executables as separate processes. The build disables FFmpeg's GPL and nonfree options, automatic external-library detection, network support, and unused components. It enables zlib as its only external library. The exact configure flags and executable hashes are recorded in [`licenses/FFmpeg-components.md`](licenses/FFmpeg-components.md).

The release ZIP includes the exact FFmpeg source archive, the build recipe, the builder definition, and the source manifest in `sources/`. It includes the FFmpeg LGPL 2.1 text in `licenses/FFmpeg-LICENSE.txt`. The source archive also contains `LICENSE.md`, which lists component-specific terms. The build uses no local FFmpeg source patches.

## zlib

- Project: [zlib](https://zlib.net/)
- Version: `1.3.2`
- License: zlib License

The static FFmpeg build uses zlib for compressed metadata. The release ZIP includes its exact source archive in `sources/` and its license in `licenses/zlib-LICENSE.txt`. The source and binary hashes are pinned in `sources/manifest.json`.

The broad BtbN FFmpeg build is acquired only to generate MP3 fixtures for automated tests. It is not bundled with the release ZIP. Codec patent questions remain part of the manual release review.