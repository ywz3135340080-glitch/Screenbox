#!/usr/bin/env bash
set -euo pipefail
root="$PWD"
prefix="$PWD/color-runtime-prefix"
mkdir -p "$prefix" color-runtime
if [[ ! -f "$prefix/lib/libswresample.a" ]]; then
  git init color-ffmpeg
  git -C color-ffmpeg remote add origin https://github.com/FFmpeg/FFmpeg.git
  git -C color-ffmpeg fetch --depth 1 origin 8dba37253f985bcba0b9dd26adb94a7593124796
  git -C color-ffmpeg checkout --detach FETCH_HEAD
  cd color-ffmpeg
  ./configure --prefix="$prefix" --disable-autodetect --disable-everything \
    --disable-programs --disable-doc --disable-debug --disable-network \
    --disable-avdevice --disable-avfilter --disable-swscale \
    --enable-static --disable-shared --enable-avcodec --enable-avformat --enable-avutil \
    --enable-swresample \
    --enable-decoder=hevc,aac,ac3,eac3,dca,truehd,flac,mp3,opus,vorbis,pcm_s16le,pcm_s24le,pcm_s32le \
    --enable-parser=hevc,aac,ac3,mpegaudio,flac,opus,vorbis \
    --enable-demuxer=mov,matroska --enable-small \
    --extra-cflags="-O2" --extra-ldflags="-static-libgcc"
  make -j4
  make install
fi
cd "$root"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:/ucrt64/lib/pkgconfig"
gcc -std=c11 -O2 -Wall -Wextra -Wno-unused-function -shared \
  native/color/screenbox-color.c -o color-runtime/Screenbox.NativeColor.dll \
  $(pkg-config --cflags --libs libplacebo) \
  $(pkg-config --cflags --libs --static libavformat libavcodec libswresample libavutil) \
  -static-libgcc -lm
strip color-runtime/Screenbox.NativeColor.dll
for pass in 1 2 3 4 5 6 7 8; do
  for item in color-runtime/*.dll; do
    while read -r dll; do
      [[ -f "/ucrt64/bin/$dll" ]] || continue
      [[ -f "color-runtime/$dll" ]] || cp "/ucrt64/bin/$dll" color-runtime/
    done < <(objdump -p "$item" | awk '/DLL Name:/ {print $3}')
  done
done
mkdir -p color-runtime/licenses
if [[ -f color-ffmpeg/COPYING.LGPLv2.1 ]]; then
  cp color-ffmpeg/COPYING.LGPLv2.1 color-runtime/licenses/FFmpeg-LGPL-2.1.txt
else
  curl --fail --location https://raw.githubusercontent.com/FFmpeg/FFmpeg/8dba37253f985bcba0b9dd26adb94a7593124796/COPYING.LGPLv2.1 -o color-runtime/licenses/FFmpeg-LGPL-2.1.txt
fi
for pkg in libplacebo libdovi shaderc spirv-cross lcms2 vulkan-loader gcc-libs; do
  path="/ucrt64/share/licenses/$pkg"
  [[ ! -d "$path" ]] || cp -r "$path" "color-runtime/licenses/$pkg"
done
pacman -Q > color-runtime/dependency-versions.txt
sha256sum color-runtime/*.dll > color-runtime/SHA256SUMS
