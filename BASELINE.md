# 基线说明（重构前的已知良好状态）

这个仓库是 B 站下载器的**重构前基线**。打上基线标签时的状态如下，
后续「迁移到最新 .NET」的重构以这份基线为对照，功能不得回退。

- **基线标签**：`v1.0.0-net48`
- **技术栈**：.NET Framework 4.8（`net48`）、C# 7.3、WinForms、**零第三方 NuGet 依赖**
- **产物**：两个自包含单文件 exe —— `BiliGetter.exe`（图形界面）、`BiliGetter-cli.exe`（命令行）
- **仓库根**：就是本目录 `VisualStudioProject\`（`BiliGetter.sln` 在根上）

## 已验证的功能（基线时的实测结果）

| 功能 | 状态 | 说明 |
| --- | --- | --- |
| 扫码登录 | ✅ | 应用内生成二维码；回调轮询实测返回 `86101 未扫码`；真实账号登录后 `login=True` |
| Cookie 持久化 | ✅ | 存 `bili-cookies.txt` 供下次复用；保存时汇总多个 bilibili 域名 |
| 单视频 / 多 P | ✅ | 多 P 自动分集 |
| UP 主页递归 | ✅ | 实测一个 UP 解析出 **453 项**（5 合集 + 6 系列全量分页） |
| 合集 / 系列 | ✅ | 自动按合集名建子目录 |
| 课堂（cheese） | ✅ | 按 `index` 排序，跳过 `aid=0` 的空章节 |
| 清晰度自动最高 | ✅ | 未登录 480P → 登录后实测 **1080P+ 高码率 1920x1080**（按接口实际给出的流取最高） |
| 音频优选 | ✅ | FLAC > 杜比 > 192K > 132K > 64K，与视频档位独立 |
| aria2c 多线程 | ✅ | 可选；缺失时自动回退单线程 |
| ffmpeg 合并 | ✅ | 实测输出 `h264 + aac`，时长与源一致 |
| 断点续跑 | ✅ | 已存在且非空的文件自动跳过 |
| 充电专属识别 | ✅ | 正确识别 `is_upower_exclusive` / `is_upower_play`；**无权限时不产出假文件** |
| 二维码编码器 | ✅ | 与 qrcodejs 1.0.0 逐位比对，36 用例中 31 个有标准答案的全部一致 |
| 便携性 | ✅ | 无注册表、无 AppData；在隔离深度验证过工具查找不依赖上级目录 |

## 仓库内容

```
.gitignore                        构建产物忽略规则
BASELINE.md                       本文件
README.md                         工程说明（项目划分、链接共享原理、构建命令）
BiliGetter.sln                    解决方案
Directory.Build.props             两个项目共用编译设置
src/                              共享核心源码（两个 exe 链接同一份，不生成 DLL）
  App.cs                          界面 + ffmpeg/aria2c 查找
  BiliApi.cs                      HTTP / Cookie / WBI 签名 / 各接口 / 扫码登录
  Download.cs                     清晰度选择、权限识别、aria2c 与 ffmpeg 调用
  Fetcher.cs                      地址解析 → 清单；下载与合并
  Json.cs                         极简 JSON 解析（零第三方依赖）
  QrCode.cs  QrTables.g.cs        二维码编码器 + 常量表
  verify/QrTest.cs                二维码自检工具（独立，不参与主构建）
BiliGetter/BiliGetter.csproj          图形界面版（WinExe）
BiliGetter.Cli/BiliGetter.Cli.csproj  命令行版（Exe，StartupObject=BiliGetter.CliTest）
BiliGetter.Cli/CliTest.cs             命令行入口
```

## 仓库外的东西（在上一级 `..\` 便携运行目录里）

这些**不在版本控制内**，也不需要：它们是运行时数据或可重新获取的二进制。

| 位置（相对仓库根） | 说明 |
| --- | --- |
| `../ffmpeg.exe` | 合并音视频必需。从 ffmpeg 官网获取，或沿用 `LEL-Downloader/Source/ffmpeg/ffmpeg.exe` |
| `../aria2c.exe` | 可选，多线程下载；缺了自动单线程 |
| `../BiliGetter.exe`、`../BiliGetter-cli.exe` | 本工程的构建产物，编译后复制过去 |
| `../bili-cookies.txt` | **登录凭证**（SESSDATA 等），只在运行时生成，绝不入库 |
| `../bili-getter.ini` | 本机界面设置 |
| `../小亮/` 等 | 下载的视频 |
| `../README.md`、`../使用说明.txt`、`../第三方组件说明.txt`、`../ffmpeg.LICENSE.txt` | 面向使用者的文档与许可文本，随便携包分发 |

**把仓库还原成可运行状态**：编译 `BiliGetter.sln` → 把
`BiliGetter/bin/Release/net48/BiliGetter.exe` 和 `BiliGetter.Cli/bin/Release/net48/BiliGetter-cli.exe`
复制到上一级 `..\`，并确认 `..\ffmpeg.exe` 存在。

## 已知限制（基线时）

1. **UP 的散装投稿拿不到**：`space/wbi/arc/search` 触发风控 `-352`，需要整套浏览器 Cookie。
   合集与系列不受影响。
2. **番剧 / 影视（pgc）未实现**：会提示改用 `Source/BBDown.exe`。
3. **充电专属只验证到「无权限」一侧**：没有已充电的账号，所以「充电后
   `is_upower_play` 变为 `true` 并能取得完整 DASH」这一步未实测（推理依据见 `../README.md`）。
4. **本机 `dotnet build BiliGetter.sln` 会失败**（只报「0 个错误」）：
   同一份 `.sln` 用 VS2022 / VS2026 的 MSBuild 均编译成功，单个项目 `dotnet build` 也正常，
   因此判定与本工程无关（疑为 dotnet 工作负载状态问题，未确证）。

## 下一步：迁移到最新 .NET

重构目标与约束（供后续对照）：

- 目标框架改为当前最新的 .NET（`net10.0-windows` 或届时最新 LTS）。
- **必须保住便携性**：仍要能产出免安装、可整包拷走的产物
  （`PublishSingleFile` / `SelfContained`），不能变成「用户还得先装运行时」。
- 需要替换或重估的 .NET Framework 专属 API：
  - `HttpWebRequest` / `HttpWebResponse` → `HttpClient`（注意复用 `SocketsHttpHandler`）
  - `CookieContainer` 仍可用，改为挂到 `HttpClientHandler.CookieContainer`
  - `System.Drawing`（`Bitmap` / `Graphics`）→ .NET 10 上 `System.Drawing.Common`
    仅 Windows 支持且需评估，或改 `ImageSharp`
  - WinForms 在 `net10.0-windows` 上支持良好，可平滑迁移
  - `CookieContainer` 没有 `GetAllCookies()` 的历史包袱可以借机清掉
- 顺带可清理的技术债：
  - `Util.Run` 的 `out string` 同步捕获可改 `Process` + 异步读取
  - 手写 `Json.cs` 可评估改用 `System.Text.Json`（但会引入依赖，需权衡是否仍单文件）
  - `App.cs` 838 行，界面与工具查找应拆开
- **行为契约**：上文「已验证的功能」表必须逐项回归通过才算重构成功。
