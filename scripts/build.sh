#!/usr/bin/env bash
# Build the AirPlay 2 receiver stack for Windows inside MSYS2.
#
#   Run from an "MSYS2 MSYS" shell (not UCRT64/MINGW64): nqptp and shairport-sync need the
#   POSIX layer (fork, shm_open, mmap). mdns-pub is built natively with the UCRT64 compiler.
#
#   scripts/build.sh    install packages, build everything into /usr/local, then stage a
#                       standalone bundle in dist/ (scripts/bundle.sh) that runs without MSYS2
#
# Sources come from ../sources (see fetch.sh); nothing is downloaded here except pacman packages.
set -euo pipefail

if [ "${MSYSTEM:-}" != "MSYS" ]; then
  echo "error: run this from an MSYS2 MSYS shell (MSYSTEM=MSYS), not ${MSYSTEM:-unknown}" >&2
  exit 1
fi

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"
src="$root/sources"
work="$root/build"
prefix=/usr/local
jobs="${JOBS:-$(nproc)}"
export PATH="$prefix/bin:$PATH" PKG_CONFIG_PATH="$prefix/lib/pkgconfig"

step() { echo; echo "=== $1 ($(date +%T))"; }
log() { echo "    log: $1"; }

step "checking sources"
(cd "$src" && sha256sum -c --quiet "$here/SHA256SUMS")

step "installing MSYS2 packages"
pacman -S --needed --noconfirm \
  autotools gcc make pkgconf diffutils tar xz flex bison texinfo vim patch libarchive git \
  libutil-linux-devel libgcrypt-devel openssl-devel popt-devel \
  mingw-w64-ucrt-x86_64-gcc \
  mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base >/dev/null

mkdir -p "$work"
cd "$work"

unpack() { # unpack <tarball> <dir>
  rm -rf "$2" && mkdir "$2"
  tar xf "$src/$1" -C "$2" --strip-components=1
}

step "libsodium"
unpack libsodium.tar.gz libsodium
(cd libsodium && ./configure --prefix=$prefix --disable-static && make -j"$jobs" && make install) > libsodium.log 2>&1 || { log libsodium.log; exit 1; }

step "libconfig"
unpack libconfig.tar.gz libconfig
(cd libconfig && autoreconf -fi && ./configure --prefix=$prefix --disable-static --disable-cxx --disable-examples --disable-tests \
  && make -j"$jobs" && make install) > libconfig.log 2>&1 || { log libconfig.log; exit 1; }

step "libplist"
unpack libplist.tar.bz2 libplist
(cd libplist && ./configure --prefix=$prefix --disable-static --without-cython && make -j"$jobs" && make install) > libplist.log 2>&1 || { log libplist.log; exit 1; }

step "ffmpeg (AAC/ALAC decoders + swresample only)"
unpack ffmpeg.tar.xz ffmpeg
(cd ffmpeg && ./configure --target-os=cygwin --prefix=$prefix --enable-shared --disable-static --disable-programs --disable-doc \
  --disable-asm --disable-everything --enable-decoder=aac,aac_fixed,alac,pcm_s16le --enable-parser=aac --enable-swresample \
  --enable-avformat --disable-avdevice --disable-avfilter --disable-swscale --disable-network \
  && make -j"$jobs" && make install) > ffmpeg.log 2>&1 || { log ffmpeg.log; exit 1; }

step "nqptp"
unpack nqptp.tar.gz nqptp
(cd nqptp && patch -p1 < "$root/patches/nqptp-cygwin.patch" && autoreconf -fi && ./configure --prefix=$prefix \
  && make -j"$jobs" && cp nqptp.exe $prefix/bin/) > nqptp.log 2>&1 || { log nqptp.log; exit 1; }

step "shairport-sync (AirPlay 2)"
unpack shairport-sync.tar.gz shairport-sync
(cd shairport-sync && patch -p1 < "$root/patches/shairport-sync-cygwin.patch" && autoreconf -fi \
  && ./configure --prefix=$prefix --sysconfdir=$prefix/etc --with-os=cygwin --with-airplay-2 --with-ssl=openssl \
       --with-stdout --with-winpipe-mdns \
  && make -j"$jobs" && cp shairport-sync.exe $prefix/bin/) > shairport-sync.log 2>&1 || { log shairport-sync.log; exit 1; }

step "mdns-pub (native, UCRT64)"
unpack uxplay.tar.gz uxplay
PATH="/ucrt64/bin:$PATH" /ucrt64/bin/gcc -O2 -Wall -static -o $prefix/bin/mdns-pub.exe \
  "$root/mdns-pub/mdns-pub.c" uxplay/lib/mdnsd/mdnsd.c -Iuxplay/lib -Iuxplay/lib/mdnsd -lws2_32 -liphlpapi \
  > mdns-pub.log 2>&1 || { log mdns-pub.log; exit 1; }

step "pcm-play (native, UCRT64)"
PATH="/ucrt64/bin:$PATH" /ucrt64/bin/gcc -O2 -Wall -static -o $prefix/bin/pcm-play.exe   "$root/pcm-play/pcm-play.c" -lole32 > pcm-play.log 2>&1 || { log pcm-play.log; exit 1; }

step "bundle"
shairport-sync -V
bash "$here/bundle.sh"
