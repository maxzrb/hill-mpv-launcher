# AGENTS.md - Hills 外部 mpv Launcher

本文件是项目根目录的项目级协作规则。

## 发布流程保护

- 所有发布、打包、创建远端仓库、创建 Git tag 和 GitHub Release 的操作，必须先完整阅读并严格执行项目根目录的《发布流程.md》。
- 《发布流程.md》是本项目唯一的发布规范。未经用户在当前对话明确授权，agent 不得修改、重命名、删除、拆分、绕过或降低该文件中的任何要求。
- 如果用户的新要求与《发布流程.md》冲突，agent 必须先停在冲突处并征求用户意见，不得自行调整流程。
- 每次发布都必须把检查结果、构建命令、产物校验、Git 状态和远端 Release 状态写入 `docs/codex/STATUS.md`，并在 `version/工作进度.md` 追加记录。

## 版本与 Release Notes

- 项目版本和 release tag 使用 `vX.Y.Z`，例如首个版本必须是 `v1.0.0`；tag 不得混入项目名或其他文字。
- Release Notes 的每个要点必须单独占一行，并且只能使用 `[修改]`、`[新增]`、`[移除]` 三种标签。
- agent 不得自行增加新的 Release Notes 标签。若认为需要新增标签，必须先询问用户并等待明确意见。

## 脱敏与文件处理

- 所有文件读写使用 UTF-8；PowerShell 读取中文文件时先使用 `chcp 65001`，并用 `Get-Content -Encoding UTF8`。
- 代码注释使用中文。修改文件使用 `apply_patch`；不使用 shell 重定向或脚本拼接覆盖项目文件。
- 公开仓库和 Release ZIP 不得包含 Token、Cookie、Authorization、CDN 签名、真实本机用户目录、调试日志或本机专用配置。
- `launcher.ini` 只允许以脱敏模板形式进入 Release；本机真实 `launcher.ini` 必须保持忽略，不得提交。

## HandShake 记录

- `docs/codex/STATUS.md` 是 AI 侧唯一状态源；新日志只能追加到文件末尾，当前快照要保持在顶部。
- `version/工作进度.md` 是面向用户的进度记录，每次实质性工作都要追加带日期和时间的记录。
- 项目版本或 Release 变化时，更新 `version/版本迭代记录.md`，保留历史记录。
