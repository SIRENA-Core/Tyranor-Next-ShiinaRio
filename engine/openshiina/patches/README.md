# OpenShiina 上游补丁维护说明

本目录维护必须施加于锁定上游提交（见 [`../source-lock.json`](../source-lock.json)）的最小化补丁。

## 基本原则

1. **宿主优先**：所有 Tyranor-Next 集成逻辑（Intent 契约、路径校验、`settings.json` 注入、进程隔离）全部放在 [`../host-apk/`](../host-apk/) 中，优先不改动上游代码。
2. **仅用补丁修改上游**：确需修改上游公开行为或修复无法在宿主层绕过的兼容性问题时，必须通过本目录的 `git format-patch` 补丁完成，禁止将整份上游源码拷入本仓库。
3. **默认空序列**：当前锁定的 `v0.8.7`（`14a24de703fb77c743ea6e0328f8e69fa26f3127`）已公开所需 API，`series` 为空；`apply-patches.py`、`export-patches.py` 与 `verify-upstream.py` 仍保持完整可用，以备后续上游同步出现破坏性变更时无缝启用。

## 常用命令

```bash
# 1. 校验外部上游克隆是否干净且命中锁定提交
python tools/openshiina/verify-upstream.py --source /path/to/OpenShiina

# 2. 应用补丁队列（空队列时会确保退回锁定提交并保持工作区干净）
python tools/openshiina/apply-patches.py --source /path/to/OpenShiina

# 3. 验证已打补丁的外部工作区与 source-lock.json / series 一致
python tools/openshiina/verify-upstream.py --source /path/to/OpenShiina --patched

# 4. 在外部上游工作区新增提交后，导出补丁并同步更新 series 与 source-lock.json
python tools/openshiina/export-patches.py --source /path/to/OpenShiina
```
