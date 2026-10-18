// WPF 主窗口：账号、地址解析、输出目录、选项、项目列表、下载动作栏、日志。
// 业务逻辑一律走现有核心（BiliSession / Fetcher / Downloader / StreamPicker），
// 这一层只负责界面与线程编排。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;

namespace BiliGetter
{
    public partial class MainWindow : Window
    {
        private readonly BiliSession _session = new BiliSession();
        private readonly DownloadSettings _settings = new DownloadSettings();
        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        private readonly string _cookieFile;
        private readonly string _configFile;
        private readonly bool _renderOnly;

        private List<DownloadItem> _items = new List<DownloadItem>();
        private volatile bool _busy;
        private volatile bool _stop;
        private Downloader _downloader;

        /// <summary>清晰度下拉的选项，索引与 Quality.CapForIndex 一一对应。</summary>
        private static readonly string[] QualityNames = { "最高可用", "1080P", "720P", "480P", "360P" };

        public MainWindow() : this(false) { }

        /// <param name="renderOnly">
        /// true 时不做启动动作（不读 Cookie、不做工具检查、不弹对话框）。
        /// 供命令行 `ui` 渲染界面截图用，避免后台网络请求和模态框阻塞。
        /// </param>
        public MainWindow(bool renderOnly)
        {
            _renderOnly = renderOnly;
            InitializeComponent();

            _cookieFile = Path.Combine(Tools.BaseDir, "bili-cookies.txt");
            _configFile = Path.Combine(Tools.BaseDir, "bili-getter.ini");

            CmbQuality.ItemsSource = QualityNames;
            CmbQuality.SelectedIndex = 0;
            Grid1.ItemsSource = _rows;

            LoadConfig();
            ApplyToolSettings();

            if (!_renderOnly)
            {
                Loaded += delegate { RefreshAccount(); EnsureTools(); };
            }
            Closing += delegate { SaveConfig(); };
        }

        /// <summary>把动作切回 UI 线程；已在 UI 线程就地执行。</summary>
        private void Ui(Action a)
        {
            if (Dispatcher.CheckAccess()) { a(); return; }
            Dispatcher.InvokeAsync(a);
        }

        private void Log(string s)
        {
            Ui(delegate
            {
                TxtLog.AppendText(s + "\r\n");
                TxtLog.ScrollToEnd();
            });
        }

        private void SetSummary(string s) { Ui(delegate { TxtSummary.Text = s; }); }

        // ---------------------------------------------------------------- 账号
        private void RefreshAccount()
        {
            try
            {
                if (File.Exists(_cookieFile))
                    _session.LoadCookies(File.ReadAllText(_cookieFile, System.Text.Encoding.UTF8));
            }
            catch { }
            try { _session.Init(); }
            catch (Exception ex) { Log("初始化会话失败：" + ex.Message); }
            UpdateAccountLabel();
        }

        private void UpdateAccountLabel()
        {
            TxtAccount.Text = _session.IsLogin
                ? "账号：" + _session.UName + "  (mid=" + _session.Mid + ")  已登录"
                : "账号：未登录 —— 只能下到 480P，登录后可到 1080P+，大会员可到 4K/8K";
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            LoginWindow w = new LoginWindow(_session);
            w.Owner = this;
            if (w.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(_cookieFile, _session.DumpCookies(), new System.Text.UTF8Encoding(false));
                    Log("已保存登录信息到 " + _cookieFile);
                }
                catch (Exception ex) { Log("保存登录信息失败：" + ex.Message); }
                UpdateAccountLabel();
            }
        }

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            try { if (File.Exists(_cookieFile)) File.Delete(_cookieFile); } catch { }
            Log("已删除本地保存的登录信息（浏览器里的登录不受影响）");
            MessageBox.Show(this, "已退出登录。重开程序生效。", "提示");
        }

        // ---------------------------------------------------------------- 工具
        private void ApplyToolSettings()
        {
            if (string.IsNullOrEmpty(_settings.FfmpegPath) || !File.Exists(_settings.FfmpegPath))
                _settings.FfmpegPath = Tools.FindFfmpeg();
            if (string.IsNullOrEmpty(_settings.Aria2cPath) || !File.Exists(_settings.Aria2cPath))
                _settings.Aria2cPath = Tools.FindAria2c();

            bool hasFfmpeg = !string.IsNullOrEmpty(_settings.FfmpegPath);
            bool hasAria2c = !string.IsNullOrEmpty(_settings.Aria2cPath);

            string text = "ffmpeg: " + (hasFfmpeg ? _settings.FfmpegPath : "未找到（必需）")
                        + "   ·   aria2c: " + (hasAria2c ? _settings.Aria2cPath : "未找到（可选）");

            // 工具放在程序目录之外时，可能被「受控文件夹访问」挡住而写不进任何目录。
            // 只提醒，不阻止使用。
            if ((hasFfmpeg && !InBaseDir(_settings.FfmpegPath)) || (hasAria2c && !InBaseDir(_settings.Aria2cPath)))
                text += "   ⚠ 不在程序目录：若报 Access is denied，请把工具移回程序目录";

            TxtTools.Text = text;
            TxtTools.ToolTip = text;
            TxtTools.Foreground = hasFfmpeg
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A))
                : System.Windows.Media.Brushes.Firebrick;

            ChkAria2c.IsEnabled = hasAria2c;
            if (!hasAria2c) ChkAria2c.IsChecked = false;
        }

        /// <summary>判断某个 exe 是否就在程序目录（含子目录）里。</summary>
        private static bool InBaseDir(string path)
        {
            try
            {
                string root = Path.GetFullPath(Tools.BaseDir).TrimEnd('\\') + "\\";
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }   // 判断不出来就别打扰用户
        }

        private void EnsureTools()
        {
            if (!string.IsNullOrEmpty(_settings.FfmpegPath) && !string.IsNullOrEmpty(_settings.Aria2cPath)) return;
            ShowToolsDialog(true);
        }

        private void BtnTools_Click(object sender, RoutedEventArgs e) { ShowToolsDialog(false); }

        private void ShowToolsDialog(bool startup)
        {
            ToolsWindow w = new ToolsWindow(_settings.FfmpegPath, _settings.Aria2cPath, startup);
            w.Owner = this;
            if (w.ShowDialog() != true) return;
            _settings.FfmpegPath = w.FfmpegPath;
            _settings.Aria2cPath = w.Aria2cPath;
            ApplyToolSettings();
            SaveConfig();
            Log("工具路径已更新：");
            Log("  ffmpeg = " + (_settings.FfmpegPath ?? "(未指定)"));
            Log("  aria2c = " + (_settings.Aria2cPath ?? "(未指定)"));
        }

        // ---------------------------------------------------------------- 解析
        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText()) TxtUrl.Text = Clipboard.GetText().Trim();
            }
            catch { }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFolderDialog dlg = new Microsoft.Win32.OpenFolderDialog();
            dlg.Title = "选择下载目录";
            dlg.Multiselect = false;
            try { if (Directory.Exists(TxtOut.Text.Trim())) dlg.InitialDirectory = TxtOut.Text.Trim(); }
            catch { }
            if (dlg.ShowDialog(this) == true) TxtOut.Text = dlg.FolderName;
        }

        private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string p = TxtOut.Text.Trim();
                Directory.CreateDirectory(p);
                Tools.OpenUrl(p);
            }
            catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
        }

        private void BtnFetch_Click(object sender, RoutedEventArgs e) { DoFetch(); }

        private void DoFetch()
        {
            if (_busy) return;
            string input = TxtUrl.Text.Trim();
            if (input.Length == 0)
            {
                MessageBox.Show(this, "请先填写视频 / UP 主页 / 合集 / 课程地址。", "提示");
                return;
            }
            if (TxtOut.Text.Trim().Length == 0)
                TxtOut.Text = Path.Combine(Tools.BaseDir, "downloads");

            _settings.OutDir = TxtOut.Text.Trim();
            _settings.QualityCap = Quality.CapForIndex(CmbQuality.SelectedIndex);
            _settings.UseAria2c = ChkAria2c.IsChecked == true;

            Target t = TargetResolver.Resolve(input);
            if (t.Kind == TargetKind.Unknown)
            {
                MessageBox.Show(this,
                    "无法识别的地址。\r\n支持：BV号 / 视频链接 / UP 主页 / 合集链接 / 系列链接 / 课堂链接", "提示");
                return;
            }

            _rows.Clear();
            _items = new List<DownloadItem>();
            _busy = true;
            BtnFetch.IsEnabled = false;
            SetSummary("解析中...");
            Log("---- 解析：" + input + " ----");

            Thread th = new Thread(delegate ()
            {
                try
                {
                    Fetcher f = new Fetcher(_session);
                    List<DownloadItem> items = f.Build(t, Log);
                    _items = items;
                    Ui(delegate { Populate(items); });
                    ProbeQuality(items);

                    int sel = 0;
                    for (int i = 0; i < items.Count; i++) if (items[i].Selected) sel++;
                    SetSummary("共 " + items.Count + " 项，已勾选 " + sel + " 项");
                    Log("解析完成：共 " + items.Count + " 项");
                }
                catch (Exception ex)
                {
                    Log("解析失败：" + ex.Message);
                    SetSummary("解析失败");
                }
                finally
                {
                    _busy = false;
                    Ui(delegate { BtnFetch.IsEnabled = true; });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void Populate(List<DownloadItem> items)
        {
            _rows.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                Row row = new Row();
                row.Index = i + 1;
                row.Item = items[i];
                row.IsSelected = items[i].Selected;
                _rows.Add(row);
            }
        }

        /// <summary>
        /// 用第一项探一次播放地址，让用户在下载前就看到当前账号能拿到的最好清晰度。
        /// </summary>
        private void ProbeQuality(List<DownloadItem> items)
        {
            if (items.Count == 0) return;
            DownloadItem first = items[0];
            try
            {
                if (string.IsNullOrEmpty(first.Cid))
                {
                    Dictionary<string, object> info = _session.VideoInfo(first.Bvid);
                    first.Cid = Json.Str(info, "data", "cid");
                }
                Dictionary<string, object> play = first.Cheese
                    ? _session.CheesePlayUrl(first.Aid, first.Cid, first.EpId)
                    : _session.PlayUrl(first.Bvid, first.Cid, first.Aid);

                object dataObj = Json.Member(play, "data");
                Dictionary<string, object> data = dataObj as Dictionary<string, object>;
                if (data == null) return;

                PickedStreams p = StreamPicker.Pick(data, _settings.QualityCap);
                SetSummary("共 " + items.Count + " 项   当前账号最高可用：" + p.Label);
                Log("清晰度探测（以第 1 项为准）：" + p.Label + "  音频 " + p.AudioCodecs);
            }
            catch (Exception ex)
            {
                Log("清晰度探测失败（不影响下载）：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 下载
        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].IsSelected = true;
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].IsSelected = false;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _stop = true;
            Downloader d = _downloader;
            if (d != null) d.Cancel();
            Log("正在停止...");
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e) { DoDownload(); }

        private void DoDownload()
        {
            if (_busy) return;
            if (_items.Count == 0)
            {
                MessageBox.Show(this, "请先点「解析」，确认列表后再下载。", "提示");
                return;
            }
            if (string.IsNullOrEmpty(_settings.FfmpegPath))
            {
                // 直接弹「工具路径」对话框，而不是只丢一句错误
                ShowToolsDialog(true);
                if (string.IsNullOrEmpty(_settings.FfmpegPath))
                {
                    Log("未指定 ffmpeg，已取消下载。");
                    return;
                }
            }

            List<DownloadItem> sel = new List<DownloadItem>();
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!_rows[i].IsSelected) continue;
                _rows[i].Item.Selected = true;
                sel.Add(_rows[i].Item);
            }
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "一项都没勾选。", "提示");
                return;
            }

            _settings.OutDir = TxtOut.Text.Trim();
            _settings.QualityCap = Quality.CapForIndex(CmbQuality.SelectedIndex);
            _settings.UseAria2c = ChkAria2c.IsChecked == true && _settings.Aria2cPath != null;
            try { Directory.CreateDirectory(_settings.OutDir); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "输出目录无法创建：" + ex.Message, "错误");
                return;
            }

            _busy = true;
            _stop = false;
            BtnDownload.IsEnabled = false;
            BtnFetch.IsEnabled = false;
            BtnStop.IsEnabled = true;
            Bar.Value = 0;
            Bar.Maximum = sel.Count;
            Log("---- 开始下载 " + sel.Count + " 项 ----");

            Thread th = new Thread(delegate ()
            {
                int ok = 0, fail = 0;
                _downloader = new Downloader();
                _downloader.Settings = _settings;
                _downloader.Log = Log;
                try
                {
                    for (int i = 0; i < sel.Count; i++)
                    {
                        if (_stop) break;
                        DownloadItem it = sel[i];
                        try
                        {
                            bool done = _downloader.DownloadOne(_session, it, delegate (int pct) { });
                            if (done) ok++; else fail++;
                            SetRowState(it, done ? "完成" : "失败", it.QualityLabel);
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            Log("  ✗ " + it.Title + "：" + ex.Message);
                            SetRowState(it, "失败", "");
                        }
                        int v = i + 1;
                        Ui(delegate { if (v <= Bar.Maximum) Bar.Value = v; });
                    }
                }
                finally
                {
                    _busy = false;
                    _stop = false;
                    Ui(delegate
                    {
                        BtnDownload.IsEnabled = true;
                        BtnFetch.IsEnabled = true;
                        BtnStop.IsEnabled = false;
                        TxtSummary.Text = "下载结束：成功 " + ok + " / 失败 " + fail;
                    });
                    Log("---- 下载结束：成功 " + ok + " / 失败 " + fail + " ----");
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void SetRowState(DownloadItem it, string status, string quality)
        {
            Ui(delegate
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (!object.ReferenceEquals(_rows[i].Item, it)) continue;
                    _rows[i].Status = status;
                    if (!string.IsNullOrEmpty(quality)) _rows[i].Quality = quality;
                    break;
                }
            });
        }

        // ---------------------------------------------------------------- 配置
        private void LoadConfig()
        {
            string outDir = Path.Combine(Tools.BaseDir, "downloads");
            int quality = 0;
            bool aria = true;
            try
            {
                if (File.Exists(_configFile))
                {
                    foreach (string line in File.ReadAllLines(_configFile))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 1) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "out") outDir = v;
                        else if (k == "quality") { int q; if (int.TryParse(v, out q)) quality = q; }
                        else if (k == "aria2c") aria = v == "1";
                        else if (k == "ffmpeg_path") _settings.FfmpegPath = v.Length > 0 ? v : null;
                        else if (k == "aria2c_path") _settings.Aria2cPath = v.Length > 0 ? v : null;
                    }
                }
            }
            catch { }

            TxtOut.Text = outDir;
            if (quality >= 0 && quality < QualityNames.Length) CmbQuality.SelectedIndex = quality;
            ChkAria2c.IsChecked = aria;
        }

        private void SaveConfig()
        {
            try
            {
                File.WriteAllText(_configFile,
                    "out=" + TxtOut.Text.Trim() + "\r\n"
                    + "quality=" + CmbQuality.SelectedIndex + "\r\n"
                    + "aria2c=" + (ChkAria2c.IsChecked == true ? "1" : "0") + "\r\n"
                    + "ffmpeg_path=" + (_settings.FfmpegPath ?? "") + "\r\n"
                    + "aria2c_path=" + (_settings.Aria2cPath ?? "") + "\r\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>列表里的一行，供 DataGrid 绑定。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public DownloadItem Item { get; set; }

        public string Title { get { return Item == null ? "" : Item.Title; } }
        public string Group { get { return Item == null ? "" : Item.Group; } }
        public string Duration { get { return Item == null ? "" : Item.Duration; } }

        private bool _selected;
        public bool IsSelected
        {
            get { return _selected; }
            set { if (_selected == value) return; _selected = value; Raise("IsSelected"); }
        }

        private string _quality = "";
        public string Quality
        {
            get { return _quality; }
            set { if (_quality == value) return; _quality = value; Raise("Quality"); }
        }

        private string _status = "";
        public string Status
        {
            get { return _status; }
            set { if (_status == value) return; _status = value; Raise("Status"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
