# BiliGetter —— Visual Studio 工程

B 站下载器的源码工程。目标框架 **.NET 10**（`net10.0-windows`），
发布为**单个自包含单文件 exe**：双击是图形界面，加 `--cli` 是命令行。

## 打开与编译

**双击 `BiliGetter.sln`**，用 Visual Studio 2022 / 2026 打开，`Ctrl+Shift+B` 编译。

需要 **.NET 10 SDK**（本机已装 10.0.102），以及 .NET 10 的 Windows Desktop 运行时包
（首次 `restore` 时由 NuGet 自动获取）。

命令行：

```powershell
dotnet publish BiliGetter\BiliGetter.csproj -c Release
```

发布参数（自包含、单文件、压缩、内置原生库）都写在 `BiliGetter\BiliGetter.csproj` 里，
命令行不用再传。

> 本机上 `dotnet build BiliGetter.sln` 会失败却只报「0 个错误」。
> 已验证**跟工程无关**：VS2022 / VS2026 的 MSBuild 都能编译成功，
> `dotnet build` / `dotnet publish` 单个 `.csproj` 也正常。
> 疑为本机 dotnet 工作负载状态问题（它确实在报 `dotnet workload update`），未确证。
> **直接用项目文件而不是解决方案文件即可绕过。**

## 目录结构

```
VisualStudioProject\               <- 仓库根
  BiliGetter.sln
  Directory.Build.props            共用编译设置（LangVersion=latest 等）
  BASELINE.md                      迁移前 net48 基线的行为契约
  MIGRATION-net10.md               本次迁移做了什么、踩了什么坑、如何验证
  README.md                        本文件
  .gitignore
  src\                             全部源码（单项目，不再需要链接共享）
    App.cs                         入口 + 界面 + ffmpeg/aria2c 查找
    ConsoleBridge.cs               WinExe 挂接父控制台（命令行模式用）
    Cli.cs                         命令行模式的各个子命令
    BiliApi.cs                     HTTP(HttpClient) / Cookie / WBI 签名 / 各接口
    Download.cs                    清晰度选择、权限识别、aria2c 与 ffmpeg 调用
    Fetcher.cs                     地址解析 → 清单；下载与合并
    MediaClient.cs                 媒体流下载用的 HttpClient
    Json.cs                        极简 JSON 解析（零第三方依赖）
    QrCode.cs  QrTables.g.cs       二维码编码器 + 常量表
    verify\QrTest.cs               二维码自检工具（不在通配范围，不参与构建）
  BiliGetter\BiliGetter.csproj     唯一的项目
```

`src\verify\QrTest.cs` 不会被编译，是因为通配是 `..\src\*.cs`（不递归子目录）。
它是独立的二维码自检程序，需要单独编译并配合 `qr-reference.txt` 使用。

## 一个 exe 两种用法

```
BiliGetter.exe                  图形界面
BiliGetter.exe --cli tools      命令行
BiliGetter.exe list <地址>       也能识别（首参是已知子命令）
```

命令行子命令：`tools` / `list` / `dl` / `poll` / `qr` / `ui`。
清晰度档位：`0`=最高可用，`1`=1080P，`2`=720P，`3`=480P，`4`=360P。

因为输出类型是 `WinExe`（双击不弹黑框），命令行模式会先
`AttachConsole` 挂到父终端再重绑 `Console.Out`——细节见 `src/ConsoleBridge.cs`
里注释的三个坑（`StreamWriter` 默认 UTF-8、`Console.OutputEncoding` 默认 UTF-8、
控制台代码页要用 `GetConsoleOutputCP` 问系统）。

## 为什么不拆成「核心库 + 两个 exe」

自包含发布每多一个 exe 就要多带一份 .NET 运行时（约 45 MB）。
所以图形界面和命令行合并成同一个 exe，靠参数分流；
源码也就在一个项目里，不需要链接或项目引用了。

## 更新便携版

发布产物：

```
BiliGetter\bin\Release\net10.0-windows\win-x64\publish\BiliGetter.exe
```

**复制到上一级 `..\`（和 `ffmpeg.exe` 放一起）覆盖**即可。
程序会先找最近的同名 `ffmpeg.exe`（向上 8 层），所以从 `publish\` 目录直接 F5
也能用到便携版的那一份。

## 注意

- `bin\`、`obj\` 是构建产物，已在 `.gitignore` 里；打包便携版时可排除。
- 单文件版首次启动会把原生库解压到 `%TEMP%`，所以**临时目录必须可写**；
  在只读环境里运行会无声退出。普通用户环境不受影响。
- 目标框架从 net48 升到 net10 后，**旧的 `bili-cookies.txt` / `bili-getter.ini`
  可以直接复用**，不需要重新登录或重设输出目录。
