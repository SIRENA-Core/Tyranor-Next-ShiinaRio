# 第三方声明与开源许可（Third-Party Notices）

`Tyranor-Next-ShiinaRio` 是专为 `Tyranor-Next` 提供 ShiinaRio 外置宿主 APK 的包装与适配仓库，采用与上游一致的 **GPL-3.0** 协议发布（见根目录 [LICENSE](LICENSE)）。

## 1. 上游核心引擎

- 项目：`kagaminehaku/OpenShiina`
- 仓库：https://github.com/kagaminehaku/OpenShiina
- 协议：`GPL-3.0-or-later`
- 锁定方式：本仓库不直接拷贝上游源码，而是通过 [engine/openshiina/source-lock.json](engine/openshiina/source-lock.json) 锁定上游 commit，并通过 [engine/openshiina/patches/](engine/openshiina/patches/README.md) 维护最小化宿主适配补丁。

## 2. 第三方组件与许可证分发

编译生成的 Android APK 包含上游 `OpenShiina` 及其依赖的第三方开源组件（包括 Avalonia UI、SkiaSharp、Vortice.Vulkan、SDL3 / OpenAL / 图像缩放着色器等）。完整的上游第三方声明请见对应锁定版本的上游 `THIRD-PARTY-NOTICES.md`；CI 在生成 Release 产物时会自动将本仓库 `LICENSE`、`NOTICE.md` 与上游 `LICENSE`、`THIRD-PARTY-NOTICES.md` 一并打包随行发布。

## 3. 对应源码获取（GPL-3.0 第 6 条）

任何分发基于本仓库构建的二进制 APK 时，必须同步提供或书面指明获取对应版本源码的方式：
1. 本仓库中锁定的 [engine/openshiina/source-lock.json](engine/openshiina/source-lock.json) 所记录的上游 commit；
2. [engine/openshiina/patches/](engine/openshiina/patches/) 中的全部补丁与宿主工程代码；
3. 按根目录 [README.md](README.md) 中的构建命令即可完整复现对应产物。
