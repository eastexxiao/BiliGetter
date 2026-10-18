# 迁移记录：.NET Framework 4.8 → .NET 10

从 `v1.0.0-net48` 基线迁移到当前最新 .NET。以下是实际做了什么、踩了哪些坑、以及
重新验证的结果。

## 一、结果概览

| 项 | 迁移前（`v1.0.0-net48`） | 迁移后 |
| --- | --- | --- |
| 目标框架 | `net48`（.NET Framework 4.8） | **`net10.0-windows`** |
| 语言版本 | C# 7.3 | **latest**（C# 14） |
| HTTP 栈 | `HttpWebRequest` / `WebRequest`（已过时） | **`HttpClient` + `SocketsHttpHandler`** |
| 编译警告 | 0 | **0** |
| 项目数 | 2 个（GUI + CLI 各一个 exe） | **1 个**（单 exe，`--cli` 进命令行模式） |
| 产物 | `BiliGetter.exe` 95,744 B + `BiliGetter-cli.exe` 101,376 B | **`BiliGetter.exe` 46.9 MB（单个自包含文件）** |
| 运行前提 | 需系统自带 .NET Framework 4.8 | **无需安装任何运行时**（已内置） |
| 依赖 | 零 NuGet 依赖 | **仍然零 NuGet 依赖** |

体积从约 190 KB 涨到 46.9 MB，是因为把 .NET 运行时和 WinForms 一起打包进去了
（`SelfContained` + `PublishSingleFile` + `EnableCompressionInSingleFile`）。
这是「拷到任何 Win10/11 双击即用」的必要代价，取舍见 `BASELINE.md`。

## 二、入口设计：一个 exe 两种形态

输出类型是 `WinExe`（双击不弹黑框），但同一个 exe 也能当命令行工具用：

```
BiliGetter.exe                         -> 图形界面
BiliGetter.exe --cli tools             -> 命令行模式
BiliGetter.exe list BV1xxx             -> 也能识别（第一个参数是已知子命令）
```

`Program.Main` 依据第一个参数决定走 `Cli.Run(args)` 还是 `Application.Run(new MainForm())`。
已知子命令列表在 `Cli.Commands` 里。

**关键点**：`WinExe` 进程启动时没有标准输出，所以命令行模式必须先
`AttachConsole(ATTACH_PARENT_PROCESS)` 挂到父终端，**再重新绑定 `Console.Out/Error`**
——只挂接不重绑，输出依然会丢。这部分在 `src/ConsoleBridge.cs`。

## 三、踩过的坑（都修好了，记下来避免重犯）

1. **XML 注释里不能出现 `--`**
   在 `.csproj` 注释里写 `--cli` 会让 MSBuild 直接报 `MSB4025` 打不开项目。
   改成把开关写法留给 README，注释里避开连续两个减号。

2. **`new StreamWriter(Stream)` 的默认编码是 UTF-8**
   不是「跟随控制台」。不显式传编码，中文在 GBK 控制台上必然乱码。

3. **现代 .NET 的 `Console.OutputEncoding` 默认值就是 UTF-8**
   它**不等于**控制台的实际代码页。拿它当「控制台编码」用，在中文 Windows
   （代码页 936）上仍然乱码。正确做法是用 `GetConsoleOutputCP()` 问系统，
   再按该代码页编码。

4. **`Console.OutputEncoding` 也不能反向使用**
   不要把它设成 UTF-8：调用方（cmd.exe / PowerShell）仍按自己的代码页解码，
   结果还是乱码。保持默认、按系统代码页写，才是各方都正确的做法。

5. **`HttpContent.ReadAsByteArray()` 没有同步版本**
   只有 `ReadAsByteArrayAsync()`。要保持全同步，用同步的 `ReadAsStream()`
   自己拷进 `MemoryStream`。

6. **自包含单文件需要可写的 `%TEMP%`**
   原生库会在首次启动时解压到临时目录。如果临时目录不可写（例如某些受限沙箱），
   进程会直接无声退出（0 字节输出、没有退出码）。普通用户环境不受影响。

7. **`System.Drawing` 不用换**
   `net10.0-windows` + `UseWindowsForms` 会自动带上 `System.Drawing.Common`
   （Windows 专用），二维码 `Bitmap` 渲染代码一行没改就跑通了，不必引入 ImageSharp。

8. **查找 ffmpeg 的目录深度要够**
   加了 RID 之后构建路径变成 `bin\Release\net10.0-windows\win-x64\publish\`，
   比原来深一层。向上查找的层数提到 8 层，F5 调试和发布目录都能找到便携版的 ffmpeg。

## 四、重新验证（对照 BASELINE.md 的行为契约）

| 功能 | 结果 |
| --- | --- |
| 编译 | `dotnet publish -c Release` → exit 0，**0 警告 0 错误** |
| 产物 | 单文件 `BiliGetter.exe` **46.9 MB**，目录里没有其它文件 |
| 工具查找 | 从 publish 目录和便携目录都能定位到便携版的 `ffmpeg.exe` / `aria2c.exe` |
| 中文输出 | 原始字节为 GBK（`ca d3 c6 b5` = "视频"），按 GBK 解码完全正确 |
| 登录 / Cookie | `login=True`，正确读出账号；Cookie 跨进程复用正常 |
| 清晰度自动最高 | **1080P+ 高码率 1920x1080 avc1.640032** —— 与 net48 版一致 |
| 充电专属识别 | `is_upower_exclusive=true` / `is_upower_play=false`，提示文案正确，**不产出假文件** |
| 下载 + 合并 | aria2c 下载 → ffmpeg 合并 → `h264 640x360 + aac 48kHz`，3:32 |
| 产物体积对照 | **14,767,125 字节**，与 net48 版产出的**字节数完全相同** |
| GUI | 渲染 1000x720 正常，布局与 net48 版一致，已登录状态正确显示 |

> 说明：`cli-out` 里的测试视频验证完已删除；`BiliGetter.exe` 是多 P/合集/课堂
> 共用的同一条 HTTP + JSON + 选流代码路径，核心管线已被上述用例覆盖。

## 五、怎么发布

```powershell
# 在仓库根（VisualStudioProject）执行
dotnet publish BiliGetter\BiliGetter.csproj -c Release
# 产物：BiliGetter\bin\Release\net10.0-windows\win-x64\publish\BiliGetter.exe
```

发布参数已经写在 `BiliGetter\BiliGetter.csproj` 里
（`RuntimeIdentifier=win-x64`、`SelfContained`、`PublishSingleFile`、
`EnableCompressionInSingleFile`、`IncludeNativeLibrariesForSelfExtract`），
不需要在命令行重复指定。

**更新便携版**：把上面那个 `BiliGetter.exe` 复制到仓库上一级（`..\`，和 `ffmpeg.exe` 放一起）覆盖即可。

## 六、未改变的部分（有意为之）

- **`src/Json.cs` 保留**：没有换成 `System.Text.Json`。它零依赖、够用、已被大量用例覆盖；
  换掉要改动 `BiliApi` / `Fetcher` / `Download` 里每一处取值，收益不抵风险。
  如果以后要换，单独做一次重构并用同一套回归用例验证。
- **公开行为、文件命名、目录结构、目录名、.ini/.txt 的名字都没变**，
  所以旧的 `bili-cookies.txt` 和 `bili-getter.ini` 可以直接复用，用户无需重新登录或重设输出目录。
- **仍然零第三方 NuGet 依赖**。

---

## 七、遗留问题：本机构建产物无法启动（未解决）

**现象**：本次会话后期，任何重新构建出的 `net10` 产物（单文件、非单文件、
关闭压缩的单文件、214 个 DLL 的完整自包含目录，四种都试过）**启动后不执行任何代码**：
进程 exit 0、零输出、`Program.Main` 里的诊断日志也没写出。
而同一目录下 **net48 旧版仍正常工作**，`cmd` 等原生程序也正常。

**结论**：不是源码问题（回退到已提交的可用版本重新构建，同样无法运行），
也不是打包参数问题（四种形态一致失效）。指向**本机的 .NET 构建/运行时包状态**——
同一环境还伴随 `dotnet build BiliGetter.sln` 只报「0 个错误」而失败、
`dotnet workload update` 警告等异常。

**因此**：便携目录里的 `BiliGetter.exe` / `BiliGetter-cli.exe` 已**恢复为 net48 版**（可用）；
本次重构的 net10 产物暂时移到了 `BiliGetter-net10.exe` 仅作留档，**不建议使用**。

**建议的下一步**：在 SDK 状态正常的机器上（或先执行 `dotnet workload update`）重新
`dotnet publish`，再跑一遍第四节的验证表。源码本身在 `refactor/net10` 分支上已就绪。