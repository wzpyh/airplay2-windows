# airplay2-windows

A free **AirPlay 2 audio receiver for Windows**, built from
[Shairport Sync](https://github.com/mikebrady/shairport-sync) and
[NQPTP](https://github.com/mikebrady/nqptp) running on the MSYS2/Cygwin POSIX layer.
Your PC shows up as a speaker in the iPhone/iPad/Mac AirPlay menu and plays through
whatever Windows audio device is the default.

[中文说明](README.zh-CN.md)

## Why

Free Windows receivers such as UxPlay speak the older real-time AirPlay audio protocol. Many
iOS **video apps** only send sound to receivers that support AirPlay 2 *buffered audio* with
*PTP timing*; with an older receiver they stream silence. With this receiver:

- video apps (tested: Bilibili, YouTube) play sound, **in sync with the picture**
  on the phone (the video stays on the phone);
- music apps (Apple Music) work as usual.

It is audio only: no screen mirroring, no AirPlay video.

## How it works

```
iPhone ──AirPlay 2──▶ shairport-sync ──PCM (stdout)──▶ gst-launch … wasapisink ──▶ default audio device
                         │  ▲
       PTP (UDP 319/320) │  │ shared memory
                         ▼  │
                        nqptp

shairport-sync ──TXT records (pipe)──▶ mdns-pub.exe ──mDNS (UDP 5353)──▶ LAN
```

| Part | What was needed on Windows |
| --- | --- |
| `nqptp` | Builds as-is once `configure` accepts Cygwin, plus two fixes ([patch](patches/nqptp-cygwin.patch)): the shared-memory block was only `mmap`ed for Linux/FreeBSD/macOS (on any other OS Shairport Sync reports *"The NQPTP service … does not seem to be initialised"*), and the clock identity MAC comes from `NQPTP_MAC` because Cygwin's `getifaddrs()` has no link-layer entries. |
| `shairport-sync` | `--with-os=cygwin`, and a new mDNS backend `winpipe` ([patch](patches/shairport-sync-cygwin.patch)): there is no Avahi on Windows, and the bundled tinysvcmdns cannot advertise the AirPlay 2 service. |
| `mdns-pub.exe` | Small native publisher built on UxPlay's minimal mDNS responder (`lib/mdnsd`). Reads the records from Shairport Sync over a pipe and answers mDNS queries; sends goodbye packets when the pipe closes. |
| Audio out | Shairport Sync's `stdout` backend piped into GStreamer's `wasapisink`. |

## Requirements

- Windows 10/11 x64
- [MSYS2](https://www.msys2.org/) installed in `C:\msys64` (another path works; pass `-MsysRoot`)
- Your home network set to **Private** in Windows (the firewall rules only open the Private profile)

## Install

1. **Get the sources** (any machine with internet; checksums are verified):
   ```sh
   bash scripts/fetch.sh
   ```
2. **Build** in an *MSYS2 MSYS* shell (not UCRT64/MINGW64). It installs the needed pacman packages,
   builds libsodium, libconfig, libplist, a minimal FFmpeg, nqptp, shairport-sync and mdns-pub,
   and installs them into `/usr/local`. Takes a few minutes on a fast CPU; FFmpeg is the slow
   part and needs a few GB of RAM with many jobs (`JOBS=2 bash scripts/build.sh` on small machines).
   ```sh
   bash scripts/build.sh
   ```
3. **Install the service** from an elevated PowerShell:
   ```powershell
   .\install.ps1 -Name "Living Room PC"      # optional: -Port 7000 -MsysRoot C:\msys64
   ```
   This writes `config/shairport-sync.conf`, adds firewall rules, registers a scheduled task
   `airplay2-windows` that starts at boot as SYSTEM (no logon needed), and starts it.

Pick the name in the AirPlay menu on your phone. Logs are in `logs/`.

To remove: `.\uninstall.ps1` (elevated).

### Building on another machine

`bash scripts/build.sh package` also writes `dist/airplay2-windows-bin.tgz`. Unpack it into
`/usr/local` of another MSYS2 install **with the same `msys2-runtime` version**
(`pacman -Q msys2-runtime`), install the runtime packages there
(`pacman -S libgcrypt openssl popt libutil-linux mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base`),
then run `install.ps1`.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Not listed on the phone | Network profile must be Private. Toggle Wi-Fi on the phone (iOS caches entries). `mdns-pub` must be running. |
| Listed, connects, no sound | `logs/shairport-sync.log`. If NQPTP is "not initialised", the nqptp patch is missing. |
| Sound only after a fresh start, silent after pausing | `wasapisink` must have `sync=false` (see the comment in `runtime/run.sh`). |
| Port 7000 already in use (e.g. UxPlay) | Re-run `install.ps1 -Port 7010`. Both can run side by side. |

`Can't create a device ID -- no valid MAC address can be found` in the log is harmless:
Shairport Sync then derives the device ID from the service name.

## Tested with

| Component | Version |
| --- | --- |
| Windows | 11 25H2 (Steam Deck OLED) |
| MSYS2 | msys2-runtime 3.6.11, gcc 15.3 |
| shairport-sync | 5.6 (`cf6998d`) |
| nqptp | 1.2.9 (`ab8fb67`) |
| UxPlay (mdnsd only) | 1.74 (`d9e293d`) |
| FFmpeg | 7.1.5 |
| Client | iPhone, iOS AirPlay 980.77.1 |

## License

The scripts and `mdns-pub/mdns-pub.c` in this repository are under the [MIT License](LICENSE).
The patches modify, and are distributed under the licenses of, the projects they apply to:
nqptp (GPL-2.0) and shairport-sync (see its `LICENSES`). `mdns-pub` is linked with UxPlay's
`lib/mdnsd` (LGPL-2.1), which is downloaded at build time. No third-party binaries are distributed
here.
