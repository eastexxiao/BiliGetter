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

## 两种发布方式（体积差 160 倍）

| 方式 | 附加参数 | 产物 | 目标机器要求 |
| --- | --- | --- | --- |
| 自包含单文件（默认） | 无 | 约 **46.9 MB** | 无需安装任何运行时 |
| 依赖框架单文件 | `-p:SelfContained=false`
`-p:EnableCompressionInSingleFile=false`
`-p:IncludeNativeLibrariesForSelfExtract=false` | 约 **0.29 MB** | 需装 .NET 10 桌面运行时 |

完整命令示例（依赖框架版）：

```powershell
dotnet publish BiliGetter\BiliGetter.csproj -c Release `
  -p:SelfContained=false -p:EnableCompressionInSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=false
```

目标机器装一次运行时即可：`winget install Microsoft.DotNet.DesktopRuntime.10`

**为什么推荐依赖框架版**：它只有 0.29 MB、单文件、**不做自解压**——启动快，
也不容易被 Defender 当成可疑的自解压程序反复扫描。自包含版那 46.9 MB 里装的是
整个 .NET 运行时，每次启动都要解压到 `%TEMP%\.net\...`，这正是「点了允许但窗口
半天不出来」的来源。

依赖框架版需要目标机器已有运行时；没有时系统会弹窗提示去下载。
如果要求「拷到任何 Win10/11 都能直接双击」，就用自包含版。
## 更新便携版

发布产物：

```
BiliGetter\bin\Release\net10.0-windows\win-x64\publish\BiliGetter.exe
```

**复制到上一级 `..\`（和 `ffmpeg.exe` 放一起）覆盖**即可。
程序会先找最近的同名 `ffmpeg.exe`（向上 8 层），所以从 `publish\` 目录直接 F5
也能用到便携版的那一份。

## 代码结构（MVVM 分层）

界面层分四层，箭头单向：**View → ViewModel → Service → Core**。

| 层 | 做什么 | 关键约束 |
|---|---|---|
| `*.xaml` | 布局 + 绑定 | 不写业务判断，按钮一律 `Command` |
| `*ViewModel.cs` | 状态、命令、校验 | 不出现控件、`MessageBox`、`File`、`Dispatcher` |
| `*Service.cs` | 配置、网络、进程、对话框 | 不弹窗（对话框实现除外）、不认 UI 概念 |
| `BiliApi/Fetcher/Download` | 接口、选流、下载、合并 | 不认识上面三层，可被任何前端复用 |

`MainWindow.xaml.cs` 只有 24 行（挂 `DataContext` + 日志滚动），业务全在 `MainViewModel`。

**完整讲解见 [MVVM.md](MVVM.md)** —— 含三条调用链、机制逐个说明、八条边界规则、
加新功能的配方、以及 `cli vmtest` 为什么能证明分层成立。

验证分层是否还站得住：

    BiliGetter.exe cli vmtest BV1GJ411x7h7 1

不开窗口直接驱动 `MainViewModel` 走「解析 → 勾选 → 下载」，跑通即说明
ViewModel 没有偷偷依赖窗口。
## 便携性：相对路径与整体搬移

整个文件夹可以整体搬到 U 盘或别的机器，不改任何配置。规则只有一条：

**ini 里的相对路径，一律相对「BiliGetter.exe 所在目录」解析，与当前工作目录无关。**

所以下面这份配置搬到哪都有效：

    out=downloads
    ffmpeg_path=ffmpeg.exe
    aria2c_path=bin\aria2c.exe

反过来，程序保存配置时会**优先写相对路径**：凡是位于程序目录内的路径
（输出目录、两个工具），ini 里都存成相对形式；只有位于程序目录之外的
才存绝对路径（那种情况本来就不可搬移）。

实测过的四点：

1. 干净目录首次运行，写出的 ini 就是 `out=downloads`；
2. 把 ffmpeg / aria2c 放进 `bin\`（这样自动查找是找不到的），ini 只写
   `bin\ffmpeg.exe` 仍能正常下载并合并；
3. 把整个文件夹改名搬走，相对路径自动指向新位置，不需要重新指定；
4. 故意换一个工作目录启动，`out=downloads` 不会被改写成绝对路径。

工具查找顺序（未变）：程序目录 → 逐级向上最多 8 层 → 历史 `Source\` 布局 → PATH。
因此**只要把 ffmpeg.exe / aria2c.exe 和 BiliGetter.exe 放在一起**，ini 里连这两项都不用写。
## 已知限制：DRM 加密课程

部分付费课程（版权方要求）把媒体打包成 CENC 加密：视频轨 `encv`、音频轨 `enca`，
附带 `pssh` 保护系统头和每分片的 `senc` 样本加密表。

这类内容本工具无法下载，而且**不是程序缺陷**：

- ffmpeg 没有内容解密密钥，连文件头都读不了（`AVERROR_INVALIDDATA`）；
- 视频轨能"打开"，但每一帧解码都报错 —— 那是密文被当明码在解；
- 换 CDN 地址、重试、清缓存、换清晰度都无效，密钥只在授权的播放器里解出。

程序的处理方式是**尽早识别、立刻停下**：

- 先下音频分片（通常几 MB），检查 `moov/stsd` 的第一个条目是不是以 `enc` 开头；
- 一旦是加密的，马上清理临时分片并中止整批
  （同一门课所有分集是同一套加密），不会为了发现这一点白下上百 MB 的 4K 视频；
- 界面和命令行直接给出「CENC 加密」的结论，而不是让你去猜"文件是不是被占用了"。

想知道某门课能不能下，最省事的做法是只勾第 1 集先试：正常课程几秒就出 `.mp4`；
加密课程会在几 MB 后停下并说明原因。
## 注意

- `bin\`、`obj\` 是构建产物，已在 `.gitignore` 里；打包便携版时可排除。
- 单文件版首次启动会把原生库解压到 `%TEMP%`，所以**临时目录必须可写**；
  在只读环境里运行会无声退出。普通用户环境不受影响。
- 目标框架从 net48 升到 net10 后，**旧的 `bili-cookies.txt` / `bili-getter.ini`
  可以直接复用**，不需要重新登录或重设输出目录。

## 常见故障：下载报 `Access is denied`（错误 5）

**现象**：aria2c 报 `Failed to open the file ...\.biliget-tmp\<guid>\v.m4s, cause: Access is denied`，
ffmpeg 随后也报 `Error opening output ...mp4: Permission denied`。

**根因**：不是输出目录的问题，而是**工具不在受信任位置**。
Windows Defender「受控文件夹访问」按**可执行文件路径**放行 ——
`BiliGetter.exe` 被放行了，但放在别处的 `aria2c.exe` / `ffmpeg.exe` 没有，
所以它们创建**任何**文件都会被拒（`errNum=5`）。

实测对照（同一台机器）：

| 输出目录 | 工具位置 | 结果 |
| --- | --- | --- |
| `...\BiliGetter\dl` | 外部路径 | 失败 |
| `D:\Downloads\123` | 外部路径（未改） | 失败（证明输出目录无关） |
| `...\BiliGetter\dl` | 移到程序目录 | 成功（4K 16 集 / 4.31 GB） |

**解决（按省事程度）**：

1. **把 `aria2c.exe` 和 `ffmpeg.exe` 放进 `BiliGetter.exe` 同目录** —— `Tools.Find`
   本来就优先找自己的目录，不需要额外设置。（已实测有效）
2. 在「受控文件夹访问 → 允许应用通过」里逐个添加这两个 exe 的完整路径。
3. 换一个不受保护的输出目录（治标，且实测**不总能解决**，因为变量是工具而非目录）。

程序自身也有两道防线：开下前会试写一个探针文件（能发现"目录完全不可写"），
以及 aria2c 失败后自动回退到内置下载器。但探针由主程序执行，无法发现
"主程序能写、子进程不能写"这种情况 —— 所以上面的第 1 条仍是最可靠的配置。