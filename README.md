# Tyranor-Next-ShiinaRio

为 [`Tyranor-Next`](https://github.com/Weiss-UltimateSavior/Tyranor-Next) 提供 **ShiinaRio**（[`kagaminehaku/OpenShiina`](https://github.com/kagaminehaku/OpenShiina)）Android 外置引擎宿主 APK 与上游同步工具链，仅聚焦 `Tyranor-Next` 显式启动集成、目录导出、补丁管理与可复现 APK 构建。

## 仓库结构

```text
Tyranor-Next-ShiinaRio/
├── engine/openshiina/
│   ├── source-lock.json          # 锁定 OpenShiina 上游提交、宿主清单契约与目录路径
│   ├── games.json                # 从上游 Formats.Json 导出的轻量化方案索引（165 个具名方案）
│   ├── patches/                  # 上游补丁队列（series 与 .patch 文件；当前为空队列）
│   └── host-apk/                 # .NET 10 Android 外置宿主工程与 xUnit 契约单元测试
├── tools/openshiina/             # 校验、补丁、目录生成、上游同步、Manifest 检查与 APK 构建脚本
├── .github/workflows/            # 工具自检、按需上游同步与发版自动同步构建
├── LICENSE / NOTICE.md           # GPL-3.0 开源许可与第三方组件声明
└── AGENTS.md                     # AI / 协作者开发与验证规范
```

## 上游同步与发版机制

- **每次发版自动同步上游**：推送 `v*` 标签或通过 `Build OpenShiina Host APK` 工作流传入 `release_tag` 触发发版时，会自动调用 [`tools/openshiina/sync-upstream.py`](tools/openshiina/sync-upstream.py) 拉取 `kagaminehaku/OpenShiina` 最新提交（或指定 `target_commit`）、重放补丁队列、刷新 `engine/openshiina/games.json` 并更新 [`engine/openshiina/source-lock.json`](engine/openshiina/source-lock.json) 的提交与版本号，在通过全量测试与 APK 签名/清单校验后回写 `main` 并发布 Release。
- 当前锁定基准：`kagaminehaku/OpenShiina` 提交 [`14a24de703fb`](https://github.com/kagaminehaku/OpenShiina/tree/14a24de703fb77c743ea6e0328f8e69fa26f3127)（标签 `v0.8.7`，协议 `GPL-3.0-or-later`）。
- 本仓库**不拷贝上游源码**。宿主层直接通过公开 API 对接 `OpenShiina.App` / `OpenShiina.Core` / `OpenShiina.Gpu`；若后续确需微调上游行为，必须通过 `tools/openshiina/export-patches.py` 导出为 [`engine/openshiina/patches/`](engine/openshiina/patches/README.md) 补丁。

## 与 Tyranor-Next 的启动契约

宿主不注册桌面启动器入口（无 `LAUNCHER` / `MAIN`），仅暴露供 `Tyranor-Next` 显式拉起的组件并在独立的 `:game` 进程中运行游戏：

| 字段 | 约定值 / 说明 |
| :--- | :--- |
| 包名 / `applicationId` | `com.tyranor.next.shiinario` |
| 启动 Activity（`exported=true`） | `com.tyranor.next.shiinario.ShiinaRioLaunchActivity`（`singleTask`） |
| 运行 Activity（`exported=false`） | `com.tyranor.next.shiinario.TyranorShiinaRioActivity`（`process=":game"`） |
| Intent Action | `com.tyranor.next.action.LAUNCH_SHIINARIO` |
| `com.tyranor.next.extra.PROTOCOL_VERSION` | `int`，固定为 `1` |
| `com.tyranor.next.extra.ENGINE` | `string`，固定为 `"shiinario"` |
| `com.tyranor.next.extra.GAME_DIR` | `string`，游戏根目录绝对路径（禁止 `..` 与相对路径） |
| `com.tyranor.next.extra.LAUNCH_TARGET` | `string`（可选），游戏目录识别出的目标 `.exe`/`.war` 或方案名 |
| `com.tyranor.next.extra.GAME_KEY` | `string`（可选），`SHA-256("<scheme>\n<canonicalDir>")` 64 位小写 Hex |
| `com.tyranor.next.extra.SETTINGS_JSON` | `string`（可选），UTF-8 JSON 运行配置 |
| `com.tyranor.next.extra.LOCALE` | `string`（可选），主程序语言（如 `zh-CN` / `en` / `ja`） |

### 下发设置项（`SETTINGS_JSON`）

宿主按每款游戏独立的 `OPENSHIINA_DATA` 路径（`<ExternalFilesDir>/games/<gameKey>/openshiina-data/settings.json`）原子落盘配置：

| JSON 键 | 类型与可选值 | 默认值 | 说明 |
| :--- | :--- | :--- | :--- |
| `renderer` | `"Gpu"` / `"Cpu"` | `"Gpu"` | 渲染后端（Vulkan 加速或纯 CPU） |
| `scaling` | `"Sharp"` / `"WholeNumbers"` / `"Smooth"` / `"Nearest"` / `"Fsr"` / `"Anime4K"` | `"Sharp"` | 画面缩放滤镜 |
| `fsrSharpness` | 整数 `0..20` | `8` | FSR 锐化强度（超出范围自动钳制） |
| `framePacing` | `"Game"` / `"Display"` | `"Game"` | 帧步调同步策略 |
| `fileCache` | `"Auto"` / `"Potato"` / `"Low"` / `"Medium"` / `"High"` / `"Max"` | `"Auto"` | 封包文件缓存上限 |
| `translateX86` | `boolean` | `true` | x86 自定义指令转译 |
| `pauseInBackground` | `boolean` | `true` | 切至后台时自动暂停 |
| `gameController` | `boolean` | `true` | 启用手柄与触控输入 |
| `showFrameRate` | `boolean` | `false` | 叠加显示帧率 |
| `theme` | `"Shiina"` / `"Parchment"` / `"Graphite"` / `"White"` / `"System"` | `"Shiina"` | 播放器主题 |
| `language` | `""` / `"zh-CN"` / `"ja"` / `"en"` | `""` | 运行界面语言（留空随主程序） |

## 游戏识别清单

- [`tools/openshiina/export-catalog.py`](tools/openshiina/export-catalog.py) 从上游 `Formats.Json` 提取 **165 个具名作品方案**与 **173 个文件入口映射**到 [`engine/openshiina/games.json`](engine/openshiina/games.json)，并主动排除 `ShiinaRio v2.34 and older`、`ShiinaRio v2.36-2.38` 两个通用兜底方案，避免静态扫描误匹配。
- 构建过程会将 `games.json` 打包至 APK `assets/tyn/games.json`，并检验字节完全一致，供 `Tyranor-Next` 侧使用 `check-game-catalog.py` 对齐。

## 本地构建与验证

前置依赖：**.NET 10 SDK（含 `android` 工作负载）**、**JDK 17**、**Android SDK（含 API 36 平台与 build-tools）**、**Python 3.10+**。

```bash
# 1. 在仓库外部克隆锁定版本的 OpenShiina
git -c core.autocrlf=false clone https://github.com/kagaminehaku/OpenShiina.git ../openshiina-src
git -C ../openshiina-src checkout --detach 14a24de703fb77c743ea6e0328f8e69fa26f3127

# 2. 运行全部单元测试、Manifest 静态校验与目录一致性检查
python tools/openshiina/check-host-manifest.py
python tools/openshiina/test-host-manifest.py
python tools/openshiina/test-upstream-integrity.py
python tools/openshiina/test-export-catalog.py
python tools/openshiina/test-sync-upstream.py
dotnet test engine/openshiina/host-apk/Tyranor.OpenShiina.Host.Tests/Tyranor.OpenShiina.Host.Tests.csproj -c Release

# 3. 构建并校验宿主 APK（全部中间文件均输出到 ../openshiina-build，不污染任一仓库）
python tools/openshiina/build-apk.py --dev --source ../openshiina-src --build-root ../openshiina-build
```

正式 Release 构建改用 `--keystore`（或 `ANDROID_KEYSTORE_*` 环境变量）签名。

## 开源许可

本仓库与上游 `OpenShiina` 均遵循 **GPL-3.0** 协议（见 [LICENSE](LICENSE) 与 [NOTICE.md](NOTICE.md)）。分发构建产物时必须同步提供对应版本的上游 commit、本仓库源码及构建说明。
