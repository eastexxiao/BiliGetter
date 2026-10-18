# BiliGetter —— Visual Studio 工程

这是 B 站下载器（`..\BiliGetter.exe`）的**标准 Visual Studio 解决方案**。

## 打开与编译

**双击 `BiliGetter.sln`**，用 Visual Studio 2022 或 2026 打开，然后 `Ctrl+Shift+B` 编译。
需要 **.NET Framework 4.8 开发工具**（VS 安装器里叫 ".NET Framework 4.8 targeting pack"）。

没有 VS 时用 MSBuild：

```
"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe" ^
  BiliGetter.sln /restore /p:Configuration=Release
```

> 本机上 `dotnet build BiliGetter.sln` 会失败但只报「0 个错误」。已验证**这不是工程的问题**：
> 同一份 `.sln` 用 VS2022 和 VS2026 的 MSBuild 都编译成功（exit 0），
> 单独 `dotnet build BiliGetter\BiliGetter.csproj` 也正常。
> 推测是本机 dotnet 的工作负载状态有问题（它确实在报 `dotnet workload update` 提示），但未确证。

## 解决方案里的两个项目

| 项目 | 输出 | 说明 |
| --- | --- | --- |
| `BiliGetter` | `BiliGetter.exe` | 图形界面版（`OutputType = WinExe`），双击运行的那个 |
| `BiliGetter.Cli` | `BiliGetter-cli.exe` | 命令行版（`OutputType = Exe`，`StartupObject = BiliGetter.CliTest`） |

两者都输出到各自的 `bin\Release\net48\`。

## 目录结构

```
VisualStudioProject\
  BiliGetter.sln                  解决方案（VS 2022/2026）
  Directory.Build.props           两个项目共用的编译设置（C# 7.3、net48、确定性构建）
  .gitignore                      忽略 bin\ obj\ .vs\
  src\                            共享核心源码
    App.cs                        界面 + 工具（ffmpeg/aria2c）查找
    BiliApi.cs                    HTTP / Cookie / WBI 签名 / 各接口
    Download.cs                   清晰度选择、权限识别、aria2c 与 ffmpeg 调用
    Fetcher.cs                    地址解析 → 下载清单；下载与合并
    Json.cs                       极简 JSON 解析（零第三方依赖）
    QrCode.cs                     二维码编码器
    QrTables.g.cs                 二维码常量表（由脚本生成）
    verify\QrTest.cs              二维码自检工具，独立，不参与主构建
  BiliGetter\BiliGetter.csproj
  BiliGetter.Cli\
    BiliGetter.Cli.csproj
    CliTest.cs                    命令行入口（本项目自有源码）
```

## 关键设计：为什么不拆成「核心库 + 两个 exe」

常见做法是把共用代码做成 `BiliGetter.Core.dll`，两个 exe 引用它。**这里故意不这么做**：
那样 `BiliGetter.exe` 旁边就必须多一个 DLL，破坏「单文件、拷了就能跑」的便携性。

所以两个 `.csproj` 都用**链接**的方式共享同一份源码：

```xml
<ItemGroup>
  <Compile Include="..\src\*.cs" LinkBase="src" />
</ItemGroup>
```

结果：`src\` 只有一份源码（不会出现两份代码不一致），而每个 exe 又都是**自包含的单文件**。
在 VS 的解决方案资源管理器里，这些文件会显示在项目下的 `src` 节点里（带快捷箭头图标，表示是链接）。

`src\verify\QrTest.cs` 没被通配到，是因为通配是 `..\src\*.cs`（不递归子目录）。

## 改完代码怎么更新便携版

编译产物：

- `BiliGetter\bin\Release\net48\BiliGetter.exe`
- `BiliGetter.Cli\bin\Release\net48\BiliGetter-cli.exe`

**把这两个 exe 复制到上一级 `..\`（也就是 `BiliGetter\`，和 `ffmpeg.exe` 放一起）覆盖即可。**
便携版里现成的那两个 exe 就是本工程 Release 的产物。

不用手动拷贝 ffmpeg：程序会**先找最近的同名 `ffmpeg.exe`**，所以从 `bin\Release\net48\`
里直接 F5 调试也能找到 `BiliGetter\ffmpeg.exe`（向上最多查 6 层）。

## 注意

- **不要**把 `src\` 再复制一份到别处然后各改各的，两边会分叉。源码只有 `src\` 这一份。
- 编译需要 .NET Framework 4.8 targeting pack。缺了的话 VS 会提示；
  命令行 `dotnet build` 会自动从 NuGet 拉 `Microsoft.NETFramework.ReferenceAssemblies`（需要联网）。
- 这两个 exe 是 IL（AnyCPU），在 64 位 Windows 上以 64 位运行，跟 `Source\ffmpeg.exe`（32 位）不冲突。
- `bin\`、`obj\` 是构建产物，已在 `.gitignore` 里；打包便携版时可以排除。
