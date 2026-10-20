#!/usr/bin/env bash
# Runs the receiver: nqptp (PTP clock) + shairport-sync (PCM on stdout) -> GStreamer -> WASAPI.
# Started by runtime/start.ps1 inside MSYS2 (MSYSTEM=MSYS). Supervises itself: if any part
# exits, everything is restarted after 3 seconds.
#
# Environment:
#   NQPTP_MAC   MAC address used for the PTP clock identity (start.ps1 sets it from the
#               adapter that holds the default route; Cygwin cannot read MACs itself)
root="$(cd "$(dirname "$0")/.." && pwd)"
conf="$root/config/shairport-sync.conf"
logs="$root/logs"
mkdir -p "$logs"
export PATH="/usr/local/bin:$PATH"

if [ ! -f "$conf" ]; then
  echo "missing $conf -- run install.ps1 first" >&2
  exit 1
fi

while true; do
  pkill -x nqptp 2>/dev/null
  pkill -x shairport-sync 2>/dev/null
  pkill -x mdns-pub 2>/dev/null
  rm -f /dev/shm/nqptp*

  nqptp > "$logs/nqptp.log" 2>&1 &
  nqptp_pid=$!
  sleep 1

  # wasapisink sync=false: shairport-sync already paces its output against the PTP clock.
  # With sync=true, after a pause or app switch the parsed timestamps fall behind the pipeline
  # clock and wasapisink silently drops every buffer (levels look fine, nothing is heard).
  shairport-sync -c "$conf" 2> "$logs/shairport-sync.log" \
    | /ucrt64/bin/gst-launch-1.0.exe -q fdsrc fd=0 \
        ! rawaudioparse use-sink-caps=false format=pcm pcm-format=s16le sample-rate=44100 num-channels=2 \
        ! queue max-size-time=200000000 ! audioconvert ! audioresample ! wasapisink sync=false \
        > "$logs/gstreamer.log" 2>&1

  echo "$(date '+%F %T') pipeline exited, restarting" >> "$logs/restarts.log"
  kill "$nqptp_pid" 2>/dev/null
  sleep 3
done
