# airplay2-windows

免费的 **Windows AirPlay 2 音频接收端**。基于 [Shairport Sync](https://github.com/mikebrady/shairport-sync)
和 [NQPTP](https://github.com/mikebrady/nqptp)，跑在 MSYS2/Cygwin 的 POSIX 兼容层上。
电脑会在 iPhone/iPad/Mac 的 AirPlay 菜单里显示成一个音箱，声音从 Windows 的默认音频设备播出。

[English](README.md)

## 为什么做这个

UxPlay 等免费的 Windows 接收端只支持老式的实时 AirPlay 音频。很多 iOS **视频 App** 只会给支持
AirPlay 2 **缓冲音频 + PTP 时钟**的接收端发声音，碰到老式接收端就只发静音。用这个接收端：

- 视频 App（实测 B 站、YouTube）有声音，而且**和手机上的画面同步**（视频留在手机上播）；
- 音乐 App（Apple Music）照常可用。

只有音频，不支持屏幕镜像和 AirPlay 视频。

## 原理

```
iPhone ──AirPlay 2──▶ shairport-sync ──PCM(stdout)──▶ gst-launch … wasapisink ──▶ 默认音频设备
                         │  ▲
         PTP(UDP 319/320)│  │ 共享内存
                         ▼  │
                        nqptp

shairport-sync ──TXT 记录(管道)──▶ mdns-pub.exe ──mDNS(UDP 5353)──▶ 局域网
```

| 组件 | 在 Windows 上需要的改动 |
| --- | --- |
| `nqptp` | 让 `configure` 接受 Cygwin，另修两处（[补丁](patches/nqptp-cygwin.patch)）：共享内存只在 Linux/FreeBSD/macOS 分支里 `mmap`，其他系统上 Shairport Sync 会报 *"The NQPTP service … does not seem to be initialised"*；Cygwin 的 `getifaddrs()` 拿不到网卡 MAC，时钟 ID 改从环境变量 `NQPTP_MAC` 读。 |
| `shairport-sync` | `--with-os=cygwin`，新增 mDNS 后端 `winpipe`（[补丁](patches/shairport-sync-cygwin.patch)）：Windows 上没有 Avahi，自带的 tinysvcmdns 又发布不了 AirPlay 2 服务。 |
| `mdns-pub.exe` | 基于 UxPlay 的精简 mDNS 响应器（`lib/mdnsd`）写的原生小程序。通过管道接收 Shairport Sync 的服务记录并应答 mDNS 查询；管道关闭时发送下线包。 |
| 声音输出 | Shairport Sync 的 `stdout` 后端通过管道送给 GStreamer 的 `wasapisink`。 |

## 环境要求

- Windows 10/11 x64
- [MSYS2](https://www.msys2.org/)，装在 `C:\msys64`（装在别处也行，安装时加 `-MsysRoot`）
- Windows 里家庭网络设为**专用网络（Private）**，防火墙规则只对 Private 放行

## 安装

1. **下载源码**（任何能上网的机器都行，会校验 sha256）：
   ```sh
   bash scripts/fetch.sh
   ```
2. **编译**：在 *MSYS2 MSYS* 终端里跑（不是 UCRT64/MINGW64）。脚本会装好需要的 pacman 包，
   依次编译 libsodium、libconfig、libplist、精简版 FFmpeg、nqptp、shairport-sync、mdns-pub，
   装到 `/usr/local`。快的 CPU 几分钟就好；FFmpeg 最慢，并行任务多时要吃几 GB 内存，
   小内存机器用 `JOBS=2 bash scripts/build.sh`。
   ```sh
   bash scripts/build.sh
   ```
3. **安装为服务**：用管理员 PowerShell 运行
   ```powershell
   .\install.ps1 -Name "客厅电脑"      # 可选参数：-Port 7000 -MsysRoot C:\msys64
   ```
   脚本会生成 `config/shairport-sync.conf`、添加防火墙规则、注册计划任务 `airplay2-windows`
   （开机以 SYSTEM 身份启动，不用登录），并立即启动。

然后在手机的 AirPlay 菜单里选这个名字。日志在 `logs/`。

卸载：用管理员 PowerShell 运行 `.\uninstall.ps1`。

### 在别的机器上编译

`bash scripts/build.sh package` 会额外生成 `dist/airplay2-windows-bin.tgz`。把它解压到另一份
MSYS2 的 `/usr/local`，前提是两边 **`msys2-runtime` 版本相同**（`pacman -Q msys2-runtime`）。
目标机器还要装运行时依赖
（`pacman -S libgcrypt openssl popt libutil-linux mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base`），
然后运行 `install.ps1`。

## 排障

| 现象 | 检查 |
| --- | --- |
| 手机上看不到 | 网络必须是 Private；iPhone 会缓存列表，关开一次 Wi-Fi；`mdns-pub` 进程要在。 |
| 能看到、能连上，但没声音 | 看 `logs/shairport-sync.log`。如果报 NQPTP "not initialised"，说明 nqptp 补丁没打上。 |
| 刚启动时有声音，暂停或切 App 后就没了 | `wasapisink` 必须是 `sync=false`（原因见 `runtime/run.sh` 里的注释）。 |
| 7000 端口被占用（比如装了 UxPlay） | 重新运行 `install.ps1 -Port 7010`，两个可以并存。 |

日志里的 `Can't create a device ID -- no valid MAC address can be found` 不影响使用：
Shairport Sync 会改用服务名生成设备 ID。

## 测试环境

| 组件 | 版本 |
| --- | --- |
| Windows | 11 25H2（Steam Deck OLED） |
| MSYS2 | msys2-runtime 3.6.11，gcc 15.3 |
| shairport-sync | 5.6（`cf6998d`） |
| nqptp | 1.2.9（`ab8fb67`） |
| UxPlay（只用 mdnsd） | 1.74（`d9e293d`） |
| FFmpeg | 7.1.5 |
| 客户端 | iPhone，iOS AirPlay 980.77.1 |

## 许可证

本仓库的脚本和 `mdns-pub/mdns-pub.c` 采用 [MIT 许可证](LICENSE)。补丁修改的是对应的上游项目，
按上游的许可证发布：nqptp（GPL-2.0）、shairport-sync（见其 `LICENSES`）。`mdns-pub` 链接 UxPlay 的
`lib/mdnsd`（LGPL-2.1），编译时下载。本仓库不分发任何第三方二进制文件。
