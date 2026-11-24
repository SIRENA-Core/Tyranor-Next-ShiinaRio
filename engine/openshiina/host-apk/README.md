# Tyranor.OpenShiina.Host 工程说明

本目录包含外置 ShiinaRio 宿主 Android 工程与纯 C# 契约单元测试：

- `Tyranor.OpenShiina.Host/Shared/`：跨端共享纯逻辑（Intent 契约常量、目录解析、设置解析与钳制、`OPENSHIINA_DATA/settings.json` 生成、不可信请求校验），无 Android 依赖，被测试工程直接复用。
- `Tyranor.OpenShiina.Host/`：Android 宿主应用，通过 `ShiinaRioLaunchActivity` 校验参数并检查 `MANAGE_EXTERNAL_STORAGE` 权限，生成每款游戏独立的 `<ExternalFilesDir>/games/<gameKey>/openshiina-data/settings.json`，随后在隔离进程 `:game` 中的 `TyranorShiinaRioActivity` 内加载 `GameView(library: false)`。
- `Tyranor.OpenShiina.Host.Tests/`：xUnit 测试工程，验证 `games.json` 解析、`gameKey` 黄金 SHA-256 向量、目录穿越拦截与设置序列化。

## 本地调试命令

```bash
# 运行纯逻辑测试（无需连接上游源码）
dotnet test engine/openshiina/host-apk/Tyranor.OpenShiina.Host.Tests/Tyranor.OpenShiina.Host.Tests.csproj -c Release

# 全量构建并验证调试 APK（所有产物均位于 --build-root 外部目录）
python tools/openshiina/build-apk.py --dev --source <OpenShiinaDir> --build-root <BuildDir>
```
