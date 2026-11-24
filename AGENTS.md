# AGENTS.md

本文件面向协作者与 AI Agent，约定 `Tyranor-Next-ShiinaRio` 仓库的架构边界、编码规范与必过校验。所有对话回复、代码注释与文档默认使用简体中文（技术标识与 API 名保持英文原名）。

## 1. 仓库定位与边界

- **唯一职责**：将上游 [`kagaminehaku/OpenShiina`](https://github.com/kagaminehaku/OpenShiina) 包装为供 `Tyranor-Next` 显式调用的 Android 外置宿主 APK，并维护上游同步、游戏目录导出与构建工具链。
- **不跨仓越界**：`Tyranor-Next` 主工程代码位于主仓库，不在本仓库复制主 App 源码；本仓库不维护桌面端或主机端（Switch 等）发行目标。
- **严禁拷入上游源码**：不得将 `OpenShiina` 源码复制进本仓库。上游版本完全通过 [`engine/openshiina/source-lock.json`](engine/openshiina/source-lock.json) 锁定。

## 2. 不可违背的技术约束

1. **宿主优先，补丁极简**
   - 所有 Tyranor-Next 协议解析、路径校验、`settings.json` 生成与 `:game` 进程管理全部实现在 [`engine/openshiina/host-apk/`](engine/openshiina/host-apk/)。
   - 仅当上游公开 API 无法实现需求或存在致命缺陷时，才在外部上游工作区提交修改并通过 [`tools/openshiina/export-patches.py`](tools/openshiina/export-patches.py) 导出到 [`engine/openshiina/patches/`](engine/openshiina/patches/)。
2. **禁止桌面图标与非授权入口**
   - `AndroidManifest.xml` 与编译后 APK 不得包含 `android.intent.category.LAUNCHER`、`LEANBACK_LAUNCHER` 或 `android.intent.action.MAIN`。
   - 唯一外部入口为 `ShiinaRioLaunchActivity`（`exported="true"`，`launchMode="singleTask"`），真实游戏运行窗口 `TyranorShiinaRioActivity` 必须保持 `exported="false"` 与 `process=":game"`。
3. **不信任输入边界与沙盒**
   - `ShiinaRioRequest.TryParse` 必须校验协议版本号、引擎标识、绝对路径且拒绝任何 `.`/`..`/控制字符穿越。
   - `ShiinaRioSettings.Parse` 必须限制 JSON 体积、校验枚举值并钳制数值区间（如 `fsrSharpness` `[0, 20]`）。
   - `ShiinaRioRequest.ComputeGameKey` 公式（`SHA-256("<target>\n<canonicalDir>")`）不得擅自变更，否则将破坏各游戏 `OPENSHIINA_DATA` 隔离目录兼容性。
4. **目录同步与零污染构建**
   - [`engine/openshiina/games.json`](engine/openshiina/games.json) 必须与锁定上游 `Formats.Json` 导出结果字节等价，并过滤通用兜底方案名。
   - 构建中间目录（`ArtifactsPath`、`BaseOutputPath`、`dev-debug.keystore` 等）一律重定向到仓库与上游外部目录，确保双方 `git status` 干净。
5. **编码规范**
   - 全仓库文本使用 UTF-8（无 BOM）与 `LF` 行尾（`*.patch` 在 `.gitattributes` 中设为 `-text`）。

## 3. 标准作业与验证命令

改动宿主清单、契约逻辑、工具脚本或升级上游后，在提交前必须按需跑通以下检查并确认全部退出码为 `0`：

```bash
# Python 工具链与 Manifest 静态校验
python tools/openshiina/check-host-manifest.py
python tools/openshiina/test-host-manifest.py
python tools/openshiina/test-upstream-integrity.py
python tools/openshiina/test-export-catalog.py
python tools/openshiina/test-sync-upstream.py

# C# 宿主契约单元测试
dotnet test engine/openshiina/host-apk/Tyranor.OpenShiina.Host.Tests/Tyranor.OpenShiina.Host.Tests.csproj -c Release

# 上游工作区验证与目录对齐（需外部上游克隆）
python tools/openshiina/verify-upstream.py --source <OpenShiinaDir>
python tools/openshiina/apply-patches.py --source <OpenShiinaDir>
python tools/openshiina/verify-upstream.py --source <OpenShiinaDir> --patched
python tools/openshiina/export-catalog.py --source <OpenShiinaDir> --check

# 全链路调试构建 + aapt2/apksigner/assets 校验
python tools/openshiina/build-apk.py --dev --source <OpenShiinaDir> --build-root <OutDir>
```

同步上游提交时使用 [`tools/openshiina/sync-upstream.py`](tools/openshiina/sync-upstream.py)；若改动影响包名、Action、Extra 键或设置项字段，须同步更新 [`Tyranor-Next`](https://github.com/Weiss-UltimateSavior/Tyranor-Next) 中的 `ShiinaRioExternalEngineModule` 契约与资源。

## 4. 文档维护原则

- [`README.md`](README.md) 与本文件须保持**清晰、完善、精简**：聚焦职责、契约字段、设置表、约束与验证命令，不堆砌实现细节与日志流水。
- 如无用户明确指令，禁止在完成工作后自行向远端 `git push`、创建 PR 或发布 Release。
