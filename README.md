# STS2-TUI

<details open>
<summary><b>English</b></summary>

A CLI for Slay the Spire 2.

Based on [wuhao21/sts2-cli](https://github.com/wuhao21/sts2-cli), with a full-screen terminal UI and additional gameplay support.

Runs the real game engine headless in your terminal — all damage, card effects, enemy AI, relics, and RNG are identical to the actual game. Everything is unlocked from the start: all characters, cards, relics, potions, and ascension levels — no timeline progression required.

![demo](docs/demo_en.gif)

## Setup

Requirements:
- [Slay the Spire 2](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/) on Steam
- [.NET 9 SDK and runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Python 3.9+

```bash
git clone https://github.com/Yiniann/STS2-TUI.git
cd STS2-TUI
./setup.sh      # copies DLLs from Steam → IL patches → builds
```

Or just run `python3 python/play.py` — it auto-detects and sets up on first run.

## Play

```bash
python3 python/play.py                        # interactive (Chinese)
python3 python/play.py --lang en              # interactive (English)
python3 python/play.py --ascension 10         # Ascension 10
python3 python/play.py --character Silent      # play as Silent
```

Interactive terminals now open a full-screen TUI. Use the arrow keys to select a
card and Enter to play it; targeted cards then let you select an enemy directly.
Press `D`, `P`, `R`, or `M` to inspect the deck, potions, relics, or route, and
`E` to end the turn. When Driftwood enables a card-reward reroll, press `F` to
refresh the choices; `R` remains reserved for relics. Pass `--cli` to use the
original command input interface.
Before each new run, the setup screen lets you choose the character, ascension,
language, seed, and game logging, or load a local/native save. Command-line flags
provide its initial values.
The active run is automatically saved to `saves/current_run.save` at room
checkpoints. Press `Q` or `Ctrl+C` to save and exit, and press `A` to abandon the
run after confirmation. While an active save exists, the start screen only allows
continuing, abandoning, or quitting; a new run becomes available after abandoning
or finishing the current one.
By **Y1niann**.

In the legacy `--cli` interface, type `help` in-game:

```
  help     — show help
  map      — show map
  deck     — show deck
  potions  — show potions
  relics   — show relics
  quit     — save and quit
  abandon  — abandon the run and delete its active save

  Map:     enter path number (0, 1, 2)
  Combat:  card index / e (end turn) / p0 (use potion)
  Reward:  card index / s (skip)
  Rest:    option index
  Event:   option index / leave
  Shop:    c0 (card) / r0 (relic) / p0 (potion) / rm (remove) / leave
```

## JSON Protocol

For programmatic control (AI agents, RL, etc.), communicate via stdin/stdout JSON:

```bash
dotnet run --project src/Sts2Headless/Sts2Headless.csproj
```

```json
{"cmd": "start_run", "character": "Ironclad", "seed": "test", "ascension": 0}
{"cmd": "action", "action": "play_card", "args": {"card_index": 0, "target_index": 0}}
{"cmd": "action", "action": "end_turn"}
{"cmd": "action", "action": "select_map_node", "args": {"col": 3, "row": 1}}
{"cmd": "action", "action": "skip_card_reward"}
{"cmd": "quit"}
```

Each command returns a JSON decision point (`map_select` / `combat_play` / `card_reward` / `rest_site` / `event_choice` / `shop` / `game_over`). All names are in English.

## Game Logs

Game logging is disabled by default. Enable it to record each game state and action with timestamps in a JSONL file under `logs/`. Logs older than 7 days are cleaned up automatically.

```bash
python3 python/play.py --log       # enable logging
```

**When filing a bug report, please attach the relevant log file from `logs/`** — it contains the full step-by-step game state needed to reproduce the issue.

## Supported Characters

| Character | Status |
|---|---|
| Ironclad | Fully playable |
| Silent | Fully playable |
| Defect | Fully playable |
| Necrobinder | Fully playable |
| Regent | Fully playable |

## Architecture

```
Your code (Python / JS / LLM)
    │  JSON stdin/stdout
    ▼
src/Sts2Headless (C#)
    │  RunSimulator.cs
    ▼
sts2.dll (game engine, IL patched)
  + src/GodotStubs (replaces GodotSharp.dll)
  + Harmony patches (localization)
```

</details>

<details>
<summary><b>中文</b></summary>

杀戮尖塔2的命令行版本。

本项目基于 [wuhao21/sts2-cli](https://github.com/wuhao21/sts2-cli)，增加了全屏终端界面和更多游戏流程支持。

在终端里运行真实游戏引擎 — 所有伤害计算、卡牌效果、敌人AI、遗物触发、随机数都和真实游戏一致。所有内容从一开始就全部解锁：全角色、全卡牌、全遗物、全药水、全渐进难度等级，无需时间线进度。

![demo](docs/demo_zh.gif)

## 安装

需要：
- [Slay the Spire 2](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/) (Steam)
- [.NET 9 SDK 和运行时](https://dotnet.microsoft.com/download/dotnet/9.0)
- Python 3.9+

```bash
git clone https://github.com/Yiniann/STS2-TUI.git
cd STS2-TUI
./setup.sh      # 从 Steam 复制 DLL → IL patch → 编译
```

或者直接运行 `python3 python/play.py`，首次会自动完成 setup。

## 玩

```bash
python3 python/play.py                        # 中文交互模式
python3 python/play.py --lang en              # English
python3 python/play.py --ascension 10         # 渐进难度 10
python3 python/play.py --character Silent      # 选择静默猎手
```

交互终端现在默认进入全屏 TUI。方向键选择手牌，回车出牌；需要指定目标时，
继续用方向键选择敌人。按 `D`、`P`、`R`、`M` 可查看牌组、药水、遗物和路线，
按 `E` 结束回合。浮木允许重掷卡牌奖励时按 `F` 刷新选项，`R` 仍用于查看遗物。
使用 `--cli` 可继续使用原来的命令输入界面。
每次新游戏前可在设置页选择英雄、渐进难度、语言、种子和游戏日志，也可直接读取
本地或游戏原生存档；命令行参数会作为设置页的初始值。
游戏会在房间检查点自动保存到 `saves/current_run.save`。按 `Q` 或 `Ctrl+C`
保存并退出，按 `A` 确认后放弃本局。有活动存档时，启动页只允许继续、放弃或退出；
只有放弃或完成当前游戏后，才能开始新的一局。
By **Y1niann**.

在原来的 `--cli` 界面中，输入 `help` 查看所有命令：

```
  help     — 帮助
  map      — 显示地图
  deck     — 查看牌组
  potions  — 查看药水
  relics   — 查看遗物
  quit     — 保存并退出
  abandon  — 放弃本局并删除活动存档

  地图:    输入编号 (0, 1, 2)
  战斗:    输入卡牌编号 / e 结束回合 / p0 使用药水
  奖励:    输入卡牌编号 / s 跳过
  休息:    输入选项编号
  事件:    输入选项编号 / leave 离开
  商店:    c0 买卡 / r0 买遗物 / p0 买药水 / rm 移除 / leave 离开
```

## 角色支持

| 角色 | 状态 |
|---|---|
| 铁甲战士 (Ironclad) | 完全可玩 |
| 静默猎手 (Silent) | 完全可玩 |
| 故障机器人 (Defect) | 完全可玩 |
| 亡灵契约师 (Necrobinder) | 完全可玩 |
| 储君 (Regent) | 完全可玩 |

## JSON 协议

除了交互模式，也可以通过 stdin/stdout JSON 协议编程控制（写 AI agent、RL 训练等）：

```bash
dotnet run --project src/Sts2Headless/Sts2Headless.csproj
```

```json
{"cmd": "start_run", "character": "Ironclad", "seed": "test", "ascension": 0}
{"cmd": "action", "action": "play_card", "args": {"card_index": 0, "target_index": 0}}
{"cmd": "action", "action": "end_turn"}
{"cmd": "action", "action": "select_map_node", "args": {"col": 3, "row": 1}}
{"cmd": "action", "action": "skip_card_reward"}
{"cmd": "quit"}
```

每个命令返回一个 JSON decision point（`map_select` / `combat_play` / `card_reward` / `rest_site` / `event_choice` / `shop` / `game_over`），所有名称为英文。

## 游戏日志

游戏日志默认关闭。开启后，每局游戏会记录到 `logs/` 目录下的 JSONL 文件中，包含每一步的游戏状态和操作以及时间戳。超过 7 天的旧日志会自动清理。

```bash
python3 python/play.py --log       # 开启日志
```

**提交 bug 报告时，请附上 `logs/` 中对应的日志文件** — 它包含了复现问题所需的完整游戏步骤。

## 架构

```
你的代码 (Python / JS / LLM)
    │  JSON stdin/stdout
    ▼
src/Sts2Headless (C#)
    │  RunSimulator.cs
    ▼
sts2.dll (游戏引擎, IL patched)
  + src/GodotStubs (替代 GodotSharp.dll)
  + Harmony patches (本地化)
```

</details>
