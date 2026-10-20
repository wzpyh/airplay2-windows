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
- music apps (Apple Music) work as usual;
- the last volume is remembered across restarts (`config\volume.txt`).

It is audio only: no screen mirroring, no AirPlay video.

## How it works

Two programs run:

```
                     ┌──────────── shairport-sync.exe ────────────┐
iPhone ──AirPlay 2──▶│ AirPlay 2 service ──▶ wasapi audio backend  │──▶ default audio device
                     │ uxmdnsd backend (mDNS, UDP 5353) ──▶ LAN    │
                     └────────────────────▲───────────────────────┘
                                          │ shared memory (clock)
         PTP (UDP 319/320) ◀──▶ nqptp.exe ┘
```

| Part | What was needed on Windows |
| --- | --- |
| `nqptp` | Builds as-is once `configure` accepts Cygwin, plus two fixes ([patch](patches/nqptp-cygwin.patch)): the shared-memory block was only `mmap`ed for Linux/FreeBSD/macOS (on any other OS Shairport Sync reports *"The NQPTP service … does not seem to be initialised"*), and the clock identity MAC comes from `NQPTP_MAC` because Cygwin's `getifaddrs()` has no link-layer entries. |
| `shairport-sync` | `--with-os=cygwin` plus two new backends ([patch](patches/shairport-sync-windows.patch)). **`wasapi`** plays to the default Windows audio device in shared mode and reports the real output delay (queued frames + stream latency), which Shairport Sync uses to stay in sync with the AirPlay clock; it reopens the default device if the current one goes away. **`uxmdnsd`** advertises the AirPlay 2 and AirPlay 1 services with UxPlay's minimal mDNS responder (`lib/mdnsd`) running in-process — there is no Avahi on Windows, and the bundled tinysvcmdns cannot advertise AirPlay 2. |

The release zip is self-contained: the Cygwin/MSYS runtime DLLs ship in `usr\bin`, so the folder
is its own little MSYS root and nothing else needs to be installed.

## Install

Requirements: Windows 10/11 x64, and your home network set to **Private** in Windows
(Settings → Network → your Wi-Fi/Ethernet → Network profile type; the firewall rules only open
the Private profile).

1. Download `airplay2-windows-<version>-x64.zip` from
   [Releases](../../releases) and unzip it somewhere permanent, e.g. `C:\Program Files\airplay2-windows`.
2. Open PowerShell **as Administrator** in that folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -Name "Living Room PC"
   ```
   Options: `-Port 7000` (change it if something else, e.g. UxPlay, already uses 7000).
3. Pick the name in the AirPlay menu on your phone.

`install.ps1` writes `config\shairport-sync.conf`, adds firewall rules, and registers a scheduled
task `airplay2-windows` that starts at boot as SYSTEM (no logon needed) and restarts the receiver
if it ever exits. Logs are in `logs\`. Re-run it to change the name or port; run `uninstall.ps1`
(as Administrator) to remove it.

## Building from source

Needs [MSYS2](https://www.msys2.org/). In an *MSYS2 MSYS* shell (not UCRT64/MINGW64):

```sh
bash scripts/fetch.sh     # pinned source tarballs, sha256-checked (any machine with internet)
bash scripts/build.sh     # installs pacman packages, builds everything, stages dist/airplay2-windows/
```

`build.sh` builds libsodium, libconfig, libplist, a minimal FFmpeg (AAC/ALAC decoders only), nqptp,
and shairport-sync (with the WASAPI and built-in mDNS backends), then `scripts/bundle.sh` collects them with the DLLs they
need, license texts and a third-party notice into `dist/airplay2-windows/` and a zip. FFmpeg is the
slow part and needs a few GB of RAM with many parallel jobs; use `JOBS=2 bash scripts/build.sh` on
small machines. Releases are built the same way by GitHub Actions (`.github/workflows/release.yml`).

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Not listed on the phone | Network profile must be Private. Toggle Wi-Fi on the phone (iOS caches entries). Another program holding UDP 5353 exclusively can stop the announcement; see `logs\shairport-sync.log`. |
| Listed, connects, no sound | `logs/shairport-sync.log`. If NQPTP is "not initialised", the nqptp patch is missing. |
| Plays on the wrong device | It always plays to the Windows default output device. If that device goes away, the next audio reopens the new default. |
| Port 7000 already in use (e.g. UxPlay) | Re-run `install.ps1 -Port 7010`. Both can run side by side. |

`Can't create a device ID -- no valid MAC address can be found` in the log is harmless:
Shairport Sync then derives the device ID from the service name.

## Tested with

| Component | Version |
| --- | --- |
| Windows | 11 25H2 (Steam Deck OLED) |
| MSYS2 (build) | msys2-runtime 3.6.11, gcc 15.3 |
| shairport-sync | 5.6 (`cf6998d`) |
| nqptp | 1.2.9 (`ab8fb67`) |
| UxPlay (mdnsd only) | 1.74 (`d9e293d`) |
| FFmpeg | 7.1.5 |
| Client | iPhone, iOS AirPlay 980.77.1 |

## License

The scripts in this repository are under the [MIT License](LICENSE).
The patches modify, and are distributed under the licenses of, the projects they apply to:
nqptp (GPL-2.0) and shairport-sync (see its `LICENSES`). The `uxmdnsd` backend compiles in UxPlay's
`lib/mdnsd` (LGPL-2.1). Release zips contain third-party binaries; `THIRD_PARTY.txt` and
`licenses\` in the zip list them with their licenses, and each release has a matching
`-sources.zip` with the corresponding source code.
