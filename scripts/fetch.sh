#!/usr/bin/env bash
# Download the pinned source tarballs into ../sources and verify their checksums.
# Needs only bash + curl + sha256sum, so it can run on any machine with internet access
# (copy the sources/ folder over if the build machine cannot reach GitHub).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dest="$here/../sources"
mkdir -p "$dest"
cd "$dest"

grep -v '^#' "$here/sources.list" | while IFS='|' read -r name url; do
  [ -n "$name" ] || continue
  if [ -f "$name" ]; then
    echo "have  $name"
  else
    echo "fetch $name"
    curl -fsSL --retry 3 -o "$name.part" "$url"
    mv "$name.part" "$name"
  fi
done

sha256sum -c "$here/SHA256SUMS"
