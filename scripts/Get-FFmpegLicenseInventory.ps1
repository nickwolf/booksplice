[CmdletBinding()]
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\tools\ffmpeg\manifest.json'),
    [string]$MediaToolDirectory,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\licenses\FFmpeg-components.md'),
    [string]$BuildConfPath,
    [switch]$FixtureInput,
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json

function New-Component {
    param(
        [Parameter(Mandatory = $true)][string]$Flag,
        [Parameter(Mandatory = $true)][string]$Component,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$License,
        [Parameter(Mandatory = $true)][string]$SourceUrl,
        [Parameter(Mandatory = $true)][string]$LicenseUrl,
        [Parameter(Mandatory = $true)][string]$Notes
    )

    [pscustomobject]@{
        Flag = $Flag
        Component = $Component
        Kind = $Kind
        License = $License
        SourceUrl = $SourceUrl
        LicenseUrl = $LicenseUrl
        Notes = $Notes
    }
}

# This table is intentionally explicit. A new --enable-* flag must be reviewed
# here before the report can be regenerated.
$componentMappings = @(
    (New-Component '--enable-amf' 'AMD Advanced Media Framework' 'vendor API' 'AMD AMF license' 'https://github.com/GPUOpen-LibrariesAndSDKs/AMF' 'https://github.com/GPUOpen-LibrariesAndSDKs/AMF/blob/master/LICENSE.txt' 'Vendor SDK headers and runtime integration; the FFmpeg flag does not identify a redistributable AMD driver.'),
    (New-Component '--enable-chromaprint' 'Chromaprint' 'external library' 'LGPL-2.1-or-later' 'https://github.com/acoustid/chromaprint' 'https://github.com/acoustid/chromaprint/blob/master/LICENSE.txt' 'Confirm the exact dependency revision and any bundled third-party notices from the BtbN build.'),
    (New-Component '--enable-cuda-llvm' 'CUDA LLVM compiler integration' 'build facility' 'NVIDIA CUDA toolkit terms apply to the build toolchain; no runtime library is identified by this flag alone' 'https://github.com/FFmpeg/FFmpeg/blob/n9.0.1/configure' 'https://docs.nvidia.com/cuda/eula/index.html' 'Build-time compiler facility, not evidence that a CUDA runtime is redistributed.'),
    (New-Component '--enable-ffnvcodec' 'NVIDIA Video Codec headers' 'external headers' 'MIT' 'https://github.com/FFmpeg/nv-codec-headers' 'https://github.com/FFmpeg/nv-codec-headers/blob/master/LICENSE' 'The flag names the headers used at build time; it does not by itself prove that an NVIDIA driver is bundled.'),
    (New-Component '--enable-fontconfig' 'Fontconfig' 'external library' 'MIT-style license' 'https://gitlab.freedesktop.org/fontconfig/fontconfig' 'https://gitlab.freedesktop.org/fontconfig/fontconfig/-/blob/main/COPYING' 'Exact dependency revision and transitive font terms require the pinned build metadata.'),
    (New-Component '--enable-gmp' 'GNU MP' 'external library' 'LGPL-3.0-or-later or GPL-3.0-or-later' 'https://gmplib.org/' 'https://gmplib.org/repo/gmp/file/tip/COPYING.LESSER' 'GNU MP is dual licensed; the applicable choice depends on the linked library and build.'),
    (New-Component '--enable-iconv' 'GNU libiconv' 'external library' 'LGPL-2.1-or-later' 'https://www.gnu.org/software/libiconv/' 'https://git.savannah.gnu.org/cgit/libiconv.git/tree/COPYING.LIB' 'The build flag does not identify whether the implementation is the GNU library or a platform implementation.'),
    (New-Component '--enable-libaom' 'Alliance for Open Media libaom' 'external library' 'BSD-2-Clause plus Alliance for Open Media Patent License 1.0' 'https://aomedia.googlesource.com/aom/' 'https://aomedia.googlesource.com/aom/+/refs/heads/main/LICENSE' 'Patent and notice terms are separate from the copyright license.'),
    (New-Component '--enable-libaribb24' 'aribb24' 'external library' 'LGPL-2.1-or-later' 'https://github.com/nkoriyama/aribb24' 'https://github.com/nkoriyama/aribb24/blob/master/COPYING' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libaribcaption' 'libaribcaption' 'external library' 'MIT' 'https://github.com/xqq/libaribcaption' 'https://github.com/xqq/libaribcaption/blob/master/LICENSE' 'The project README identifies the MIT license.'),
    (New-Component '--enable-libass' 'libass' 'external library' 'ISC' 'https://github.com/libass/libass' 'https://github.com/libass/libass/blob/master/COPYING' 'Confirm any bundled font or text-rendering dependency notices.'),
    (New-Component '--enable-libbluray' 'libbluray' 'external library' 'LGPL-2.1-or-later' 'https://code.videolan.org/videolan/libbluray' 'https://code.videolan.org/videolan/libbluray/-/blob/master/COPYING' 'The flag enables the library; it does not indicate that Blu-ray keys or AACS materials are present.'),
    (New-Component '--enable-libdav1d' 'dav1d' 'external library' 'BSD-2-Clause' 'https://code.videolan.org/videolan/dav1d' 'https://code.videolan.org/videolan/dav1d/-/blob/master/COPYING' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libfreetype' 'FreeType' 'external library' 'FreeType License or GPL-2.0-or-later' 'https://gitlab.freedesktop.org/freetype/freetype' 'https://gitlab.freedesktop.org/freetype/freetype/-/blob/master/LICENSE.txt' 'The default FreeType license and its optional GPL alternative must be preserved as stated upstream.'),
    (New-Component '--enable-libfribidi' 'FriBidi' 'external library' 'LGPL-2.1-or-later' 'https://github.com/fribidi/fribidi' 'https://github.com/fribidi/fribidi/blob/master/COPYING' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libgme' 'Game Music Emu' 'external library' 'LGPL-2.1-or-later' 'https://github.com/libgme/game-music-emu' 'https://github.com/libgme/game-music-emu/blob/master/license.txt' 'Confirm any codec-specific notices from the pinned dependency revision.'),
    (New-Component '--enable-libharfbuzz' 'HarfBuzz' 'external library' 'MIT' 'https://github.com/harfbuzz/harfbuzz' 'https://github.com/harfbuzz/harfbuzz/blob/main/COPYING' 'HarfBuzz has additional bundled data and dependency notices; inspect the exact source package for distribution.'),
    (New-Component '--enable-libjxl' 'JPEG XL reference library' 'external library' 'BSD-3-Clause' 'https://github.com/libjxl/libjxl' 'https://github.com/libjxl/libjxl/blob/main/LICENSE' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libkvazaar' 'Kvazaar' 'external library' 'BSD-3-Clause' 'https://github.com/ultravideo/kvazaar' 'https://github.com/ultravideo/kvazaar/blob/master/LICENSE' 'Kvazaar changed from LGPL to BSD-3-Clause in the 2.1 release; the exact pinned revision still needs confirmation.'),
    (New-Component '--enable-liblcevc-dec' 'LCEVCdec' 'external library' 'BSD-3-Clause-Clear with an explicit patent-license exclusion' 'https://github.com/v-novaltd/LCEVCdec' 'https://github.com/v-novaltd/LCEVCdec/blob/main/LICENSE' 'The upstream license excludes patent rights and refers to separate patent terms. Confirm the exact dependency revision from the pinned build.'),
    (New-Component '--enable-libmp3lame' 'LAME' 'external library' 'LGPL-2.0-or-later' 'https://github.com/lameproject/lame' 'https://github.com/lameproject/lame/blob/master/LICENSE' 'FFmpeg specifically calls out LAME as an external library whose terms must be considered.'),
    (New-Component '--enable-liboapv' 'OpenAPV' 'external library' 'BSD-3-Clause' 'https://github.com/AcademySoftwareFoundation/openapv' 'https://github.com/AcademySoftwareFoundation/openapv/blob/main/LICENSE' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libopencore-amrnb' 'OpenCORE AMR-NB' 'external library' 'Apache-2.0 or LGPL-2.1-or-later depending on the source component' 'https://sourceforge.net/projects/opencore-amr/' 'https://sourceforge.net/projects/opencore-amr/' 'The upstream distribution contains separately licensed source components; inspect the exact pinned source and notices.'),
    (New-Component '--enable-libopencore-amrwb' 'OpenCORE AMR-WB' 'external library' 'Apache-2.0 or LGPL-2.1-or-later depending on the source component' 'https://sourceforge.net/projects/opencore-amr/' 'https://sourceforge.net/projects/opencore-amr/' 'The upstream distribution contains separately licensed source components; inspect the exact pinned source and notices.'),
    (New-Component '--enable-libopenh264' 'OpenH264' 'external library' 'BSD-2-Clause source license plus Cisco binary and patent terms' 'https://github.com/cisco/openh264' 'https://github.com/cisco/openh264/blob/master/LICENSE' 'Cisco publishes a separate binary license; source copyright terms do not settle patent or binary distribution obligations.'),
    (New-Component '--enable-libopenjpeg' 'OpenJPEG' 'external library' 'BSD-2-Clause' 'https://github.com/uclouvain/openjpeg' 'https://github.com/uclouvain/openjpeg/blob/master/LICENSE' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libopenmpt' 'libopenmpt' 'external library' 'BSD-3-Clause' 'https://github.com/OpenMPT/openmpt' 'https://github.com/OpenMPT/openmpt/blob/master/LICENSE' 'The upstream project notes that some external-project folders have separate terms.'),
    (New-Component '--enable-libopus' 'Opus' 'external library' 'BSD-3-Clause' 'https://github.com/xiph/opus' 'https://github.com/xiph/opus/blob/main/COPYING' 'The Opus codec also has separate patent policy material.'),
    (New-Component '--enable-libplacebo' 'libplacebo' 'external library' 'LGPL-2.1-or-later' 'https://github.com/haasn/libplacebo' 'https://github.com/haasn/libplacebo/blob/master/LICENSE' 'libplacebo includes third-party submodules with their own terms.'),
    (New-Component '--enable-librav1e' 'rav1e' 'external library' 'BSD-2-Clause' 'https://github.com/xiph/rav1e' 'https://github.com/xiph/rav1e/blob/master/LICENSE' 'Codec patent and contribution terms are separate from the copyright license.'),
    (New-Component '--enable-librist' 'libRIST' 'external library' 'BSD-2-Clause' 'https://github.com/eerimoq/librist' 'https://github.com/eerimoq/librist/blob/master/LICENSE' 'The project is also maintained through the VideoLAN RIST project.'),
    (New-Component '--enable-librsvg' 'librsvg' 'external library' 'LGPL-2.1-or-later' 'https://gitlab.gnome.org/GNOME/librsvg' 'https://gitlab.gnome.org/GNOME/librsvg/-/blob/main/COPYING.LIB' 'The library includes bundled dependencies and data whose notices must be retained.'),
    (New-Component '--enable-libsnappy' 'Snappy' 'external library' 'BSD-3-Clause' 'https://github.com/google/snappy' 'https://github.com/google/snappy/blob/main/COPYING' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libsoxr' 'SoX Resampler' 'external library' 'LGPL-2.1-or-later' 'https://github.com/chirlu/soxr' 'https://github.com/chirlu/soxr/blob/master/COPYING.LGPL' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libsrt' 'Secure Reliable Transport' 'external library' 'MPL-2.0' 'https://github.com/Haivision/srt' 'https://github.com/Haivision/srt/blob/master/LICENSE' 'SRT has optional cryptography and platform dependencies with separate terms.'),
    (New-Component '--enable-libssh' 'libssh' 'external library' 'LGPL-2.1-or-later' 'https://gitlab.com/libssh/libssh-mirror' 'https://gitlab.com/libssh/libssh-mirror/-/blob/master/COPYING' 'Confirm the exact dependency revision and crypto dependency notices.'),
    (New-Component '--enable-libsvtav1' 'SVT-AV1' 'external library' 'BSD-3-Clause plus Alliance for Open Media Patent License 1.0' 'https://gitlab.com/AOMediaCodec/SVT-AV1' 'https://gitlab.com/AOMediaCodec/SVT-AV1/-/blob/master/LICENSE.md' 'The project documents a separate patent license and changed from BSD-2 to BSD-3 at v0.9.'),
    (New-Component '--enable-libtheora' 'Theora' 'external library' 'BSD-3-Clause' 'https://github.com/xiph/theora' 'https://github.com/xiph/theora/blob/main/COPYING' 'Theora depends on Ogg and may carry related notices.'),
    (New-Component '--enable-libtwolame' 'TwoLAME' 'external library' 'LGPL-2.1-or-later' 'https://github.com/njh/twolame' 'https://github.com/njh/twolame/blob/master/COPYING' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libuavs3d' 'uavs3d' 'external library' 'BSD-3-Clause' 'https://github.com/uavs3/uavs3d' 'https://github.com/uavs3/uavs3d/blob/master/COPYING' 'The upstream README identifies the BSD-3-Clause terms; verify the exact COPYING revision.'),
    (New-Component '--enable-libvmaf' 'VMAF' 'external library' 'BSD plus patent license' 'https://github.com/Netflix/vmaf' 'https://github.com/Netflix/vmaf/blob/master/LICENSE' 'VMAF documents a BSD plus patent license; models and data can have separate notices.'),
    (New-Component '--enable-libvorbis' 'Vorbis' 'external library' 'BSD-3-Clause' 'https://xiph.org/vorbis/' 'https://github.com/xiph/vorbis/blob/master/COPYING' 'Vorbis depends on Ogg and may carry related notices.'),
    (New-Component '--enable-libvpl' 'Intel oneVPL' 'external library' 'MIT' 'https://github.com/intel/libvpl' 'https://github.com/intel/libvpl/blob/master/LICENSE' 'The Intel runtime and hardware driver are separate from the SDK headers.'),
    (New-Component '--enable-libvpx' 'libvpx' 'external library' 'BSD-3-Clause' 'https://chromium.googlesource.com/webm/libvpx/' 'https://chromium.googlesource.com/webm/libvpx/+/main/LICENSE' 'The project includes patent-policy material in addition to the copyright license.'),
    (New-Component '--enable-libvvenc' 'VVenc' 'external library' 'BSD-3-Clause' 'https://github.com/fraunhoferhhi/vvenc' 'https://github.com/fraunhoferhhi/vvenc/blob/master/LICENSE.txt' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libwebp' 'WebP' 'external library' 'BSD-3-Clause' 'https://chromium.googlesource.com/webm/libwebp/' 'https://chromium.googlesource.com/webm/libwebp/+/main/COPYING' 'The project includes patent-policy material in addition to the copyright license.'),
    (New-Component '--enable-libxml2' 'libxml2' 'external library' 'MIT' 'https://gitlab.gnome.org/GNOME/libxml2' 'https://gitlab.gnome.org/GNOME/libxml2/-/blob/master/Copyright' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-libzimg' 'zimg' 'external library' 'WTFPL-2.0' 'https://github.com/sekrit-twc/zimg' 'https://github.com/sekrit-twc/zimg/blob/master/COPYING' 'The upstream license text is the authority; do not substitute a generic permissive-license label.'),
    (New-Component '--enable-libzmq' 'libzmq' 'external library' 'MPL-2.0' 'https://github.com/zeromq/libzmq' 'https://github.com/zeromq/libzmq/blob/master/LICENSE' 'Confirm bundled dependencies and notices from the pinned source revision.'),
    (New-Component '--enable-libzvbi' 'ZVBI' 'external library' 'LGPL-2.1-or-later' 'https://github.com/zapping-vbi/zvbi' 'https://github.com/zapping-vbi/zvbi/blob/master/COPYING' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-lv2' 'LV2 plugin API' 'external API' 'ISC and component-specific terms' 'https://github.com/lv2/lv2' 'https://github.com/lv2/lv2/blob/master/COPYING' 'LV2 is a family of specifications and plugins with per-component terms; the flag alone does not identify a plugin.'),
    (New-Component '--enable-lzma' 'liblzma from XZ Utils' 'external library' '0BSD' 'https://github.com/tukaani-project/xz' 'https://github.com/tukaani-project/xz/blob/master/COPYING' 'XZ Utils applies separate terms to some command-line support files; confirm the exact liblzma revision and notices from the pinned build.'),
    (New-Component '--enable-openal' 'OpenAL Soft' 'external library' 'LGPL-2.1-or-later' 'https://github.com/kcat/openal-soft' 'https://github.com/kcat/openal-soft/blob/master/COPYING' 'The flag does not identify whether a system OpenAL implementation or OpenAL Soft was used.'),
    (New-Component '--enable-opencl' 'OpenCL API' 'external API' 'Apache-2.0 for Khronos headers; implementation terms vary by vendor' 'https://github.com/KhronosGroup/OpenCL-Headers' 'https://github.com/KhronosGroup/OpenCL-Headers/blob/main/LICENSE' 'The flag does not identify an OpenCL runtime or vendor implementation in the redistributed binary.'),
    (New-Component '--enable-pthreads' 'pthreads portability layer' 'platform/runtime facility' 'Implementation-specific; no separate library is identified by this flag' 'https://github.com/FFmpeg/FFmpeg/blob/n9.0.1/configure' 'https://github.com/FFmpeg/FFmpeg/blob/n9.0.1/configure' 'The build flag selects threading support; it does not identify the Windows threading implementation.'),
    (New-Component '--enable-schannel' 'Windows Schannel' 'platform API' 'Microsoft Windows SDK and system component terms' 'https://learn.microsoft.com/windows/win32/secauthn/secure-channel' 'https://learn.microsoft.com/legal/termsofuse' 'System API used at runtime; no Schannel binary is bundled by this flag.'),
    (New-Component '--enable-sdl2' 'Simple DirectMedia Layer 2' 'external library' 'zlib License' 'https://github.com/libsdl-org/SDL' 'https://github.com/libsdl-org/SDL/blob/main/LICENSE.txt' 'Confirm the exact dependency revision and notices from the pinned build.'),
    (New-Component '--enable-vaapi' 'VA-API' 'external API' 'MIT' 'https://github.com/intel/libva' 'https://github.com/intel/libva/blob/master/COPYING' 'Linux-oriented API flag; the Windows binary does not thereby bundle a VA-API implementation.'),
    (New-Component '--enable-gpl' 'FFmpeg GPL license switch' 'FFmpeg build setting' 'GNU GPL-2.0-or-later applies when enabled' 'https://ffmpeg.org/legal.html' 'https://github.com/FFmpeg/FFmpeg/blob/n9.0.1/LICENSE.md' 'Forbidden for this pinned LGPL package; included so a future enablement fails with an explicit license-switch error.'),
    (New-Component '--enable-nonfree' 'FFmpeg nonfree license switch' 'FFmpeg build setting' 'Nonfree terms require separate review and are not redistributable under the project configuration' 'https://ffmpeg.org/legal.html' 'https://github.com/FFmpeg/FFmpeg/blob/n9.0.1/LICENSE.md' 'Forbidden for this pinned LGPL package; included so a future enablement fails with an explicit license-switch error.'),
    (New-Component '--enable-version3' 'FFmpeg version 3 license compatibility switch' 'FFmpeg build setting' 'FFmpeg LGPL-3.0-or-later' 'https://ffmpeg.org/legal.html' 'https://github.com/FFmpeg/FFmpeg/blob/n9.0.1/LICENSE.md' 'This is an FFmpeg licensing setting, not a separately maintained external library.'),
    (New-Component '--enable-vulkan' 'Vulkan API' 'external API' 'Apache-2.0 for Khronos headers; implementation terms vary by vendor' 'https://github.com/KhronosGroup/Vulkan-Headers' 'https://github.com/KhronosGroup/Vulkan-Headers/blob/main/LICENSE.txt' 'The flag does not identify a Vulkan runtime or vendor driver in the redistributed binary.'),
    (New-Component '--enable-zlib' 'zlib' 'external library' 'zlib License' 'https://zlib.net/' 'https://zlib.net/zlib_license.html' 'Confirm the exact dependency revision and notices from the pinned build.')
)

$mappingByFlag = @{}
foreach ($mapping in $componentMappings) { $mappingByFlag[$mapping.Flag] = $mapping }

function Invoke-FFmpeg {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & $ExecutablePath @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg command '$($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
    return $output
}

if (-not ($manifest.PSObject.Properties.Name -contains 'expectedVersionPrefix')) { throw 'FFmpeg manifest is missing expectedVersionPrefix.' }
if (-not ($manifest.PSObject.Properties.Name -contains 'release')) { throw 'FFmpeg manifest is missing release.' }
if (-not ($manifest.PSObject.Properties.Name -contains 'ffmpegSha256')) { throw 'FFmpeg manifest is missing ffmpegSha256.' }

$ffmpegPath = $null
$versionOutput = $null
if ([string]::IsNullOrWhiteSpace($BuildConfPath)) {
    if ([string]::IsNullOrWhiteSpace($MediaToolDirectory)) { $MediaToolDirectory = $env:BOOKSPLICE_FFMPEG_DIR }
    if ([string]::IsNullOrWhiteSpace($MediaToolDirectory)) {
        $MediaToolDirectory = Join-Path $repositoryRoot (Join-Path 'artifacts\tools\ffmpeg' ([string]$manifest.release))
    }
    $ffmpegPath = Join-Path ([IO.Path]::GetFullPath($MediaToolDirectory)) 'ffmpeg.exe'
    if (-not (Test-Path -LiteralPath $ffmpegPath -PathType Leaf)) { throw "Pinned ffmpeg.exe was not found at '$ffmpegPath'." }
    $actualFfmpegSha256 = (Get-FileHash -LiteralPath $ffmpegPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actualFfmpegSha256, [string]$manifest.ffmpegSha256, [StringComparison]::Ordinal)) {
        throw "ffmpeg.exe checksum drift detected. Expected '$($manifest.ffmpegSha256)', received '$actualFfmpegSha256'."
    }
    $versionOutput = Invoke-FFmpeg -ExecutablePath $ffmpegPath -Arguments @('-version')
    $versionLine = ($versionOutput -split "`r?`n")[0].Trim()
    if (-not $versionLine.StartsWith([string]$manifest.expectedVersionPrefix, [StringComparison]::Ordinal)) {
        throw "ffmpeg.exe version drift detected. Expected prefix '$($manifest.expectedVersionPrefix)', received '$versionLine'."
    }
    $buildConfText = Invoke-FFmpeg -ExecutablePath $ffmpegPath -Arguments @('-hide_banner', '-buildconf')
}
else {
    if (-not $FixtureInput) { throw 'BuildConfPath is test-only and requires -FixtureInput.' }
    $buildConfText = Get-Content -LiteralPath ([IO.Path]::GetFullPath($BuildConfPath)) -Raw
    $versionLine = "$($manifest.expectedVersionPrefix) (fixture input)"
}

$flags = @([regex]::Matches($buildConfText, '(?<!\S)--(?:enable|disable)-[^\s]+') |
    ForEach-Object { $_.Value } |
    Sort-Object -Unique)
if ($flags.Count -eq 0) { throw 'FFmpeg build configuration contains no enable or disable flags.' }
$enabledFlags = @($flags | Where-Object { $_ -like '--enable-*' })
$disabledFlags = @($flags | Where-Object { $_ -like '--disable-*' })

$unmapped = @($enabledFlags | Where-Object { -not $mappingByFlag.ContainsKey($_) })
if ($unmapped.Count -gt 0) {
    throw "Pinned FFmpeg build has enabled flags without an explicit reviewed mapping: $($unmapped -join ', ')"
}

$forbidden = @($enabledFlags | Where-Object { $_ -in @('--enable-gpl', '--enable-nonfree') })
if ($forbidden.Count -gt 0) { throw "Pinned FFmpeg build enables forbidden license switches: $($forbidden -join ', ')" }
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$canonicalOutputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'licenses\FFmpeg-components.md'))
if ($FixtureInput -and [string]::Equals($resolvedOutputPath, $canonicalOutputPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture input cannot write or verify the canonical release inventory.'
}

$rows = @($enabledFlags | ForEach-Object { $mappingByFlag[$_] } | Sort-Object Flag)
$manifestSha = if ($manifest.PSObject.Properties.Name -contains 'sha256') { [string]$manifest.sha256 } else { 'not recorded' }
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Pinned FFmpeg component inventory')
$lines.Add('')
$lines.Add('This document is generated by `scripts/Get-FFmpegLicenseInventory.ps1` from the exact pinned executable and its `ffmpeg -buildconf` output. It is a reviewed mapping of enabled configure flags to their upstream project or platform source.')
$lines.Add('')
$lines.Add("- FFmpeg version: $versionLine")
$lines.Add("- Provider: $($manifest.provider)")
$lines.Add("- Release: $($manifest.release)")
$lines.Add("- Asset: $($manifest.asset)")
$lines.Add("- Variant: $($manifest.variant)")
$lines.Add("- Archive SHA-256: $manifestSha")
$lines.Add("- ffmpeg.exe SHA-256: $($manifest.ffmpegSha256)")
$lines.Add('')
$lines.Add('## Scope and limits')
$lines.Add('')
$lines.Add('The configure line identifies enabled FFmpeg integration points. It does not prove which optional code paths were exercised, whether a dependency was linked statically or dynamically, which transitive dependencies were included, or that a system API implementation is redistributed. The mappings below therefore do not claim legal completeness. The exact dependency revisions, notices, patent terms, and vendor or platform terms must be checked against the pinned BtbN build sources and the corresponding upstream files before release publication.')
$lines.Add('')
$lines.Add('FFmpeg reports an LGPL configuration because `--enable-gpl` and `--enable-nonfree` are absent and `--enable-version3` is present. Separately maintained libraries remain subject to their own terms.')
$lines.Add('')
$lines.Add('## Enabled components')
$lines.Add('')
$lines.Add('| Configure flag | Component | Kind | License or terms | Upstream source | License source | Notes |')
$lines.Add('| --- | --- | --- | --- | --- | --- | --- |')
foreach ($row in $rows) {
    $notes = $row.Notes.Replace('|', '\|')
    $lines.Add("| $($row.Flag) | $($row.Component) | $($row.Kind) | $($row.License) | [upstream]($($row.SourceUrl)) | [license or terms]($($row.LicenseUrl)) | $notes |")
}
$lines.Add('')
$lines.Add('## Disabled license-sensitive flags')
$lines.Add('')
$lines.Add("The exact build configuration reports these disabled flags: $($disabledFlags -join ', ').")
$lines.Add('')
$lines.Add('This report does not independently determine the terms of every transitive dependency used by the enabled libraries. It is a release review input and must be used with the exact binary archive, its included FFmpeg license, the pinned build source, and the upstream notices.')
$lines.Add('')
$content = ($lines -join "`n") + "`n"
if ($Check) {
    if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) { throw "Inventory output was not found at '$resolvedOutputPath'." }
    $existing = [IO.File]::ReadAllText($resolvedOutputPath)
    if ($existing -ne $content) { throw "Inventory output is stale. Regenerate '$resolvedOutputPath'." }
    Write-Output "FFmpeg component inventory is current: $resolvedOutputPath"
}
else {
    $parent = Split-Path $resolvedOutputPath -Parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [IO.File]::WriteAllText($resolvedOutputPath, $content, [Text.UTF8Encoding]::new($false))
    Write-Output "Wrote FFmpeg component inventory: $resolvedOutputPath"
}
