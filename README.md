# STS2-TUI

《杀戮尖塔 2》的全屏终端界面。直接用键盘完成选牌、选怪、路线选择、事件、商店、奖励和存档操作。

## 功能

- 全屏 TUI，支持中英文显示
- 方向键选择卡牌，`Tab` 切换到怪物和状态
- 展示怪物意图、伤害、格挡、Buff 和 Debuff
- 展示卡牌稀有度、升级预览、遗物和药水效果
- 支持事件、商店、篝火、占卜及多阶段 Boss
- 自动保存当前进度，死亡或主动放弃后删除活动存档
- Windows 和 macOS Release 不需要安装 Python

## Windows 下载

1. 打开仓库右侧的 **Releases**。
2. 下载 `STS2-TUI-Windows-x64.zip` 并完整解压。
3. 确保已经通过 Steam 安装《杀戮尖塔 2》和 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。
4. 双击 `STS2-TUI.exe` 或 `STS2-TUI.cmd`。

首次启动会自动查找 Steam 游戏目录，从本机复制所需 DLL，并构建无界面后端。Release 不包含或分发游戏文件。完成首次配置后，之后可直接双击启动。

如果 Steam 安装在无法自动识别的位置，可在 PowerShell 中运行：

```powershell
.\setup.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64"
```

## macOS 下载

1. Apple Silicon Mac 下载 `STS2-TUI-macOS-arm64.zip`，Intel Mac 下载 `STS2-TUI-macOS-x64.zip`。
2. 完整解压，并确保已通过 Steam 安装《杀戮尖塔 2》和 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。
3. 双击 `STS2-TUI.command`，或在终端中运行 `./STS2-TUI`。

首次启动会从本机的 Steam 游戏目录复制所需 DLL、打补丁并构建后端。如果 macOS 阻止打开下载的未公证程序，可在系统设置的“隐私与安全性”中选择“仍要打开”，或对解压目录执行：

```bash
xattr -dr com.apple.quarantine STS2-TUI-macOS-arm64
```

如果 Steam 安装在无法自动识别的位置：

```bash
./setup.sh "/path/to/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
```

## 操作

| 按键 | 操作 |
| --- | --- |
| 方向键 | 移动选择 |
| Enter | 确认、出牌 |
| Tab | 在手牌与怪物状态之间切换 |
| E / Space | 结束回合 |
| P | 查看或使用药水 |
| D | 查看牌组 |
| R | 查看遗物 |
| M | 查看路线 |
| A | 放弃本局并删除活动存档 |
| Q | 保存并退出 |

## 从源码运行

需要 Python 3.9+、.NET 9 SDK，以及 Steam 版《杀戮尖塔 2》。

macOS / Linux：

```bash
./setup.sh
python3 python/play.py
```

Windows：

```powershell
.\setup.ps1
py -3 python\play.py
```

## 制作 Release

仓库内的 GitHub Actions 工作流会用 PyInstaller 生成 Windows x64、macOS arm64 和 macOS x64 的免 Python 发行包。

手动构建测试包：进入仓库的 **Actions → Build Windows release → Run workflow**，完成后在该次运行的 Artifacts 中下载 ZIP。

正式发版：创建并推送 `v*` 标签，工作流会自动创建 GitHub Release 并上传各平台 ZIP。

```bash
git tag v0.1.0
git push origin v0.1.0
```

## 存档与日志

- 活动存档：`saves/current_run.save`
- 日志默认关闭；启动时开启日志后写入 `logs/`
- `Q` 或 `Ctrl+C` 会保存退出
- 角色死亡或确认放弃后会删除活动存档

## 上游项目

本项目基于 [wuhao21/sts2-cli](https://github.com/wuhao21/sts2-cli)。

## License

见 [LICENSE](LICENSE)。本项目不包含《杀戮尖塔 2》的游戏文件，使用前需要合法安装原游戏。

<details>
<summary>English</summary>

STS2-TUI is a full-screen terminal interface for Slay the Spire 2. It provides keyboard-driven combat, targeting, map navigation, events, shops, rewards, and save management.

### Windows and macOS

Download `STS2-TUI-Windows-x64.zip` from **Releases**, extract it, then double-click `STS2-TUI.exe` or `STS2-TUI.cmd`. Python is bundled and does not need to be installed. The first launch requires the Steam game installation and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to prepare the local headless backend.

On macOS, download `STS2-TUI-macOS-arm64.zip` for Apple Silicon or `STS2-TUI-macOS-x64.zip` for Intel, extract it, then open `STS2-TUI.command`. Python is bundled; the Steam game installation and .NET 9 SDK are still required for first-time setup.

The release never includes game DLLs. They are copied from the user's own Steam installation during first-time setup.

</details>
