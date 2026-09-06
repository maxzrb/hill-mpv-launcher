# Hills 外部 mpv Launcher 项目状态

## 当前状态快照

| 项目 | 状态 |
|------|------|
| 项目目录 | `<project-root>` |
| 项目类型 | Windows Hills Lite 外部播放器 launcher/wrapper |
| 当前阶段 | `v1.0.0` 已发布：流程锁定、公开文件脱敏、远端 Release 校验完成 |
| 技术栈 | C# / .NET 8 / Windows P/Invoke |
| 发布产物 | `hill-mpv-launcher-v1.0.0-win-x64.zip`；含 self-contained `mpv-launcher.exe`、脱敏 `launcher.ini` 与 `使用说明.md` |
| Git | `main` 已推送，tag `v1.0.0` 已推送；发布后文档更新待提交 |
| 项目版本 | `1.0.0`；正式 Release 已发布 |
| Release | `https://github.com/maxzrb/hill-mpv-launcher/releases/tag/v1.0.0` |
| 主要边界 | 不保存 Hills 登录 Token，不缓存 CDN 签名；仅按当前服务器匹配读取 Hills 已落盘 AccessToken 并在内存使用；评分不足或同分时原样回退；reporter 回传依赖父进程 stdout 通道 |

## 已完成能力

- 使用 `GetCommandLineW` 保存原始命令行哈希，并使用 `CommandLineToArgvW` 解析 Hills 参数。
- 日志写入 `logs/launcher-YYYYMMDD.log`，UTF-8，无 BOM。
- 记录启动时间、PID、父进程、父进程路径、当前目录、环境变量和每个参数的长度/哈希/脱敏值。
- 对 `api_key`、Token、签名、Cookie、Authorization、115 CDN 查询参数等敏感内容默认打码。
- 识别 Emby 会话 URL、115 CDN URL、辅助字幕/音频/Referer/代理 URL，并避免把辅助 URL误选为主媒体。
- 同时存在可靠 Emby URL 时替换主媒体参数；只有 CDN URL 且无上下文时原样回退，明确记录 `cannot recover` 类边界而不伪造 ItemId。
- 使用可靠 Windows 参数引用规则和 `CreateProcessW` 启动真正的 mpv，不经过 `cmd.exe`、`system()` 或 `ShellExecute` 拼接命令。
- 等待 mpv 退出并返回子进程退出码；支持 `--debug`、`--dry-run`、`--dump-args`、`--help`。
- 仅有 115 CDN URL 时读取 Hills 本地 `shared_preferences.json` 的 `rememberTracks`，按 `--force-media-title` 精确匹配 ItemId/MediaSourceId，并生成 Emby `/emby/videos/{id}/original.mkv` 会话地址。
- `rememberTracks` 未命中时扫描 Hills 响应缓存，兼容单媒体详情与 `Items` 列表结构；媒体文件名只剥离明确的视频扩展名，支持多点号文件名和多个媒体源。
- 剧集场景从 `MediaSourceId` 的数字部分取得具体视频 ID，避免 Hills 记录的季 ID导致 Emby 返回 401/500；若缓存没有后续集详情，使用 Hills 本地 AccessToken 查询 Emby 媒体列表。
- 一次调用含多个 `--{ ... --}` 媒体块时逐块取链；每个块只读取自己的 `--force-media-title`，完整替换可解析的 115 URL。
- 响应缓存匹配按文件名、媒体源名和标题评分；同分候选拒绝选择，避免同名电影/剧集误串片。
- mpv stdout 使用管道边转发边记录，保留 `HILLS_MPV_EVENT:` reporter 事件；mpv stderr 同步采集，统一记录脱敏诊断。
- reporter 的 `end-file` 只有 `reason=eof` 才透传为完成；`quit`、`stop`、`error` 等非完成退出会补发最后 `time-pos` 并抑制完成事件。
- 网络连接/超时、403/签名、解码/编码、媒体打开失败分别归类到 `stderr_diagnostics`。
- Hills 缓存无法可靠匹配时不猜测 ID；保留原始 CDN URL并记录 resolver 原因。
- 对 Emby/115 等远程媒体自动追加 `--script-opts-append=startup_format_logos-mode=none` 和 `--no-resume-playback`，分别关闭签名流后瞻探测、阻止 mpv watch-later 恢复旧 playlist 位置；本地文件不追加，已有明确对应参数时不覆盖。

## 验证记录

- `dotnet build .\HillsMpvLauncher.csproj -c Release --nologo`：通过，0 警告、0 错误。
- `dotnet publish .\HillsMpvLauncher.csproj -c Release -r win-x64 --self-contained true --nologo`：通过，生成单文件 exe。
- 模拟参数覆盖 `&`、`%`、`+`、`=`、`?`、中文、空格、字幕 URL、Referer、环境变量：通过；签名和 Token 未出现在日志中。
- 同时提供 CDN URL 与 Emby URL：通过，计划选择 Emby URL。
- 仅提供 CDN URL：通过，计划保持原 URL、`exact_primary_forwarding=true`。
- `--referrer` 中包含 Emby URL：通过，不会误替换主媒体 URL。
- 以 `C:\Windows\System32\where.exe` 模拟 mpv：`CreateProcessW` 成功启动、等待并记录子进程退出码。
- 使用真实 Hills 参数形状验证：`action=replace-115-with-emby-session`、`working_directory=<mpv-directory>`、`command_line_round_trip=true`。
- 对生成的 Emby 会话地址做无下载 Range 探测：跟随 Emby 302 后得到 HTTP 206；确认服务端会重新生成有效 115 签名。
- 修正环境变量 `*_API_KEY` 脱敏，并清理/修正旧测试日志中的明文敏感值。
- 启动前使用 `CommandLineToArgvW` 对完整 `CreateProcessW` 命令行逐项往返比较：包含特殊字符、中文、空格的测试通过。
- 用第二个媒体源模拟同一 Item 的外部调用：响应缓存命中可靠的 ItemId、MediaSourceId 和匹配分数。
- 用含多个点号的 `Skyfall.2012...DTS-HD.MA.5.1-FGT.mkv` 验证文件名规范化不会误剥离最后一段。
- 用 `where.exe` 验证 stdout 管道回传、子进程退出码和无 stderr 异常；模拟 `HILLS_MPV_EVENT:position=12` 被记录为 `HILLS-REPORTER`。
- 用本机拒绝连接地址验证 mpv stdout 诊断被捕获，并归类为 `network-timeout-or-connect,open-or-loading-failure`。
- 用模拟 reporter 验证 `quit` 会被抑制且最后 `time-pos=123.4` 会补发；验证 `reason=eof` 仍正常透传。
- 根据真实三集 Hills 日志确认旧逻辑只替换第一集：第一个季 ID会导致 Emby 401，后两集保留旧 115 链接并403；已修正为逐块处理。
- 发布版自动从 Hills 当前服务器数据库读取 AccessToken（不落盘、不入日志），并通过媒体源数字 ID生成正确的视频地址；实测正确的视频地址返回 HTTP 200。
- 三媒体块 dry-run 通过：3 个块均解析为对应 Emby 视频 ID，`configured_emby_token=True`，`command_line_round_trip=true`，日志只保留 `<redacted>`。
- 新增 Emby API 查询 fallback；发布 self-contained 单文件时包含 SQLite 依赖，Release build/publish 均通过 0 警告、0 错误。
- API 查询短缓存保存原始候选并按每个媒体块重新评分；已回归确认第 9 集不会复用第 8 集的匹配结果。
- 真实 S1E7 单集回归：发布版自动追加远程播放保护后，成功加载到 `time_pos=43` 并持续播放；未再出现 `Corrupt file detected`、`startup-format-logos.lua` 的 `tonumber` 错误或自动跳到下一集。
- 本次 Release build 与 self-contained publish 均通过 0 警告、0 错误；测试进程已按精确路径清理。
- 最新 Hills 日志确认旧行为含 `Resuming playback`，先进入 playlist 位置 1（S1E8），约 20 多秒后才加载 S1E7；加入 `--no-resume-playback` 后单集回归不再出现恢复提示，S1E7 按 `--start=102` 加载并持续运行。
- 三集 playlist 回归通过：最终命令含 `--no-resume-playback`，mpv 首个 `Playing` 直接为 S1E7，未恢复到 S1E8；测试进程已清理。

## 后续工作

1. 用户用发布目录的 `mpv-launcher.exe` 在 Hills 中重新验证 S1E7，并继续验证电影、剧集和多媒体源切换。
2. 若仍失败，提供对应时间段的脱敏 launcher 日志，重点看 `selected_source`、`remote_startup_logo_safety_added`、`remote_no_resume_safety_added` 与 `stderr_diagnostics`。
3. 如 Hills 的 reporter 使用的不是 stdout 文本通道，再根据实际脚本协议补充专用 IPC；当前不伪造未知协议。

## 2026-09-06 16:06

### 多集取链回归修正与最终验证

- 发现短缓存初版缓存了上一集的匹配分数，可能把第 9 集误选成第 8 集；已改为缓存原始 Emby 媒体候选，并按当前媒体块重新计算分数。
- 最终三媒体块 dry-run 通过：三集均生成各自的 Emby 会话地址，`command_line_round_trip=true`，日志只保留脱敏值。
- 最终 Release build 与 self-contained publish 通过 0 警告、0 错误；发布 exe 已更新，配置仍指向真实 mpv 且 `extra_args` 为空。
- 发布日志脱敏审计通过：未发现未打码的 API 令牌、Authorization、Cookie 或 115 查询签名；测试产生的 mpv 进程均已结束。

---

## 2026-09-06 15:06

### 第二阶段稳定性增强完成

- 响应缓存解析兼容单媒体详情和 `Items` 列表；新增文件名/媒体源名/标题评分、同分拒绝和多点号文件名处理，避免只支持当前 `rememberTracks`。
- mpv stdout 改为管道 tee：一份原样回传父进程供 Hills reporter 使用，一份写入脱敏日志；stderr 同步采集，网络/403/签名/解码/打开失败分类可审计。
- 已重新执行 self-contained publish；发布目录 `launcher.ini` 已恢复为真实 mpv 路径，`extra_args` 保持为空，不强制全屏。
- dry-run、响应缓存 fallback、stdout 回传、模拟 Hills reporter 事件和本机拒绝连接错误分类均通过。
- 已完整阅读上级 MPV 配置仓库的发布流程；本次只构建独立 launcher，不生成 MPV Vanta 01～04 包、VantaInstaller、GitHub Release 或版本标签，因此未执行上级仓库发布上传流程。
- 发布目录最终自检通过：`launcher.ini` 指向真实 mpv、`extra_args` 为空；测试启动的 `mpv-launcher`/mpv 进程已结束。上级仓库仍有用户既有改动，未重置或覆盖。

## 2026-09-06 15:17

### 修正手动关闭误报播放完成

- 根据真实 Hills 日志确认：`time-pos` 事件持续正常转发，但 mpv 关闭时 reporter 发出 `end-file(reason=quit,time_pos=0)`；Hills 将其误判为播放完成。
- wrapper 现在只透传 `end-file(reason=eof)`；手动关闭、停止和错误会抑制完成事件，并将最近一条 `time-pos` 再回传一次。
- 已重新 build/publish；模拟 `quit` 与 `eof` 两条路径均通过，发布配置已恢复真实 mpv 且不强制全屏。

## 2026-09-06 13:36

### 第一阶段实现与验证

- 在独立项目目录从零建立 `HillsMpvLauncher.csproj`、`Program.cs`、配置示例、README 和 `.gitignore`。
- 第一阶段不修改 Hills、不修改 mpv 本体、不访问网络；只分析 Hills 传入上下文并在可证明安全时选择 Emby URL。
- 发现并修正一次日志脱敏边界：路径含空格的 115 URL最初可能只遮盖到空格前，现改为按完整 argv 项处理；并补充了 `X-Emby-Token`、Authorization 等 Header 格式脱敏。
- 保留真实转发字符串不变，使用长度和短 SHA-256 记录完整性；日志中只保存脱敏表示。
- 工作区无 Git 仓库、无现成 `AGENTS.md`，因此无法执行 `git pull` 或提交；后续如需多人/多 agent 交接，应先初始化 Git 与项目规则文件。

## 2026-09-06 13:38

### 启动命令行往返自检与最终发布

- 在 `CreateProcessW` 前增加命令行往返验证：用 `CommandLineToArgvW` 解析 wrapper 生成的完整命令行，并与待转发的可执行路径及每个参数逐项比较；不一致时拒绝启动。
- 重新执行 Release build 和 self-contained publish，均通过 0 警告、0 错误；发布 exe 已更新。
- 特殊字符、中文、空格、115 签名字段、Emby URL、字幕/Referer 参数和 Header 脱敏测试继续通过。

## 2026-09-06 13:40

### 发布目录补齐配置示例

- 更新项目文件，使 `launcher.ini.example` 自动复制到 Release 输出和 self-contained publish 目录。
- 最终 publish 通过，发布目录包含 `mpv-launcher.exe` 与 `launcher.ini.example`；临时测试日志已移除，首次真实运行时会按需创建 `logs`。

## 2026-09-06 13:41

### 最终脱敏审计

- 对 fatal debug 异常和 malformed 配置行也统一使用脱敏处理，避免异常文本或错误配置把敏感参数带入日志。
- 重新 publish 通过；发布 exe 与配置示例保持可用。

## 2026-09-06 13:53

### Hills 使用配置已准备

- 已在发布目录创建 `launcher.ini`，将 `[mpv] path` 配置为本机 mpv 可执行文件，并保持 `extra_args` 为空，不强制全屏。
- 使用包含 Emby URL、`&` 和 API key 的模拟 Hills 参数执行 `--dry-run`；日志确认路径解析正确、`command_line_round_trip=true`，且敏感值已打码。
- 后续用户只需在 Hills 的“外部播放器 / mpv 路径”中选择发布目录的 `mpv-launcher.exe`。

## 2026-09-06 14:17

### 根据真实 Hills 调用完成取链修复

- 真实 Hills 日志确认外部调用只传入 `115cdn.net` 直链，`context_item_id=false`、`context_media_source_id=false`、`context_server=false`、`context_token=false`；因此旧版 wrapper 只能转发已失效/错误签名的 CDN URL。
- 从 Hills 已存在的 `shared_preferences.json` 中读取与 `--force-media-title` 匹配的 `rememberTracks`，得到可靠的 ItemId 与 MediaSourceId；不读取登录 Token。
- 生成脱敏的 Emby `/emby/videos/<video-id>/original.mkv?...` 会话地址，验证 Emby 302 后返回 HTTP 206；已重新编译并发布 self-contained exe。
- 已将 mpv 工作目录固定为用户配置的 mpv 目录，避免 Hills 从系统目录启动时改变便携配置行为。
- 修正 `*_API_KEY` 环境变量脱敏；旧诊断日志中已出现的敏感值已替换为打码形式。

## 2026-09-06 14:22

### 默认窗口行为调整

- 移除发布配置中的 `extra_args=--fs`，避免外部 mpv 启动时强制占满屏幕；取链、参数替换和工作目录配置保持不变。
- 使用真实 Hills 参数形状再次执行 `--dry-run`，确认 `extra_argument_count=0`，生成命令行不再包含 `--fs`。

## 2026-09-06 16:48

### 修复远程签名流被后瞻脚本误判并自动跳集

- 真实 S1E7 日志确认：Emby 会话地址、鉴权和起播位置均正确；S1E7 成功加载后，startup-format-logos 的 ffmpeg 后瞻与主流并发触发 `mkv` 损坏误判，mpv 发出 `end-file(reason=error)`，Hills 才自动切到 S1E8。
- 通过单集隔离验证：追加 `--script-opts-append=startup_format_logos-mode=none` 后，S1E7 成功加载并持续播放，未再出现 `Corrupt file detected` 或 Lua `tonumber` 错误。
- wrapper 已对 Emby/115 等远程媒体自动加入该保护项；本地媒体不受影响，用户已显式设置 `startup_format_logos-mode` 时不覆盖。
- Release build 和 self-contained publish 均通过 0 警告、0 错误；dry-run 与真实单集回归通过，敏感值保持脱敏，测试进程已清理。
- Git 状态：test2 不是 Git 仓库；上级 mpv2 的用户既有改动未触碰。

## 2026-09-06 17:07

### 修复远程多集播放被 mpv watch-later 恢复干扰

- 最新 Hills 日志出现 `Resuming playback`，mpv 先恢复到 playlist 位置 1（S1E8），S1E7 约 20 多秒后才 `file-loaded`；这会让 Hills 看起来卡在 S1E7 加载或起播集数错乱。
- wrapper 对远程媒体新增自动参数 `--no-resume-playback`；Hills 传入的 `--start` 仍保留，用户显式设置 `--resume-playback`/`--no-resume-playback` 时不覆盖。
- 发布版已重新 build/publish；dry-run 确认同时加入后瞻保护和 no-resume 保护，真实单集回归无 `Resuming playback`，S1E7 按指定起始时间加载并持续运行。

## 2026-09-06 17:11

### 多集 playlist 起播顺序回归通过

- 使用三集 S1E7/S1E8/S1E9 的真实解析结果回归，最终 mpv 命令同时带有后瞻保护和 `--no-resume-playback`。
- 日志确认首个 `Playing` 为 S1E7，不再先恢复到 S1E8；测试进程已清理。

## 2026-09-06 17:31

### 建立受保护发布流程并准备 v1.0.0

- 已建立项目级 `AGENTS.md` 与《发布流程.md》；流程文件已设置为 Windows 只读，未经用户在当前对话明确授权不得修改。
- 已固化版本规则：首个版本为 `1.0.0`，Git tag 严格为 `v1.0.0`，Release 标题同样为 `v1.0.0`，不混入项目名。
- 已固化 Release Notes 规则：每个要点单独一行，只允许 `[修改]`、`[新增]`、`[移除]`；agent 需要新标签时必须先询问用户。
- 已完成公开文件脱敏审计：移除真实服务器地址、本机路径、测试媒体 ID 和用户环境痕迹；真实 `launcher.ini` 仍保持忽略。
- 当前阻塞：尚未完成最终 build/package、Git 初始化、远端仓库创建和 GitHub Release 上传。
- Git 状态：项目此前不是 Git 仓库；待完成脱敏复核后初始化 `main` 分支。

## 2026-09-06 17:48

### v1.0.0 GitHub Release 发布完成

- 已按《发布流程.md》完成最终 `dotnet build` 和 self-contained `dotnet publish`，build 为 0 警告、0 错误，发布版 `--help` 退出码为 0。
- 已完成公开文件和 ZIP 二次脱敏校验；ZIP 清单严格为 `mpv-launcher.exe`、`launcher.ini`、`使用说明.md`，没有日志、本机配置或额外文件。
- 已创建并推送公开仓库 `maxzrb/hill-mpv-launcher`，初始提交为 `d14f5b7`；`main` 已同步到远端。
- 已创建并推送严格的 annotated tag `v1.0.0`，没有混入项目名或其他文字。
- 已创建正式 Release：`https://github.com/maxzrb/hill-mpv-launcher/releases/tag/v1.0.0`；非 draft、非 prerelease，唯一 asset 为 `hill-mpv-launcher-v1.0.0-win-x64.zip`。
- 远端 asset 大小为 `31676728` bytes，SHA-256 为 `b1800132980610f9389d01dec3d65e120dce723ad200aed2e2e1a1cb463df85e`，与本地一致；Release Notes 每行均使用允许的三种标签。
- 待办：将本次发布记录提交并推送，之后确认工作区清洁。
