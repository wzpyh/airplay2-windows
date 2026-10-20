# airplay2-windows

免费的 **Windows AirPlay 2 音频接收端**。基于 [Shairport Sync](https://github.com/mikebrady/shairport-sync)
和 [NQPTP](https://github.com/mikebrady/nqptp)，跑在 MSYS2/Cygwin 的 POSIX 兼容层上。
电脑会在 iPhone/iPad/Mac 的 AirPlay 菜单里显示成一个音箱，声音从 Windows 的默认音频设备播出。

[English](README.md)

## 为什么做这个

UxPlay 等免费的 Windows 接收端只支持老式的实时 AirPlay 音频。很多 iOS **视频 App** 只会给支持
AirPlay 2 **缓冲音频 + PTP 时钟**的接收端发声音，碰到老式接收端就只发静音。用这个接收端：

- 视频 App（实测 B 站、YouTube）有声音，而且**和手机上的画面同步**（视频留在手机上播）；
- 音乐 App（Apple Music）照常可用；
- 重启后会记住上次的音量（保存在 `config\volume.txt`）。

只有音频，不支持屏幕镜像和 AirPlay 视频。

## 原理

只跑两个程序：

```
                     ┌──────────── shairport-sync.exe ────────────┐
iPhone ──AirPlay 2──▶│ AirPlay 2 服务 ──▶ wasapi 音频后端           │──▶ 默认音频设备
                     │ uxmdnsd 后端（mDNS，UDP 5353）──▶ 局域网     │
                     └────────────────────▲───────────────────────┘
                                          │ 共享内存（时钟）
          PTP（UDP 319/320）◀──▶ nqptp.exe ┘
```

| 组件 | 在 Windows 上需要的改动 |
| --- | --- |
| `nqptp` | 让 `configure` 接受 Cygwin，另修两处（[补丁](patches/nqptp-cygwin.patch)）：共享内存只在 Linux/FreeBSD/macOS 分支里 `mmap`，其他系统上 Shairport Sync 会报 *"The NQPTP service … does not seem to be initialised"*；Cygwin 的 `getifaddrs()` 拿不到网卡 MAC，时钟 ID 改从环境变量 `NQPTP_MAC` 读。 |
| `shairport-sync` | `--with-os=cygwin`，另加两个后端（[补丁](patches/shairport-sync-windows.patch)）。**`wasapi`**：以共享模式播放到 Windows 默认音频设备，并上报真实的输出延迟（缓冲中的帧数 + 流延迟），Shairport Sync 靠它和 AirPlay 时钟保持同步；当前设备消失时会自动重新打开默认设备。**`uxmdnsd`**：在进程内运行 UxPlay 的精简 mDNS 响应器（`lib/mdnsd`），发布 AirPlay 2 和 AirPlay 1 服务——Windows 上没有 Avahi，自带的 tinysvcmdns 又发布不了 AirPlay 2。 |

发布的 zip 是自包含的：Cygwin/MSYS 运行库 DLL 放在 `usr\bin` 里，整个文件夹就是一个小型 MSYS 根目录，
不需要再装任何东西。

## 安装

环境要求：Windows 10/11 x64；Windows 里家庭网络设为**专用网络（Private）**
（设置 → 网络 → 你的 Wi-Fi/以太网 → 网络配置文件类型），防火墙规则只对 Private 放行。

1. 从 [Releases](../../releases) 下载 `airplay2-windows-<版本>-x64.zip`，解压到一个固定的位置，
   比如 `C:\Program Files\airplay2-windows`。
2. 在该文件夹里**以管理员身份**打开 PowerShell，运行：
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -Name "客厅电脑"
   ```
   可选参数：`-Port 7000`（如果 7000 已经被占用，比如装了 UxPlay，就换一个）。
3. 在手机的 AirPlay 菜单里选这个名字。

`install.ps1` 会生成 `config\shairport-sync.conf`、添加防火墙规则，并注册计划任务 `airplay2-windows`：
开机以 SYSTEM 身份启动（不用登录），接收端退出时自动重启。日志在 `logs\`。要改名字或端口就重新运行它；
卸载用管理员身份运行 `uninstall.ps1`。

## 从源码编译

需要 [MSYS2](https://www.msys2.org/)。在 *MSYS2 MSYS* 终端里运行（不是 UCRT64/MINGW64）：

```sh
bash scripts/fetch.sh     # 下载锁定版本的源码包并校验 sha256（任何能上网的机器都行）
bash scripts/build.sh     # 装 pacman 依赖、编译全部组件、在 dist/airplay2-windows/ 生成绿色包
```

`build.sh` 依次编译 libsodium、libconfig、libplist、精简版 FFmpeg（只有 AAC/ALAC 解码器）、nqptp、
以及带 WASAPI 和内置 mDNS 后端的 shairport-sync，然后由 `scripts/bundle.sh` 把它们连同所需的 DLL、许可证文本和
第三方声明一起放进 `dist/airplay2-windows/` 并打成 zip。FFmpeg 最慢，并行任务多时要吃几 GB 内存，
小内存机器用 `JOBS=2 bash scripts/build.sh`。Release 由 GitHub Actions 按同样的流程编译
（`.github/workflows/release.yml`）。

## 排障

| 现象 | 检查 |
| --- | --- |
| 手机上看不到 | 网络必须是 Private；iPhone 会缓存列表，关开一次 Wi-Fi；如果有别的程序独占了 UDP 5353，广播会发不出去，看 `logs\shairport-sync.log`。 |
| 能看到、能连上，但没声音 | 看 `logs\shairport-sync.log`。如果报 NQPTP "not initialised"，说明 nqptp 补丁没打上。 |
| 声音从别的设备出来 | 它总是播放到 Windows 的默认输出设备。当前设备消失时，下一段音频会自动改用新的默认设备。 |
| 7000 端口被占用（比如装了 UxPlay） | 重新运行 `install.ps1 -Port 7010`，两个可以并存。 |

日志里的 `Can't create a device ID -- no valid MAC address can be found` 不影响使用：
Shairport Sync 会改用服务名生成设备 ID。

## 测试环境

| 组件 | 版本 |
| --- | --- |
| Windows | 11 25H2（Steam Deck OLED） |
| MSYS2（编译） | msys2-runtime 3.6.11，gcc 15.3 |
| shairport-sync | 5.6（`cf6998d`） |
| nqptp | 1.2.9（`ab8fb67`） |
| UxPlay（只用 mdnsd） | 1.74（`d9e293d`） |
| FFmpeg | 7.1.5 |
| 客户端 | iPhone，iOS AirPlay 980.77.1 |

## 许可证

本仓库的脚本采用 [MIT 许可证](LICENSE)。补丁修改的是对应的上游项目，
按上游的许可证发布：nqptp（GPL-2.0）、shairport-sync（见其 `LICENSES`）。`uxmdnsd` 后端编译进了 UxPlay 的
`lib/mdnsd`（LGPL-2.1）。Release 的 zip 里包含第三方二进制文件，zip 内的 `THIRD_PARTY.txt` 和
`licenses\` 列出了它们及各自的许可证；每个 Release 都附有对应的 `-sources.zip`，内含相应源码。
