#!/usr/bin/env bash
# Stage a standalone bundle in dist/airplay2-windows/ and zip it. Run in MSYS2 MSYS after build.sh.
#
# Layout (the bundle is its own Cygwin/MSYS "root": msys-2.0.dll lives in usr/bin, so /dev/shm
# resolves to <bundle>/dev/shm and no MSYS2 installation is needed on the target machine):
#
#   install.ps1  uninstall.ps1  README.md  README.zh-CN.md  LICENSE  THIRD_PARTY.txt
#   config/shairport-sync.conf.in
#   runtime/service.ps1
#   usr/bin/      nqptp.exe shairport-sync.exe + DLLs
#   dev/shm/ tmp/ logs/
#   licenses/     license texts of everything shipped in usr/bin
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"
work="$root/build"
version="${VERSION:-$(git -C "$root" describe --tags --always --dirty 2>/dev/null || echo dev)}"
out="$root/dist/airplay2-windows"

rm -rf "$out" && mkdir -p "$out"/{usr/bin,dev/shm,tmp,logs,config,runtime,licenses}

cp "$root"/{install.ps1,uninstall.ps1,README.md,README.zh-CN.md,LICENSE} "$out/"
cp "$root/config/shairport-sync.conf.in" "$out/config/"
cp "$root/runtime/service.ps1" "$out/runtime/"
touch "$out/dev/shm/.keep" "$out/tmp/.keep" "$out/logs/.keep"

# Programs
for exe in nqptp shairport-sync; do
  cp "/usr/local/bin/$exe.exe" "$out/usr/bin/"
done

# DLLs the programs need
dlls=$(for exe in nqptp shairport-sync; do ldd "/usr/local/bin/$exe.exe"; done \
  | awk '$3 ~ "^/usr/" {print $3}' | sort -u)
for dll in $dlls; do cp "$dll" "$out/usr/bin/"; done

# Licenses and third-party notice
notice="$out/THIRD_PARTY.txt"
{
  echo "airplay2-windows $version -- third-party components"
  echo
  echo "Built from source (exact tarballs and patches are in the release's sources zip):"
  echo "  shairport-sync, nqptp, libsodium, libconfig, libplist, FFmpeg (LGPL build), UxPlay lib/mdnsd"
  echo
  echo "From MSYS2 packages (source: https://github.com/msys2/MSYS2-packages):"
} > "$notice"
# MSYS2 packages whose DLLs are shipped: notice line, license texts, and the matching source
# package (dist/msys2-sources.txt, downloaded into the release's sources zip by the CI workflow)
src_base() { # binary package -> MSYS2 source package
  case "$1" in
    libopenssl) echo openssl ;; gcc-libs) echo gcc ;; libintl) echo gettext ;;
    libutil-linux) echo util-linux ;; *) echo "$1" ;;
  esac
}
: > "$root/dist/msys2-sources.txt"
for dll in $dlls; do
  case "$dll" in
    /usr/local/*) continue ;;
  esac
  pkg=$(pacman -Qoq "$dll")
  ver=$(pacman -Q "$pkg" | cut -d' ' -f2)
  spdx=$(pacman -Qi "$pkg" | sed -n 's/^Licenses *: *//p')
  echo "  $(basename "$dll")  <-  $pkg $ver  ($spdx)  source: $(src_base "$pkg")-$ver.src.tar.zst" >> "$notice"
  # libgcc_s is covered by the GCC Runtime Library Exception: no source obligation
  [ "$pkg" = gcc-libs ] || echo "$(src_base "$pkg")-$ver.src.tar.zst" >> "$root/dist/msys2-sources.txt"
  mkdir -p "$out/licenses/$pkg"
  if [ -d "/usr/share/licenses/$pkg" ]; then
    cp -r "/usr/share/licenses/$pkg/." "$out/licenses/$pkg/"
  fi
  { pacman -Qlq "$pkg" | grep -iE '/[^/]*(copying|licen[cs]e)[^/]*$' | while read -r f; do
    if [ -f "$f" ]; then cp "$f" "$out/licenses/$pkg/"; fi
  done; } || true
done
sort -u -o "$root/dist/msys2-sources.txt" "$root/dist/msys2-sources.txt"
{
  echo
  echo "Full texts of GPL-2.0, GPL-3.0, LGPL-2.1 and LGPL-3.0 are in licenses/common/."
  echo "Corresponding source code for every component above is published with each release"
  echo "(airplay2-windows-<version>-sources.zip)."
} >> "$notice"

copy_license() { # copy_license <name> <file in build dir>...
  local name="$1"; shift
  mkdir -p "$out/licenses/$name"
  local found=0
  for f in "$@"; do
    if [ -f "$work/$f" ]; then cp "$work/$f" "$out/licenses/$name/"; found=1; fi
  done
  if [ "$found" = 0 ]; then echo "error: no license file for $name in $work" >&2; exit 1; fi
}
copy_license shairport-sync shairport-sync/LICENSES
copy_license nqptp nqptp/LICENSE nqptp/COPYING
copy_license libsodium libsodium/LICENSE
copy_license libconfig libconfig/COPYING.LIB libconfig/LICENSE
copy_license libplist libplist/COPYING.LESSER libplist/COPYING
copy_license ffmpeg ffmpeg/COPYING.LGPLv2.1 ffmpeg/LICENSE.md
copy_license uxplay-mdnsd uxplay/LICENSE
mkdir -p "$out/licenses/common"
for f in COPYING.GPLv2 COPYING.GPLv3 COPYING.LGPLv2.1 COPYING.LGPLv3; do cp "$work/ffmpeg/$f" "$out/licenses/common/"; done
head -n 20 "$work/uxplay/lib/mdnsd/mdnsd.c" > "$out/licenses/uxplay-mdnsd/mdnsd.c-header.txt"

# Zip
mkdir -p "$root/dist"
zip_name="airplay2-windows-$version-x64.zip"
rm -f "$root/dist/$zip_name"
(cd "$root/dist" && bsdtar --format zip -cf "$zip_name" airplay2-windows)
echo "bundle: $root/dist/$zip_name"
