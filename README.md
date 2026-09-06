# Hills 外部 mpv Launcher

这是一个 Windows 下的 Hills Lite 外部播放器 wrapper。第一阶段的目标是保持 Hills 参数完整，同时避免通过 `cmd.exe`、`system()` 或拼接 shell 字符串启动 mpv。

## 当前能力

- 通过 `GetCommandLineW` 保存原始命令行，并使用 `CommandLineToArgvW` 解析参数。
- 记录启动时间、PID、父进程、工作目录、环境变量、参数和 URL 诊断信息。
- 日志默认为 UTF-8，写入 `logs/launcher-YYYYMMDD.log`。
- 普通日志会遮盖 `api_key`、`AccessToken`、`token`、`sign`、Cookie 等敏感值；115 CDN URL 的查询参数全部遮盖，只保留 URL 形状。
- 如果 Hills 同时传入 Emby 会话 URL 和 115 CDN URL，优先把主媒体参数替换为 Emby URL。
- 如果 Hills 只传入 115 CDN URL，则读取 Hills 本地 `shared_preferences.json` 中与当前媒体标题匹配的 `ItemId` 和 `MediaSourceId`，生成 Emby `/emby/videos/{id}/original.mkv` 会话地址，让 Emby 重新生成新鲜的 115 签名；剧集场景优先使用 `MediaSourceId` 的视频 ID，避免把季 ID 当成视频 ID。
- `rememberTracks` 没有命中时，会继续扫描 Hills 响应缓存中的单媒体详情和 `Items` 列表，按媒体文件名、媒体源名和标题评分；同分时拒绝猜测，避免同名资源串片。
- Hills 一次传入多个 `--{ ... --}` 媒体块时，每个媒体块按自己的标题和文件名分别解析、替换，不会只处理第一集；缓存缺少后续集详情时，会使用 Hills 本地已登录会话向 Emby 查询匹配的媒体源。
- 如果配置中的 `[emby] token` 留空，会从 Hills 本地数据库读取当前服务器的 AccessToken，仅在内存中用于生成会话地址，不写回配置、不写入日志。
- 对 Emby/115 等远程媒体会自动追加 `--script-opts-append=startup_format_logos-mode=none` 和 `--no-resume-playback`：前者关闭 startup-format-logos 的 ffmpeg 后瞻探测，后者避免 mpv 的 watch-later 恢复到旧的 playlist 集数；本地文件不追加，用户已明确设置对应选项时不覆盖。
- 如果只有 CDN URL 且没有可恢复的 Emby 上下文，不猜测 ItemId，原样转发或按配置拒绝。
- 使用 `CreateProcessW` 直接启动真正的 mpv，并等待 mpv 结束后返回相同退出码。
- mpv stdout 会边转发给 Hills、边写入脱敏日志；`HILLS_MPV_EVENT:` 会标记为 `HILLS-REPORTER`，普通 mpv 输出标记为 `MPV-STDOUT`，stderr 标记为 `MPV-STDERR`。
- 只有 reporter 的 `end-file` 且 `reason=eof` 才会透传为“播放完成”；手动关闭的 `quit`、停止和错误会抑制完成事件，并补发最后一条 `time-pos`，避免误标记已播放。
- 诊断会把网络连接/超时、403/签名、解码/编码和打开媒体失败归类到 `stderr_diagnostics`，不记录完整敏感 URL。
- 启动参数中的 `&`、`%`、`+`、`=`、`?`、中文、空格和反斜杠不会经过 shell 展开。

## 使用

1. 解压发布 ZIP，在 `launcher.ini` 中把 `[mpv] path` 和 `working_directory` 改成自己的 mpv 路径，把 `[emby] server` 改成自己的 Emby 地址；默认不强制全屏。
2. 在 Hills 中将外部播放器路径设置为发布出来的 `mpv-launcher.exe`，不要设置为真正的 `mpv.exe`。
3. 默认会自动读取 Hills 当前服务器会话；若服务器不允许自动读取，可在本地 `[emby] token` 手动填写，绝不要提交到仓库。
4. 播放后如需诊断，查看发布目录 `logs\launcher-YYYYMMDD.log`；重点关注 `selected_source`、`HILLS-REPORTER`、`MPV-STDOUT`、`MPV-STDERR` 和 `stderr_diagnostics`。

诊断模式：

```text
mpv-launcher.exe --dump-args <Hills传入的参数>
mpv-launcher.exe --dry-run <Hills传入的参数>
mpv-launcher.exe --debug <Hills传入的参数>
```

这些开关都会写入日志；`--dump-args` 不启动 mpv，`--dry-run` 只记录最终准备启动的命令。敏感字段不会因为 debug 开关而写入明文。

## 构建

开发构建：

```powershell
dotnet build .\HillsMpvLauncher.csproj -c Release
```

发布为 self-contained 单文件：

```powershell
dotnet publish .\HillsMpvLauncher.csproj -c Release -r win-x64 --self-contained true
```

发布输出位于 `bin\Release\net8.0-windows\win-x64\publish\`。

## 当前边界

当前不主动请求 Emby `PlaybackInfo`，不长期缓存 CDN 签名，不读取剪贴板；会按服务器匹配读取 Hills 数据库中已经落盘的 AccessToken，并仅在内存中用于 Emby 查询和会话地址。评分不足或同分时保留原始 URL。进度回传依赖 Hills 传入的 reporter 脚本及其 stdout 通道；wrapper 只过滤非完成退出的 `end-file`，不伪造未知协议。
