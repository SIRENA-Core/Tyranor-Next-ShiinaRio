# 更新日志（Changelog）

本文件记录 `Tyranor-Next-ShiinaRio` 的主要变更。

## Unreleased

### Added
- 初始化 `Tyranor-Next-ShiinaRio` 宿主仓库，采用 `source-lock.json` + `patches/` 机制锁定并同步 `kagaminehaku/OpenShiina`（`14a24de703fb77c743ea6e0328f8e69fa26f3127` / `v0.8.7`）。
- 新增 `.NET 10` (`net10.0-android`) 外置宿主工程 `Tyranor.OpenShiina.Host`，仅暴露 `Tyranor-Next` 显式启动入口（无桌面启动图标）、`:game` 独立进程运行、严格 Intent 参数校验以及 `settings.json` / 游戏方案注入。
- 新增 `tools/openshiina/` 工具链（上游校验、补丁应用与导出、游戏目录清单生成与字节对齐校验、Manifest 启动禁令检查、自动同步与 APK 构建脚本）。
- 新增 GitHub Actions 工具自检、上游自动同步 PR 与 Release APK 构建工作流。
- 新增 `Tyranor.OpenShiina.Host.Tests` 与 Python 单元测试覆盖契约、`gameKey` 派生、设置映射、目录生成与安全校验。
