# BiliGetter 的 MVVM 分层讲解

这份文档是为「以后回看 / 学习」写的，不是 API 手册。
它解释**为什么这样分层**、**每一层能做什么不能做什么**、**一个点击是怎么穿过各层的**，
以及踩过的坑。

对应代码版本：`v2.5.0-net10`。

---

## 0. 一句话原则

> **View 不认识业务，ViewModel 不认识控件，Service 不弹窗。**

三条里最容易破的是第二条。只要 ViewModel 里出现 `MessageBox`、`TxtXxx.Text`、
`File.ReadAllText`，分层就已经漏了。

---

## 1. 改造前的样子（为什么值得改）

`MainWindow.xaml.cs` 一个文件 **560 行**，同时做了六件事：

| 它当时干的事 | 具体表现 |
|---|---|
| 存状态 | `_items`、`_busy`、`_settings`、`_curIndex`… 十几个字段 |
| 读界面 | `TxtUrl.Text.Trim()`、`ChkAria2c.Checked` |
| 写界面 | `_lblSummary.Text = ...`、`_list.Items[i].SubItems[4].Text = ...` |
| 编排线程 | `new Thread(delegate { ... })` + `Ui(delegate { ... })` 手工切回 UI |
| 读写配置 | `LoadConfig()` / `SaveConfig()` 直接 `File.WriteAllText` |
| 找工具、弹框 | `Tools.FindFfmpeg()`、`MessageBox.Show(this, ...)` |

后果不是"不好看"，而是三个实际麻烦：

1. **测不了**。想知道"解析逻辑对不对"，只能开窗口、粘贴地址、点按钮、用眼睛看。
2. **改一处动全身**。想给命令行也做同样的解析，只能复制粘贴那段逻辑。
3. **症状藏在深处**。日志滚动、按钮灰化、进度条三者纠缠在同一个方法里。

---

## 2. 四层结构

```
   ┌─────────────────────────────────────────────────────┐
   │  Views（.xaml）      只做布局 + 绑定                  │
   │  MainWindow / LoginWindow / ToolsWindow              │
   └───────────────┬─────────────────────▲───────────────┘
       数据绑定 / ICommand                │ 属性变更通知(INPC)
                   ▼                     │
   ┌─────────────────────────────────────┴───────────────┐
   │  ViewModels          状态 + 命令 + 校验               │
   │  MainViewModel / ItemViewModel / LoginViewModel /     │
   │  ToolsViewModel                                      │
   └───────────────┬─────────────────────▲───────────────┘
        调用接口                    回调 / 事件
                   ▼                     │
   ┌─────────────────────────────────────┴───────────────┐
   │  Services            把「VM 不该做的事」藏起来        │
   │  IBiliService / IDownloadService / IConfigService     │
   │  IToolLocator / IDialogService / IQrImageFactory      │
   └───────────────┬─────────────────────────────────────┘
                   ▼
   ┌─────────────────────────────────────────────────────┐
   │  Core（完全不知道上面三层存在）                        │
   │  BiliApi / Fetcher / Download / Json / QrCode         │
   └─────────────────────────────────────────────────────┘
```

规则只有一条：**箭头单向**。Core 不许引用上面任一层，Service 不许 `using System.Windows`，
ViewModel 不许 `using System.Windows.Controls`。
（`MainViewModel.cs` 的 using 里只有 `System.*` 和 `CommunityToolkit.Mvvm.*`，这就是判据。）

---

## 3. 文件地图

| 文件 | 行数 | 职责 | 关键成员 |
|---|---:|---|---|
| `MainWindow.xaml` | ~330 | 主界面布局与绑定，**零业务** | 11 个按钮的 `Command` 绑定 |
| `MainWindow.xaml.cs` | **24** | 挂 DataContext + 日志滚动 | `LogTextChanged` |
| `LoginWindow.xaml` / `.cs` | ~110 / 16 | 扫码窗 | 转发 `vm.Initialize()` |
| `ToolsWindow.xaml` / `.cs` | ~120 / 12 | 工具路径窗 | 只有 `InitializeComponent` |
| `MainViewModel.cs` | ~510 | 主界面全部状态与编排 | `FetchCommand`/`DownloadCommand`/`StopCommand` |
| `ItemViewModel.cs` | 32 | 列表一行 | `IsSelected` / `Status` / `Quality` |
| `LoginViewModel.cs` | 123 | 取码 + 轮询 | `QrImage` / `StatusText` |
| `ToolsViewModel.cs` | 80 | 校验工具路径 | `OkCommand` |
| `Services.cs` | 132 | **全部接口 + 小数据类** | `AppConfig` / `IDownloadObserver` |
| `BiliService.cs` | 68 | 包 `BiliSession` + `Fetcher` | `Build` / `ProbeQuality` |
| `DownloadService.cs` | 67 | 把同步循环包成 `Task` | `RunAsync` |
| `ConfigService.cs` | 61 | ini 读写 + 相对路径 | `Resolve` / `Storable` |
| `ToolLocator.cs` | 24 | 找 exe、判断是否在程序目录 | `FindFfmpeg` |
| `WpfDialogService.cs` | 91 | `IDialogService` 的 WPF 实现 | `ShowLogin` / `ShowTools` |
| `AppHost.cs` | 22 | 唯一装配点 | `CreateMainViewModel` |

对比一下：`MainWindow.xaml.cs` 从 **560 行 → 24 行**，业务全部搬进 `MainViewModel`。

---

## 4. 三条完整调用链（最值得看的一节）

### 4.1 「解析」按钮

```
XAML:  <Button Content="解析" Command="{Binding FetchCommand}" />
  │
  ├─ 源生成器把 MainViewModel 里的  public async Task FetchAsync()  编译成  FetchCommand
  │
  ├─ WPF 先问 CanExecute：CanWork() → !IsBusy → 决定按钮灰不灰
  │
  └─ 点下去执行 FetchAsync()（MainViewModel.cs:277）
        ① 取 Url，空就 _dialogs.Info("请先填写…")
        ② OutDir = _config.Resolve(OutDir)           ← 相对路径按程序目录解析
        ③ Target t = _bili.Resolve(input)            ← 只问 Service，不认识 URL 规则
        ④ 无法识别 → _dialogs.Info("无法识别的地址…")  ← 不碰 MessageBox
        ⑤ IsBusy = true                              ← 按钮自动灰掉（CanExecute 联动）
        ⑥ await Task.Run(() => _bili.Build(t, Log))  ← 网络/解析在工作线程
        ⑦ Items.Add(new ItemViewModel(item, i+1))    ← DataGrid 自动出现行
        ⑧ Summary = "共 N 项…"                        ← TextBlock 自动更新
        ⑨ IsBusy = false                             ← 按钮自动恢复
```

关键点：**整个过程没有一行在说"哪个控件"**。
`Items` 是 `ObservableCollection`，加进去的行由 DataGrid 自己渲染；
`Summary` 是普通属性，赋值后由绑定推给 TextBlock。

### 4.2 「开始下载」按钮

```
DownloadCommand → DownloadAsync()（MainViewModel.cs:345）
  ① 校验：列表非空 → ffmpeg 在 → 至少勾一项（否则 ShowToolsDialog / Info）
  ② 把勾选同步回核心模型：Items[i].SyncSelection()
  ③ 组装 DownloadSettings（OutDir / QualityCap / FfmpegPath / Aria2cPath / UseAria2c）
  ④ _cts = new CancellationTokenSource()
  ⑤ await _download.RunAsync(sel, ds, this, _cts.Token)
                                └── 注意这个 this：ViewModel 自己就是观察者
  ⑥ 汇总结果写 Summary / AppendLog
```

`RunAsync` 在 `DownloadService.cs` 里做的是：

```csharp
using (ct.Register(delegate { d.Cancel(); }))   // 取消 → 同时杀 aria2c/ffmpeg 子进程
{
    for (int i = 0; i < items.Count; i++)
    {
        observer.ItemStarting(i + 1, items.Count, it);
        bool ok = d.DownloadOne(_bili.Session, it, null);
        observer.ItemFinished(it, ok);
    }
}
```

「停止」按钮 → `_cts.Cancel()` → 上面那个 `Register` 触发 `d.Cancel()`。
**没有全局变量、没有静态 Cancel 标志**，取消范围就锁在这一个批次里。

### 4.3 下载进度怎么回到界面（跨线程，最容易出错的地方）

```
aria2c 子进程 / HttpClient 读流
        │  (工作线程 / 监控线程)
        ▼
Downloader.Transfer  →  observer.Transferred(p)      p 是 TransferProgress
        │  (仍在后台线程！不能直接改属性)
        ▼
MainViewModel.OnUi(action)
        │
        ├─ _ui == null            → 直接执行（命令行 / vmtest 场景，没有 UI 线程）
        ├─ _ui == 当前上下文       → 直接执行（已经在 UI 线程，避免多余排队）
        └─ 否则                    → _ui.Post(action)  ← 排到 UI 线程
        ▼
FilePercent / SpeedText / TransferText 赋值
        │
        ▼
绑定自动刷新 ProgressBar / TextBlock
```

`OnUi` 只有 6 行，但它是**整个程序唯一的线程切换点**。
以前散落在二十几个 `Ui(delegate { ... })` 里，现在收成一处。

> 为什么不用 `Dispatcher`？因为 `System.Windows.Threading.Dispatcher` 是 WPF 类型，
> 一旦 VM 用它，VM 就不能在命令行或无 WPF 的环境里跑，`vmtest` 也就做不出来了。
> `SynchronizationContext` 是 .NET 基础类型，且在没有 UI 上下文时自动内联执行。

### 4.4 「登录（扫码）」

模态窗口是 MVVM 里最别扭的部分——ViewModel 不该 `new LoginWindow()`。这里的办法是：

```
MainViewModel.Login()                       ← 只造 ViewModel
    LoginViewModel lvm = new LoginViewModel(_bili, _qr, _dialogs);
    if (!_dialogs.ShowLogin(lvm)) return;   ← 由服务负责套窗口壳 + ShowDialog
    _bili.SaveCookiesTo(...)                ← 登录成功后保存

WpfDialogService.ShowLogin(vm)              ← 表现层边界在这
    LoginWindow w = new LoginWindow();
    w.DataContext = vm;
    vm.RequestClose = ok => { try { w.DialogResult = ok; } catch { } };
    return w.ShowDialog() == true;

LoginWindow.Loaded → vm.Initialize()        ← 视图就绪后再让 VM 开始取码/轮询
```

`LoginViewModel` 里那句 `RequestClose(true)` 就是"请求关闭窗口"，
它不知道窗口是什么，只知道"请关掉，结果是成功"。

---

## 5. 机制逐个讲

### 5.1 `[ObservableProperty]`

```csharp
[ObservableProperty] private string summary = "未解析";
```

编译期生成（大致等价于）：

```csharp
public string Summary
{
    get => summary;
    set { if (summary == value) return; summary = value; OnPropertyChanged("Summary"); }
}
```

手写要 5~7 行，一个界面十几个属性就是上百行样板。
**必须 `partial class`**，这是源生成器往类里加成员的前提。

字段小写（`summary`）、属性大写（`Summary`）——绑定时永远绑定位成生的那个属性名。

### 5.2 `[RelayCommand]` 与 `CanExecute`

```csharp
[RelayCommand(CanExecute = nameof(CanWork))]
public async Task FetchAsync() { ... }

private bool CanWork() { return !IsBusy; }
```

生成 `FetchCommand`（`IAsyncRelayCommand`）。**方法名里的 `Async` 后缀会被去掉**，
所以 `FetchAsync` → `FetchCommand`，不是 `FetchAsyncCommand`——写绑定的时候别猜错。

`CanExecute` 默认只在命令被创建时求值一次，之后**不会自动重算**。属性变了要主动通知：

```csharp
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(FetchCommand))]
[NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
private bool isBusy;
```

> 这里是最容易踩的坑：漏了 `NotifyCanExecuteChangedFor`，
> 症状是「下载时"开始下载"没变灰、下载完没变回来」。
> 这种 bug 不会报错，只能靠点界面发现。

### 5.3 三条线程

| 线程 | 从哪来 | 在里面做什么 |
|---|---|---|
| UI 线程 | WPF Dispatcher | 改属性、响应命令 |
| 工作线程 | `Task.Run` | `Build` 解析、`RunAsync` 下载循环 |
| 监控线程 | `FetchWithAria2c` 里 `new Thread` | 每 300ms 读一次输出文件体积算速度 |

规则：**工作线程只调 `observer.*`，绝不直接改属性**。`observer` 的实现里第一件事就是 `OnUi`。

### 5.4 为什么用 `IDownloadObserver` 而不是 `IProgress<T>`

`IProgress<T>` 适合"一条进度流"。这里的下载会同时产生四类事件：

```csharp
void ItemStarting(int ordinal, int total, DownloadItem item);  // 开始某一项
void ItemFinished(DownloadItem item, bool success);            // 某项结束
void Transferred(TransferProgress p);                          // 传了 300ms 的字节
void Log(string line);                                         // 一行日志
```

塞进一个 `Action<T>` 要么定义四个回调参数（`RunAsync` 会变成 8 个参数），
要么定义一个 `enum + object` 的怪东西。一个接口四个方法读起来最清楚。

### 5.5 `IDialogService`：为什么连弹个框都要接口

三个理由，按重要性排：

1. **可测**：`vmtest` 里用 `HeadlessDialogs` 实现，把它变成 `Console.WriteLine`。
   如果 VM 里写的是 `MessageBox.Show`，`vmtest` 会卡在那儿等一个看不见的窗口。
2. **可替换**：将来要做无头批量下载，换一个实现即可，VM 一行不改。
3. **边界清晰**：一眼就能看出"这个 VM 会跟用户交互哪些点"，因为都在接口里。

注意：接口里出现 `LoginViewModel` 这种类型是**正常**的。
`IDialogService` 本来就在表现层边界上，它和 VM 是同事关系，不是上下游。

### 5.6 依赖注入：为什么没用容器

只有 5 个服务、1 处装配，手工写就是 `AppHost.cs` 那 12 行：

```csharp
IBiliService bili = new BiliService();
IDownloadService download = new DownloadService(bili);
IConfigService config = new ConfigService();
IToolLocator tools = new ToolLocator();
IDialogService dialogs = new WpfDialogService();
IQrImageFactory qr = new QrImageFactory();
MainViewModel vm = new MainViewModel(bili, download, config, tools, dialogs, qr, autoStart);
```

引一个容器要多一个依赖、多一套注册代码、多一层"为什么这个类型没注册"的调试成本。
**服务数量到了十几个、或者有生命周期（单例/瞬态）需求时再上容器**，
那时只要改 `AppHost` 一个文件，其余代码不受影响——这才是"可替换"的价值。

### 5.7 绑定速查表

| 界面元素 | 绑定属性 | ViewModel 成员 |
|---|---|---|
| 地址输入框 | `Text` | `Url` |
| 输出目录框 | `Text` | `OutDir` |
| 清晰度下拉 | `ItemsSource` / `SelectedIndex` | `QualityItems` / `QualityIndex` |
| aria2c 勾选 | `IsChecked` / `IsEnabled` | `UseAria2c` / `Aria2cAvailable` |
| 账号文字 | `Text` | `AccountText` |
| 工具状态文字 | `Text` + `DataTrigger` | `ToolText` / `ToolsOk` |
| 项目列表 | `ItemsSource` | `Items` |
| 每行勾选 | `IsChecked` | `ItemViewModel.IsSelected` |
| 单文件进度条 | `Value` / `IsIndeterminate` | `FilePercent` / `FileIndeterminate` |
| 速度 | `Text` | `SpeedText` |
| 当前文件文字 | `Text` | `TransferText` |
| 整体进度条 | `Value` | `OverallPercent` |
| 摘要 | `Text` | `Summary` |
| 日志 | `Text`（OneWay） | `LogText` |

按钮全部走 `Command="{Binding XxxCommand}"`，没有一个 `Click` 事件。

---

## 6. 边界规则（八条"不要"）

1. **View 里不写业务判断**。XAML 里的 `DataTrigger` 只做视觉（如 ffmpeg 缺失变红）。
2. **ViewModel 里不出现**：`System.Windows.Controls`、`MessageBox`、`File`、`Process`、`Dispatcher`。
3. **Service 里不弹窗**（`IDialogService` 的实现除外）、不碰控件、不认识 UI 概念。
4. **Core 不认识上面任何一层**。它是唯一可以被别处复用的部分。
5. **不要从 VM 用 `x:Name` 反查控件**。需要"滚动到底"这类纯视觉行为，留 code-behind。
6. **不要在属性 setter 里做重活**。绑定会在意料之外的时机调用 setter。
7. **集合用 `ObservableCollection` 并原地增删**，不要每次重建整个 `ItemsSource`（会丢选中态）。
8. **一个命令只做一件事**。不写 `DoEverything()` 然后让十个按钮都调它。

---

## 7. 怎么加一个新功能（配方）

以「只下载音频」为例，**从下往上加**，每层只依赖下一层：

1. **Core**：`DownloadSettings` 加 `public bool AudioOnly;`，`StreamPicker` 按它选流。
2. **Services.cs**：`IDownloadService` 不用改（`DownloadSettings` 已经在参数里）。
3. **Service 实现**：把 `settings.AudioOnly` 传下去。
4. **ViewModel**：加一行 `[ObservableProperty] private bool audioOnly;`，
   `DownloadAsync` 里 `ds.AudioOnly = AudioOnly;`。
5. **XAML**：加一个 `<CheckBox Content="仅音频" IsChecked="{Binding AudioOnly}"/>`。
6. **验证**：`--cli vmtest <地址> 1` 跑一次，再看 `--cli ui` 截图是否错位。

只有第 5 步碰界面，第 1~4 步全都能在命令行里验证。

---

## 8. `vmtest` 为什么能证明设计成立

```
BiliGetter.exe --cli vmtest BV1GJ411x7h7 1
```

它做的和界面上点按钮**完全是同一段代码**：造 `MainViewModel` → 设 `Url` →
`await FetchAsync()` → 勾选 → `await DownloadAsync()`。

如果 VM 里还残留 `MessageBox.Show`，它会卡住；
如果 VM 里还 `new Thread` + 反查控件，没有 UI 线程就会崩；
如果 VM 依赖 `System.Windows`，在纯控制台上下文里就会出现莫名其妙的空引用。

所以「这段代码能编过、能跑通、结果正确」本身就说明依赖方向没走反。
这是分层带来的**可观测收益**，不是纸面architecture。

实测输出：

```
[vmtest] 解析结果: items=1   共 1 项   当前账号最高可用：1080P+ 高码率 1920x1080
[vmtest]   1. 【官方 MV】...   默认选中=True
[vmtest] 下载结果: 下载结束：成功 1 / 失败 0
[vmtest]   1. 完成
```

---

## 9. 代价与取舍

诚实地讲，这次重构不是白拿的：

| 项 | 变化 |
|---|---|
| `BiliGetter.exe` | 0.31 MB → **0.47 MB**（多了 CommunityToolkit.Mvvm） |
| 源文件数 | 12 → **24** |
| 改一个显示 | 可能要走 2~3 个文件（以前改一处 code-behind） |
| 概念数 | 多了 ViewModel / Service / 观察者 / CanExecute 联动 |

**什么时候不该上 MVVM**：一次性脚本、200 行以内的工具窗、纯展示页面。
本项目值得，是因为它有一个 2500 行的核心要被三种前端共用
（GUI / CLI / 未来可能的无头批量），而且界面状态确实复杂
（批次进度 + 单文件进度 + 速度 + 可取消 + 工具缺失状态 + 登录态）。

**行为零变化的证据**：`--cli ui` 的渲染结果重构前后 SHA256 完全相同
（`626F581F31745BFD`）——布局、间距、颜色一个像素都没动，只是"谁在管这些字"变了。

---

## 10. 学习路径（如果以后要自己写一遍）

1. 先写一个最小 VM：3 个属性 + 1 个命令，`Window.DataContext = vm`，确认界面能跟着变。
2. 把一处 `MessageBox.Show` 换成接口调用，再写一个假实现——体会"可测"是什么意思。
3. 把一个 `new Thread` + 手工切线程换成 `async/await`，删掉所有 `Ui(...)`。
4. 加一个 `CanExecute`，故意**先不加** `NotifyCanExecuteChangedFor`，看按钮不更新；
   加上之后再确认恢复。这个坑自己踩一次记得最牢。
5. 最后回头看本项目的 `MainViewModel.cs` 第 274~430 行（解析 + 下载 + 停止），
   对照第 4 节的调用链读，会发现结构其实很平。

---

## 附：一句话记住各层

| 层 | 一句话 |
|---|---|
| View | 我只会「把属性显示出来」和「把点击变成命令」 |
| ViewModel | 我只会「管状态」和「调接口」，不知道界面长什么样 |
| Service | 我只会「干活」，不会说话（不弹窗、不打印） |
| Core | 我谁都不认识，所以谁都能用我 |
