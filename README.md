# 智能倍速（SmartSpeedMod）

> 吉星派对（Astral Party）的**智能倍速** Mod：局内自动加速，只在本人的操作倒计时运行时恢复 `1x`。

---

## 功能

- 游戏局内按设定倍率**加速**运行（默认 `2x`，范围 `1~10`，支持 `2.5` 这类小数）
- **只在本人的操作倒计时运行时**恢复 `1x`；他人倒计时、无倒计时的界面和自动流程不降速。
- 操作提交或倒计时结束后恢复目标倍率，不额外等待；移动演出、战斗 Ready 后的演出继续加速，直到出现下一段本人操作倒计时。
- **结算点赞面板单独排除**：即使游戏仍处于战斗场景，打开结算点赞面板后也保持 `1x`、隐藏 HUD，不受 `timerDetect` 或 `accelerateInGameOnly` 设置影响。
- **仅局内加速**：战斗房间与**吉星小镇（单人模式）**内加速；大厅 / 房间等待 / 结算等界面恒 `1x`，且不显示 HUD
- 游戏内 **HUD 实时显示当前倍率**（右下角，极简单行如 `1x` / `2.5x`，只占一小点位置；配色：金色=加速中、青色=抉择恢复、灰色=关闭）

## 依赖

本项目基于 **[CesiumLoader](https://github.com/higashitaniyume/CesiumLoader)**（Unity IL2CPP 游戏 Mod 加载器）开发，依赖其提供的：

- `CesiumLoader.SDK.dll` —— 倍率控制（`SpeedHack`）、游戏事件（`GameEvents`）、玩家判定（`Players.IsSelf`）、配置（`ModConfig`）、UI 通知等 API
- 原生倍率后端（`version.dll`）—— hook 系统时钟实现全局加速，通过 UDP 接收速度指令
- `AstralParty.Runtime.dll` —— 游戏逻辑类型与 FairyGUI（用于 HUD 绘制）

> 加载器与 SDK 请前往 [CesiumLoader 仓库](https://github.com/higashitaniyume/CesiumLoader) 获取。本项目不含任何游戏资源与加载器本体。

## 安装

1. 编译本项目（见下方「构建」），得到 `SmartSpeedMod.dll`。
2. 将 `SmartSpeedMod.dll`、`SmartSpeedMod.json`、`config.json` 放入游戏 Mod 目录：

   ```
   <游戏目录>\AstralParty_ModLoader\mods\SmartSpeedMod\
   ```

   最终路径应为 `...\mods\SmartSpeedMod\SmartSpeedMod.dll`
3. 确保 **SpeedHackMod**（手动倍速插件）保持启用 —— 本项目复用它的倍率后端。
4. 重启游戏即可生效。

## 项目结构

```
SmartSpeedMod/
├── src/                      # C# 源码
│   ├── ModEntry.cs
│   ├── SmartSpeedController.cs
│   ├── SpeedHud.cs
│   └── TimerDetector.cs
├── tools/
│   ├── fetch-deps.ps1        # 依赖拉取脚本（游戏程序集 + SDK）
│   └── game-refs.json        # 依赖清单（含 SHA256 校验）
├── .github/workflows/
│   └── release.yml           # 自动编译发布工作流
├── SmartSpeedMod.csproj      # 项目文件（引用指向 refs/）
├── SmartSpeedMod.json        # Mod 清单
├── config.json               # 默认配置
├── LICENSE                   # MIT
└── README.md
```

> `refs/` 目录由 `fetch-deps.ps1` 生成，包含编译所需的游戏程序集与 SDK，**不提交进 Git**（见 `.gitignore`）。

## 构建

项目目标框架为 `.NETStandard 2.0`。依赖（游戏程序集 + CesiumLoader SDK）不随仓库分发，构建前先拉取：

```powershell
# 拉取依赖到 refs/
pwsh -NoProfile -File tools/fetch-deps.ps1
```

脚本的下载策略（自动，无需手动配置）：

1. **系统代理**：自动读取 Windows 系统代理（注册表 Internet Settings，如 Clash 的 `127.0.0.1:7890`）或 `http_proxy`/`https_proxy` 环境变量，通过 `curl --proxy` 走代理下载。
2. **直连**：系统代理不可用或未配置时直连。
3. **gh-proxy**：直连失败时回退到 `https://v4.gh-proxy.org/` 代理前缀。

大文件带多次重试（应对代理下 EOF），每个程序集都按 `game-refs.json` 里的 SHA256 校验。可用参数：

```powershell
# 指定 gh-proxy 代理（直连失败时的回退，默认已是 v4.gh-proxy.org）
pwsh -NoProfile -File tools/fetch-deps.ps1 -Proxy https://v4.gh-proxy.org/

# 指定 SDK 版本 / 强制重下
pwsh -NoProfile -File tools/fetch-deps.ps1 -SdkVersion 3.8.7 -Force
```

然后编译：

```bash
dotnet build -c Release
```

产物位于 `bin/SmartSpeedMod.dll`。若未拉取 `refs/`，csproj 会回退到本机游戏目录/提取目录（见 csproj 中的 `LocalGameRoot` / `LocalGameDump`，可按你的环境修改）。

## 自动发布（GitHub Actions）

`.github/workflows/release.yml` 实现了自动化：

- **版本号变化**：推送到 `main`/`master` 且 `SmartSpeedMod.json` 的 `version` 变化时，自动创建并推送 `v<version>` tag。
- **tag 触发**：推送 `v*` tag（如 `v1.0.0`）时，自动：拉取依赖 → 编译 → 打包 `SmartSpeedMod-<版本>.zip` → 创建 GitHub Release 并上传。
- **手动触发**：在 Actions 页面点 "Run workflow"。

版本号以 `SmartSpeedMod.json` 的 `version` 字段为准。需要仓库开启 "Read and write permissions"（Settings → Actions → General → Workflow permissions）。

## 配置（config.json，运行中修改会自动热加载）

| 键 | 默认 | 说明 |
|---|---|---|
| `speed` | 2.0 | 目标倍率，范围 1~10，支持小数 |
| `timerDetect` | true | 本人操作倒计时降速保护；关闭后启用模组时局内始终使用目标倍率 |
| `notify` | true | 切换倍率时弹出游戏内提示 |
| `smartEnabled` | true | 智能模式总开关；关闭则恒定 1x（无热键，改此配置控制） |
| `accelerateInGameOnly` | true | 仅局内加速（战斗房间 / 吉星小镇）；大厅 / 房间 / 结算等界面恒 1x |
| `showHud` | true | 是否显示倍率 HUD（右下角） |
| `hudX` / `hudY` | 0 / 0 | HUD 距右边缘 / 底部的像素距离（0=贴右下角） |
| `hudFontSize` | 30 | HUD 字号（10~64），默认 30，右下角方形小块 |
| `animSyncEnabled` | true | 抉择恢复 1x 时同步 SpeedHackMod 动画状态 |
| `speedUpKey` | 61 | 加速热键 KeyCode（`=`） |
| `speedDownKey` | 45 | 减速热键 KeyCode（`-`） |

## 热键

- `=`：目标倍率 +0.5（长按连续）
- `-`：目标倍率 -0.5（长按连续）

> 加速开关（原 `P` 键）已移除，改为在 `config.json` 里设置 `smartEnabled`。运行中修改会自动热加载。

## 实现原理

- **倒计时判定**：从 `RawAction` 登记本人的操作序号，再与游戏的活动操作计时器匹配。回合通知、方向箭头或商店界面本身不触发降速。
- **恢复倍率**：操作进入游戏完成记录、或对应计时器被移除后，下一次检测即恢复目标倍率。若仍有其他本人的活动操作倒计时，则继续保持 `1x`。
- **局内判定**：`SceneManager.currentType.Value` —— `Battle`(3) 战斗房间 或 `SinglePlayer`(6) 吉星小镇单人模式；场景信息不可用时回退到战斗房间/玩家数据判定。
- **HUD**：基于 FairyGUI（与 `BattleBuffDisplay` 等 Mod 相同的绘制方式），`touchable=false` 不拦截操作。

## 已知限制

- native 模式下动画加速来自全局系统时钟 hook。**已经开始播放的动画**在加速状态下启动时已按当时的 deltaTime 推进，中途改倍率无法让它倒退减速（Unity / FairyGUI 引擎限制）。本项目通过 `animSyncEnabled` 减少"新动画仍以高倍率启动"，但对播放中的那一段无效。
- `timerDetect` 与 `animSyncEnabled` 依赖游戏 / SpeedHackMod 的内部字段名，游戏或插件大版本更新后可能需要重新适配；失败会安全降级，不影响核心加速功能。
- 卸载 Mod 时会自动恢复倍率到设定值，不会卡在高倍率。

## 许可证

本项目采用 [MIT License](LICENSE) 开源。

本项目依赖 [CesiumLoader](https://github.com/higashitaniyume/CesiumLoader)，其版权归各自作者所有。本项目不包含任何游戏资源、游戏程序集或加载器本体。
