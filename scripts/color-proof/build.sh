#!/usr/bin/env bash
set -euo pipefail
root="$PWD"
prefix="$PWD/native-prefix"
mkdir -p "$prefix" native-out
git init ffmpeg-src
git -C ffmpeg-src remote add origin https://github.com/FFmpeg/FFmpeg.git
git -C ffmpeg-src fetch --depth 1 origin 8dba37253f985bcba0b9dd26adb94a7593124796
git -C ffmpeg-src checkout --detach FETCH_HEAD
cd ffmpeg-src
./configure --prefix="$prefix" --disable-autodetect --disable-everything \
  --disable-programs --disable-doc --disable-debug --disable-network \
  --disable-avdevice --disable-avfilter --disable-swresample --disable-swscale \
  --enable-static --disable-shared --enable-avcodec --enable-avformat --enable-avutil \
  --enable-decoder=hevc,aac --enable-parser=hevc,aac \
  --enable-demuxer=mov --enable-protocol=file --enable-small \
  --extra-cflags="-O2" --extra-ldflags="-static-libgcc"
make -j4
make install
cd "$root"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:$PKG_CONFIG_PATH"
gcc -std=c11 -O2 -Wall -Wextra -Wno-unused-function -municode \
  scripts/color-proof/color-probe.c -o native-out/color-probe.exe \
  $(pkg-config --cflags --libs libplacebo) \
  $(pkg-config --cflags --libs --static libavformat libavcodec libavutil) \
  -static-libgcc -lm
strip native-out/color-probe.exe
for pass in 1 2 3 4 5 6 7 8; do
  for item in native-out/*.exe native-out/*.dll; do
    [[ -f "$item" ]] || continue
    while read -r dll; do
      [[ -f "/ucrt64/bin/$dll" ]] || continue
      [[ -f "native-out/$dll" ]] || cp "/ucrt64/bin/$dll" native-out/
    done < <(objdump -p "$item" | awk '/DLL Name:/ {print $3}')
  done
done
mkdir -p native-out/licenses
cp ffmpeg-src/COPYING.LGPLv2.1 native-out/licenses/FFmpeg-LGPL-2.1.txt
for pkg in libplacebo libdovi shaderc spirv-cross lcms2 vulkan-loader gcc-libs; do
  path="/ucrt64/share/licenses/$pkg"
  [[ ! -d "$path" ]] || cp -r "$path" "native-out/licenses/$pkg"
done
pacman -Q > native-out/dependency-versions.txt
sha256sum native-out/*.exe native-out/*.dll > native-out/SHA256SUMS
