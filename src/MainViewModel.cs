// 主窗口的 ViewModel。
//
// 这里集中了原来 MainWindow.xaml.cs 里的全部状态与编排：
// 账号、解析、勾选、下载、进度、日志、配置。
// 不引用任何 WPF 类型（图像用 object、对话框走 IDialogService、
// 线程用 SynchronizationContext），因此可以脱离界面驱动 —— 见 cli vmtest。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliGetter
{
    internal partial class MainViewModel : ObservableObject, IDownloadObserver
    {
        private readonly IBiliService _bili;
        private readonly IDownloadService _download;
        private readonly IConfigService _config;
        private readonly IToolLocator _tools;
        private readonly IDialogService _dialogs;
        private readonly IQrImageFactory _qr;
        private readonly SynchronizationContext _ui;
        private readonly StringBuilder _log = new StringBuilder();
        private readonly bool _autoStart;

        private AppConfig _cfg = new AppConfig();
        private CancellationTokenSource _cts;

        private int _curIndex;
        private int _curTotal;
        private string _curTitle = "";
        private int _doneCount;
        private int _totalCount;

        public ObservableCollection<ItemViewModel> Items { get; private set; }

        /// <summary>清晰度下拉的选项，索引与 Quality.CapForIndex 一一对应。</summary>
        public string[] QualityItems { get { return QualityNames; } }

        private static readonly string[] QualityNames = { "最高可用", "1080P", "720P", "480P", "360P" };

        // ------------------------------------------------------------ 可绑定状态
        [ObservableProperty] private string url = "";
        [ObservableProperty] private string outDir = "";
        [ObservableProperty] private int qualityIndex;
        [ObservableProperty] private bool useAria2c = true;

        [ObservableProperty] private string accountText = "";
        [ObservableProperty] private string toolText = "";
        [ObservableProperty] private bool toolsOk = true;
        [ObservableProperty] private bool aria2cAvailable;

        [ObservableProperty] private string summary = "未解析";
        [ObservableProperty] private string transferText = "";
        [ObservableProperty] private string speedText = "";
        [ObservableProperty] private double filePercent;
        [ObservableProperty] private bool fileIndeterminate;
        [ObservableProperty] private double overallPercent;
        [ObservableProperty] private string logText = "";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
        private bool isBusy;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StopCommand))]
        private bool canStop;

        public MainViewModel(IBiliService bili, IDownloadService download, IConfigService config,
                             IToolLocator tools, IDialogService dialogs, IQrImageFactory qr, bool autoStart)
        {
            _bili = bili;
            _download = download;
            _config = config;
            _tools = tools;
            _dialogs = dialogs;
            _qr = qr;
            _autoStart = autoStart;
            _ui = SynchronizationContext.Current;
            Items = new ObservableCollection<ItemViewModel>();
        }

        /// <summary>读配置、解析工具路径并刷新工具状态。不弹窗、不联网。</summary>
        public void Initialize()
        {
            _cfg = _config.Load();
            OutDir = _cfg.OutDir;
            QualityIndex = ClampQuality(_cfg.QualityIndex);
            UseAria2c = _cfg.UseAria2c;
            ApplyToolSettings();
        }

        /// <summary>
        /// 主窗口显示之后调用：加载登录态、检查工具。
        /// 这里可能弹模态的「工具路径」窗，所以必须晚于窗口可见，
        /// 否则对话框会出现在一个还看不见的主窗口前面。
        /// </summary>
        public void OnLoaded()
        {
            if (!_autoStart) return;
            RefreshAccount();
            EnsureTools();
        }

        /// <summary>窗口关闭时调用，把当前设置写回 ini。</summary>
        public void SaveState()
        {
            _cfg.OutDir = _config.Resolve(OutDir);
            _cfg.QualityIndex = QualityIndex;
            _cfg.UseAria2c = UseAria2c;
            _config.Save(_cfg);
        }

        private static int ClampQuality(int index)
        {
            if (index < 0 || index >= QualityNames.Length) return 0;
            return index;
        }

        // ------------------------------------------------------------ 线程切换
        /// <summary>把动作切回创建 ViewModel 的线程（WPF 下就是 UI 线程）。</summary>
        private void OnUi(Action a)
        {
            if (a == null) return;
            if (_ui == null || _ui == SynchronizationContext.Current) { a(); return; }
            _ui.Post(delegate { a(); }, null);
        }

        // ------------------------------------------------------------ 日志
        private void AppendLog(string line)
        {
            OnUi(delegate
            {
                _log.Append(line);
                _log.Append("\r\n");
                LogText = _log.ToString();
            });
        }

        // ------------------------------------------------------------ 账号
        private void RefreshAccount()
        {
            try { _bili.LoadCookiesFrom(_config.CookieFile); }
            catch { }
            try { _bili.Init(); }
            catch (Exception ex) { AppendLog("初始化会话失败：" + ex.Message); }
            UpdateAccountLabel();
        }

        private void UpdateAccountLabel()
        {
            AccountText = _bili.IsLogin
                ? "账号：" + _bili.UserName + "  (mid=" + _bili.Mid + ")  已登录"
                : "账号：未登录 —— 只能下到 480P，登录后可到 1080P+，大会员可到 4K/8K";
        }

        [RelayCommand]
        private void Login()
        {
            LoginViewModel vm = new LoginViewModel(_bili, _qr, _dialogs);
            if (!_dialogs.ShowLogin(vm)) return;

            try
            {
                _bili.SaveCookiesTo(_config.CookieFile);
                AppendLog("已保存登录信息到 " + _config.CookieFile);
            }
            catch (Exception ex) { AppendLog("保存登录信息失败：" + ex.Message); }
            UpdateAccountLabel();
        }

        [RelayCommand]
        private void Logout()
        {
            _bili.ClearCookies(_config.CookieFile);
            AppendLog("已删除本地保存的登录信息（浏览器里的登录不受影响）");
            _dialogs.Info("已退出登录。重开程序生效。", "提示");
        }

        // ------------------------------------------------------------ 工具
        private void ApplyToolSettings()
        {
            _cfg.FfmpegPath = _config.Resolve(_cfg.FfmpegPath);
            _cfg.Aria2cPath = _config.Resolve(_cfg.Aria2cPath);

            if (string.IsNullOrEmpty(_cfg.FfmpegPath) || !File.Exists(_cfg.FfmpegPath))
                _cfg.FfmpegPath = _tools.FindFfmpeg();
            if (string.IsNullOrEmpty(_cfg.Aria2cPath) || !File.Exists(_cfg.Aria2cPath))
                _cfg.Aria2cPath = _tools.FindAria2c();

            bool hasFfmpeg = !string.IsNullOrEmpty(_cfg.FfmpegPath);
            bool hasAria2c = !string.IsNullOrEmpty(_cfg.Aria2cPath);

            string text = "ffmpeg: " + (hasFfmpeg ? _cfg.FfmpegPath : "未找到（必需）")
                        + "   ·   aria2c: " + (hasAria2c ? _cfg.Aria2cPath : "未找到（可选）");

            // 工具放在程序目录外时，可能被「受控文件夹访问」挡住，只提醒不阻止
            if ((hasFfmpeg && !_tools.IsInsideApp(_cfg.FfmpegPath))
                || (hasAria2c && !_tools.IsInsideApp(_cfg.Aria2cPath)))
                text += "   ⚠ 不在程序目录：若报 Access is denied，请把工具移回程序目录";

            ToolText = text;
            ToolsOk = hasFfmpeg;
            Aria2cAvailable = hasAria2c;
            if (!hasAria2c) UseAria2c = false;
        }

        private void EnsureTools()
        {
            if (!string.IsNullOrEmpty(_cfg.FfmpegPath) && !string.IsNullOrEmpty(_cfg.Aria2cPath)) return;
            ShowToolsDialog(true);
        }

        [RelayCommand]
        private void ShowTools() { ShowToolsDialog(false); }

        private void ShowToolsDialog(bool startup)
        {
            ToolsViewModel vm = new ToolsViewModel(_dialogs, startup);
            vm.FfmpegPath = _cfg.FfmpegPath ?? "";
            vm.Aria2cPath = _cfg.Aria2cPath ?? "";
            if (!_dialogs.ShowTools(vm)) return;

            _cfg.FfmpegPath = vm.FfmpegPath;
            _cfg.Aria2cPath = string.IsNullOrEmpty(vm.Aria2cPath) ? null : vm.Aria2cPath;
            ApplyToolSettings();
            SaveState();
            AppendLog("工具路径已更新：");
            AppendLog("  ffmpeg = " + (_cfg.FfmpegPath ?? "(未指定)"));
            AppendLog("  aria2c = " + (_cfg.Aria2cPath ?? "(未指定)"));
        }

        // ------------------------------------------------------------ 输入
        [RelayCommand]
        private void Paste()
        {
            string t = _dialogs.PasteText();
            if (!string.IsNullOrEmpty(t)) Url = t.Trim();
        }

        [RelayCommand]
        private void Browse()
        {
            string p = _dialogs.PickFolder(_config.Resolve(OutDir));
            if (!string.IsNullOrEmpty(p)) OutDir = p;
        }

        [RelayCommand]
        private void OpenDir()
        {
            try { _dialogs.OpenFolder(_config.Resolve(OutDir)); }
            catch (Exception ex) { AppendLog("打开目录失败：" + ex.Message); }
        }

        [RelayCommand]
        private void SelectAll()
        {
            for (int i = 0; i < Items.Count; i++) Items[i].IsSelected = true;
        }

        [RelayCommand]
        private void SelectNone()
        {
            for (int i = 0; i < Items.Count; i++) Items[i].IsSelected = false;
        }

        // ------------------------------------------------------------ 解析
        private bool CanWork() { return !IsBusy; }

        [RelayCommand(CanExecute = nameof(CanWork))]
        public async Task FetchAsync()
        {
            string input = (Url ?? "").Trim();
            if (input.Length == 0)
            {
                _dialogs.Info("请先填写视频 / UP 主页 / 合集 / 课程地址。", "提示");
                return;
            }
            if ((OutDir ?? "").Trim().Length == 0) OutDir = _config.DefaultOutDir;

            // 相对路径按程序目录解析（保持便携），并把结果回显到输入框
            OutDir = _config.Resolve(OutDir);

            Target t = _bili.Resolve(input);
            if (t.Kind == TargetKind.Unknown)
            {
                _dialogs.Info("无法识别的地址。\r\n支持：BV号 / 视频链接 / UP 主页 / 合集链接 / 系列链接 / 课堂链接", "提示");
                return;
            }

            Items.Clear();
            IsBusy = true;
            Summary = "解析中...";
            AppendLog("---- 解析：" + input + " ----");
            Url = input;

            try
            {
                List<DownloadItem> items = await Task.Run(delegate { return _bili.Build(t, Log); });

                Items.Clear();
                for (int i = 0; i < items.Count; i++)
                    Items.Add(new ItemViewModel(items[i], i + 1));

                int sel = 0;
                for (int i = 0; i < Items.Count; i++) if (Items[i].IsSelected) sel++;
                Summary = "共 " + items.Count + " 项，已勾选 " + sel + " 项";
                AppendLog("解析完成：共 " + items.Count + " 项");

                QualityProbe q = await Task.Run(delegate { return TryProbe(items); });
                if (q != null)
                {
                    Summary = "共 " + items.Count + " 项   当前账号最高可用：" + q.Label;
                    AppendLog("清晰度探测（以第 1 项为准）：" + q.Label + "  音频 " + q.AudioCodecs);
                }
            }
            catch (Exception ex)
            {
                AppendLog("解析失败：" + ex.Message);
                Summary = "解析失败";
            }
            finally { IsBusy = false; }
        }

        /// <summary>探测当前账号能拿到的最好清晰度；失败只记日志，不影响下载。</summary>
        private QualityProbe TryProbe(List<DownloadItem> items)
        {
            if (items.Count == 0) return null;
            try { return _bili.ProbeQuality(items[0], Quality.CapForIndex(QualityIndex)); }
            catch (Exception ex)
            {
                AppendLog("清晰度探测失败（不影响下载）：" + ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------ 下载
        [RelayCommand(CanExecute = nameof(CanWork))]
        public async Task DownloadAsync()
        {
            if (Items.Count == 0)
            {
                _dialogs.Info("请先点「解析」，确认列表后再下载。", "提示");
                return;
            }
            if (string.IsNullOrEmpty(_cfg.FfmpegPath))
            {
                // 弹「工具路径」对话框，而不是只丢一句错误
                ShowToolsDialog(true);
                if (string.IsNullOrEmpty(_cfg.FfmpegPath))
                {
                    AppendLog("未指定 ffmpeg，已取消下载。");
                    return;
                }
            }

            List<DownloadItem> sel = new List<DownloadItem>();
            for (int i = 0; i < Items.Count; i++)
            {
                if (!Items[i].IsSelected) continue;
                Items[i].SyncSelection();
                sel.Add(Items[i].Item);
            }
            if (sel.Count == 0)
            {
                _dialogs.Info("一项都没勾选。", "提示");
                return;
            }

            DownloadSettings ds = new DownloadSettings();
            ds.OutDir = _config.Resolve(OutDir);
            OutDir = ds.OutDir;
            ds.QualityCap = Quality.CapForIndex(QualityIndex);
            ds.FfmpegPath = _cfg.FfmpegPath;
            ds.Aria2cPath = _cfg.Aria2cPath;
            ds.UseAria2c = UseAria2c && ds.Aria2cPath != null;

            try { Directory.CreateDirectory(ds.OutDir); }
            catch (Exception ex)
            {
                _dialogs.Info("输出目录无法创建：" + ex.Message, "错误");
                return;
            }

            _totalCount = sel.Count;
            _doneCount = 0;
            OverallPercent = 0;
            IsBusy = true;
            CanStop = true;
            AppendLog("---- 开始下载 " + sel.Count + " 项 ----");

            _cts = new CancellationTokenSource();
            try
            {
                DownloadBatchResult r = await _download.RunAsync(sel, ds, this, _cts.Token);
                if (r.DrmProtected)
                {
                    Summary = "已停止：该课程媒体受 DRM 加密";
                    AppendLog("---- 已停止：媒体是 CENC 加密的（encv / enca），本工具无法解密 ----");
                    AppendLog("    这不是网络或程序问题：ffmpeg 没有内容解密密钥，换地址、重试、清缓存都无效。");
                    AppendLog("    加密分片已自动清理，不会占用磁盘。");
                }
                else
                {
                    Summary = "下载结束：成功 " + r.Ok + " / 失败 " + r.Fail + (r.Cancelled ? "（已停止）" : "");
                    AppendLog("---- 下载结束：成功 " + r.Ok + " / 失败 " + r.Fail + " ----");
                }
            }
            catch (Exception ex)
            {
                AppendLog("下载异常：" + ex.Message);
                Summary = "下载异常";
            }
            finally
            {
                ResetTransfer();
                IsBusy = false;
                CanStop = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
            }
        }

        private bool CanStopWork() { return CanStop; }

        [RelayCommand(CanExecute = nameof(CanStopWork))]
        private void Stop()
        {
            CanStop = false;
            CancellationTokenSource c = _cts;
            if (c != null) { try { c.Cancel(); } catch { } }
            AppendLog("正在停止...");
        }

        private void ResetTransfer()
        {
            TransferText = "";
            SpeedText = "";
            FilePercent = 0;
            FileIndeterminate = false;
        }

        // ------------------------------------------------- 下载服务回调（后台线程）
        public void ItemStarting(int ordinal, int total, DownloadItem item)
        {
            OnUi(delegate
            {
                _curIndex = ordinal;
                _curTotal = total;
                _curTitle = item == null ? "" : item.Title;
                ResetTransfer();
            });
        }

        public void ItemFinished(DownloadItem item, bool success)
        {
            OnUi(delegate
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (!object.ReferenceEquals(Items[i].Item, item)) continue;
                    Items[i].Status = success ? "完成" : "失败";
                    break;
                }
                _doneCount++;
                OverallPercent = _totalCount > 0 ? _doneCount * 100.0 / _totalCount : 0;
            });
        }

        public void Transferred(TransferProgress p)
        {
            if (p == null) return;
            OnUi(delegate
            {
                string head = _curTotal > 0
                    ? "第 " + _curIndex + "/" + _curTotal + " 项 · " + _curTitle + " · " + p.Stage
                    : p.Stage;
                string size = p.SizeText();
                TransferText = size.Length > 0 ? head + "    " + size : head;
                SpeedText = p.SpeedText();

                int pct = p.Percent;
                if (pct < 0)
                {
                    // 服务器没给总长：滚动条，而不是假装 0%
                    FileIndeterminate = true;
                }
                else
                {
                    FileIndeterminate = false;
                    FilePercent = pct > 100 ? 100 : pct;
                }
            });
        }

        public void Log(string line)
        {
            AppendLog(line);
        }

        /// <summary>
        /// 只给 cli ui 用：填一行示例传输数据，让界面回归截图能看到这一行的排布。
        /// </summary>
        public void FillDemoTransfer()
        {
            _curIndex = 3;
            _curTotal = 16;
            _curTitle = "[P03]白平衡与色调：一步定冷暖";
            TransferProgress p = new TransferProgress();
            p.Stage = "视频";
            p.Received = 47400000;
            p.Total = 95600000;
            p.BytesPerSecond = 12.6 * 1024 * 1024;
            Transferred(p);
            Summary = "共 16 项，已勾选 16 项";
        }
    }
}
