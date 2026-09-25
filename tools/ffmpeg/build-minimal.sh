#!/bin/sh
set -eu
export SOURCE_DATE_EPOCH=0
mkdir -p /tmp/ffmpeg-src /tmp/zlib-src /work/output
tar -xzf /work/zlib-v1.3.2.tar.gz -C /tmp/zlib-src --strip-components=1
make -C /tmp/zlib-src -f win32/Makefile.gcc PREFIX=x86_64-w64-mingw32- libz.a > /work/make-zlib.log 2>&1
tar -xzf /work/ffmpeg-946fcce07b.tar.gz -C /tmp/ffmpeg-src --strip-components=1
cd /tmp/ffmpeg-src
./configure \
  --target-os=mingw32 --arch=x86_64 --enable-cross-compile --cross-prefix=x86_64-w64-mingw32- \
  --disable-autodetect --disable-x86asm --disable-doc --disable-debug --disable-network --disable-devices \
  --disable-programs --enable-ffmpeg --enable-ffprobe --enable-static --disable-shared --disable-gpl --disable-nonfree \
  --disable-everything --enable-zlib --extra-cflags=-I/tmp/zlib-src --extra-ldflags="-L/tmp/zlib-src -Wl,--no-insert-timestamp" \
  --enable-protocol=file,pipe \
  --enable-indev=lavfi \
  --enable-demuxer=aac,asf,concat,ffmetadata,flac,image2,mp3,mov,ogg,wav \
  --enable-muxer=adts,pcm_f32le,ffmetadata,hash,image2,image2pipe,ipod,mov,mp3,mp4,null,wav \
  --enable-decoder=aac,alac,flac,mjpeg,mp3,mp3float,opus,pcm_s16le,png,vorbis,wmav1,wmav2,wmapro,wmavoice,wrapped_avframe \
  --enable-encoder=aac,mjpeg,pcm_f32le,pcm_s16le,png \
  --enable-parser=aac,aac_latm,flac,mjpeg,mpegaudio,opus,png,vorbis \
  --enable-filter=abuffer,abuffersink,anull,anullsink,aresample,asetpts,buffer,buffersink,color,concat,format,null,scale,sine \
  --enable-bsf=aac_adtstoasc,extract_extradata \
  > /work/configure-minimal.log 2>&1
make -j2 > /work/make-minimal.log 2>&1
cp ffmpeg.exe ffprobe.exe /work/output/
cp COPYING.LGPLv2.1 /work/output/FFmpeg-LICENSE.txt
cp /tmp/zlib-src/LICENSE /work/output/zlib-LICENSE.txt
cp LICENSE.md /work/output/FFmpeg-LICENSE-overview.md