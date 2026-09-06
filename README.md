# Hill MPV Launcher

Hills Lite 外部 mpv 启动器，主要用于处理 Emby + 115 STRM 场景下的外部播放取链问题。

Hills Lite 的内置播放器和外部播放器在部分场景下会使用不同的播放地址。内置播放通常经过 Emby 的 `/emby/videos/.../original.mkv` 会话地址，而外部播放器可能直接收到已经解析出的 115 CDN 临时签名地址。

当签名失效、过期或在传递过程中发生变化时，115 CDN 会直接返回：

```text
403 invalid signature
```

此时调整 mpv 的缓存、网络超时、User-Agent 或 Referer 都无法解决问题。

Hill MPV Launcher 放在 Hills Lite 和 mpv 之间，尝试根据 Hills 本地保存的媒体信息和当前 Emby 会话恢复正确的播放入口，再启动用户指定的 mpv。

```text
Hills Lite
    ↓
mpv-launcher.exe
    ↓
Emby 播放入口
    ↓
115 / 其他媒体源
    ↓
mpv.exe
```

项目目前主要针对 Windows x64、Hills Lite、Emby 和外部 mpv 使用场景。

## 功能

* 作为 Hills Lite 的外部播放器入口启动独立 `mpv.exe`
* 保留 Hills 原有的播放参数
* 从 Hills 本地数据中读取当前 Emby 会话
* 根据媒体标题、文件名、`ItemId`、`MediaSourceId` 等信息恢复对应媒体
* 对 115 STRM 等临时签名资源重新生成 Emby 播放入口
* 支持电视剧和一次传入多个媒体项目的播放列表
* 保留 Hills 的播放进度回传
* 区分正常播放结束、手动关闭、停止和播放错误
* 普通日志自动遮盖 Token、Cookie、CDN 签名等敏感字段
* 直接启动 mpv，不经过 `cmd.exe`

mpv 本身的配置不会被修改。`mpv.conf`、`input.conf`、脚本、Shader 和其他本地配置仍由原有 mpv 环境负责。

## 下载

在 [Releases](https://github.com/maxzrb/hill-mpv-launcher/releases/latest) 下载最新的 Windows x64 压缩包并解压到固定目录。

发布包为 self-contained 版本，无需另外安装 .NET Runtime。

## 配置

第一次运行时双击：

```text
mpv-launcher.exe
```

程序会进入配置向导，需要填写：

```text
mpv 路径
mpv 工作目录
Emby 服务器地址
Emby Token（可选）
```

例如：

```text
mpv:
D:\mpv\mpv.exe

工作目录:
D:\mpv

Emby:
https://emby.example.com
```

Token 通常可以留空。Launcher 会尝试读取 Hills 当前服务器已经保存的登录会话。

需要重新运行配置向导时：

```text
mpv-launcher.exe --setup
```

配置保存在程序目录下的：

```text
launcher.ini
```

## Hills Lite 设置

在 Hills Lite 的外部播放器设置中，将 mpv 路径指向：

```text
mpv-launcher.exe
```

不要填写真正的 `mpv.exe`。

实际调用关系为：

```text
Hills Lite
    ↓
mpv-launcher.exe
    ↓
mpv.exe
```

配置完成后，Hills 中的外部播放操作无需改变。

## 115 STRM 处理

当 Hills 传入正常的 Emby 播放地址时，Launcher 会优先保留该地址。

如果 Hills 只传入 115 CDN 地址，Launcher 会尝试从 Hills 本地数据恢复对应的 Emby 媒体信息。

目前使用的信息包括：

* 当前 Emby 服务器
* AccessToken
* `ItemId`
* `MediaSourceId`
* 媒体标题
* 文件名
* 媒体源名称
* Hills 本地响应缓存

恢复成功后，会生成类似下面的 Emby 地址：

```text
https://emby.example.com/emby/videos/{id}/original.mkv
```

后续跳转和临时 CDN 地址继续由 Emby 处理。

电视剧场景会优先使用对应媒体源的视频 ID，避免季 ID、剧集 ID 和实际视频源混淆。

Hills 一次传入多个媒体块时，各媒体块分别解析，不共用第一集的结果。

如果本地缓存缺少后续集信息，Launcher 会尝试通过 Hills 当前已经登录的 Emby 会话查询对应媒体源。

## 播放进度

Hills 调用外部 mpv 时会通过 reporter 输出播放状态。

Launcher 会继续转发这些信息，同时处理部分容易导致误判的退出情况。

正常播放到结尾：

```text
end-file
reason=eof
```

会继续作为播放完成事件交给 Hills。

手动关闭、停止或播放错误时，不会直接把 `end-file` 当作完整播放结束，同时会尽量补发最后一次 `time-pos`。

这样可以减少手动关闭播放器后被 Hills 直接标记为已播放的情况。

## 远程媒体参数

对 Emby、115 等远程媒体，Launcher 默认会补充：

```text
--script-opts-append=startup_format_logos-mode=none
--no-resume-playback
```

前者用于关闭部分启动脚本的额外 ffmpeg 后瞻探测，避免临时签名流在正式播放前被重复访问。

后者用于避免 mpv 的 watch-later 状态恢复到旧的播放列表项目。

本地文件不会自动加入这些参数。

如果 Hills 或用户已经明确传入对应设置，Launcher 不会重复覆盖。

## 日志

日志位于：

```text
logs\launcher-YYYYMMDD.log
```

其中会记录：

* Hills 传入参数
* 当前媒体识别结果
* 选中的播放来源
* mpv stdout / stderr
* reporter 输出
* HTTP 403
* CDN 签名错误
* 网络连接错误
* mpv 打开媒体失败

常用标记：

```text
selected_source
HILLS-REPORTER
MPV-STDOUT
MPV-STDERR
stderr_diagnostics
```

Token、Cookie、`api_key`、`AccessToken`、`sign` 等字段会自动脱敏。

115 CDN URL 的查询参数默认不会完整写入日志。

提交 Issue 前仍建议检查日志中是否包含本机路径、私有服务器地址等信息。

不要上传包含明文 Token 的 `launcher.ini`。

## 诊断参数

```text
mpv-launcher.exe --help
mpv-launcher.exe --setup
mpv-launcher.exe --dump-args <参数>
mpv-launcher.exe --dry-run <参数>
mpv-launcher.exe --debug <参数>
```

`--dump-args`

记录接收到的参数，不启动 mpv。

`--dry-run`

完成媒体识别和命令生成，不启动 mpv。

`--debug`

记录更详细的处理过程。

Debug 模式下敏感字段仍然会脱敏。

## 手动配置

配置向导生成的 `launcher.ini` 可以直接编辑。

示例：

```ini
[mpv]
path=D:\mpv\mpv.exe
working_directory=D:\mpv

[emby]
server=https://emby.example.com
token=
```

一般情况下 `token` 保持为空即可。

Launcher 会优先读取 Hills 当前服务器对应的 AccessToken，并只在当前进程中使用。

更多说明见 [使用说明.md](./使用说明.md)。

## 已知限制

Launcher 需要足够的 Emby 上下文才能重新获取播放入口。

如果 Hills 只传入一个已经失效的 CDN 地址，同时 Hills 本地数据中找不到对应的媒体项目、媒体源或服务器会话，就无法可靠恢复原始 Emby 项目。

这类情况下会保留原始地址或按照配置拒绝播放，不会通过模糊匹配强行猜测 ItemId。

媒体匹配出现同分时同样会放弃自动替换，避免同名资源串片。

目前没有主动长期缓存 115 CDN 签名。

项目依赖 Hills 当前的本地数据结构。Hills 后续如果调整缓存格式、数据库字段或外部播放器协议，相关兼容逻辑可能需要同步更新。

## 构建

开发构建：

```powershell
dotnet build .\HillsMpvLauncher.csproj -c Release
```

Windows x64 self-contained 发布：

```powershell
dotnet publish .\HillsMpvLauncher.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true
```

输出目录：

```text
bin\Release\net8.0-windows\win-x64\publish\
```

## 说明

这个项目只处理 Hills Lite 到外部 mpv 之间的调用和取链兼容。

Hills Lite、Emby Server 和 mpv 均需单独安装和配置。
