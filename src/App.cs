// WinForms front end: login (in-app QR), output folder, address box,
// "解析/fetch" preview list with checkboxes, and the download runner.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace BiliGetter
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal static class Tools
    {
        public static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        /// <summary>
        /// Finds a bundled helper executable (ffmpeg / aria2c).
        ///
        /// The search is deliberately two-pass so that the *nearest* plain copy
        /// always wins:
        ///   1. a bare "ffmpeg.exe", starting beside the exe and walking up to 6
        ///      parents (6 covers Visual Studio's bin\Release\net48\ layout);
        ///   2. only if that fails, the historical Source\-style layouts
        ///      (Source\ffmpeg\, ..\Source\ffmpeg\, ..\..\Source\ffmpeg\);
        ///   3. finally PATH.
        ///
        /// Doing the Source\ fallbacks first would make a Visual Studio build pick
        /// up some unrelated ffmpeg higher up the tree instead of the copy shipped
        /// in this portable folder.
        /// </summary>
        public static string Find(string exeName, string[] relative)
        {
            string baseDir = BaseDir;

            // Pass 1: the plain file name, nearest directory first.
            DirectoryInfo dir = new DirectoryInfo(baseDir);
            for (int up = 0; up < 6 && dir != null; up++)
            {
                string p = Path.Combine(dir.FullName, exeName);
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }

            // Pass 2: the legacy Source\... layouts.
            dir = new DirectoryInfo(baseDir);
            for (int up = 0; up < 6 && dir != null; up++)
            {
                for (int i = 0; i < relative.Length; i++)
                {
                    string p = Path.Combine(dir.FullName, relative[i]);
                    if (File.Exists(p)) return p;
                }
                dir = dir.Parent;
            }

            // Pass 3: PATH.
            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                string[] parts = pathVar.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Trim().Length == 0) continue;
                    try
                    {
                        string p = Path.Combine(parts[i].Trim(), exeName);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            return null;
        }

        public static string FindFfmpeg()
        {
            return Find("ffmpeg.exe", new string[] {
                @"Source\ffmpeg\ffmpeg.exe", @"..\Source\ffmpeg\ffmpeg.exe", @"..\..\Source\ffmpeg\ffmpeg.exe"
            });
        }

        public static string FindAria2c()
        {
            return Find("aria2c.exe", new string[] {
                @"Source\aria2c.exe", @"..\Source\aria2c.exe", @"..\..\Source\aria2c.exe"
            });
        }

        public static Bitmap RenderQr(QrCode qr, int moduleSize, int quietZone)
        {
            int n = qr.ModuleCount;
            int size = (n + quietZone * 2) * moduleSize;
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using (SolidBrush b = new SolidBrush(Color.Black))
                {
                    for (int r = 0; r < n; r++)
                        for (int c = 0; c < n; c++)
                            if (qr.IsDark(r, c))
                                g.FillRectangle(b, (c + quietZone) * moduleSize, (r + quietZone) * moduleSize, moduleSize, moduleSize);
                }
            }
            bmp.Tag = null;
            return bmp;
        }
    }

    // ------------------------------------------------------------------- login
    internal sealed class LoginForm : Form
    {
        private readonly BiliSession _s;
        private readonly PictureBox _qr;
        private readonly Label _status;
        private readonly Button _browser;
        private readonly Button _copy;
        private string _url = "";

        public LoginForm(BiliSession s)
        {
            _s = s;
            Text = "登录 B 站（手机扫码）";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 500);
            BackColor = Color.White;

            _qr = new PictureBox();
            _qr.Location = new Point(32, 16);
            _qr.Size = new Size(336, 336);
            _qr.SizeMode = PictureBoxSizeMode.Zoom;
            _qr.BackColor = Color.White;
            Controls.Add(_qr);

            _status = new Label();
            _status.Location = new Point(20, 362);
            _status.Size = new Size(360, 40);
            _status.TextAlign = ContentAlignment.MiddleCenter;
            _status.Text = "正在获取二维码...";
            Controls.Add(_status);

            _browser = new Button();
            _browser.Text = "在浏览器中打开";
            _browser.Location = new Point(32, 412);
            _browser.Size = new Size(150, 30);
            _browser.Enabled = false;
            _browser.Click += delegate { OpenUrl(_url); };
            Controls.Add(_browser);

            _copy = new Button();
            _copy.Text = "复制链接";
            _copy.Location = new Point(196, 412);
            _copy.Size = new Size(110, 30);
            _copy.Enabled = false;
            _copy.Click += delegate
            {
                try { if (_url.Length > 0) Clipboard.SetText(_url); } catch { }
            };
            Controls.Add(_copy);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(316, 412);
            cancel.Size = new Size(52, 30);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            Load += LoginForm_Load;
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void Ui(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        private void SetStatus(string s) { Ui(delegate { _status.Text = s; }); }

        private void LoginForm_Load(object sender, EventArgs e)
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    Dictionary<string, object> g = _s.QrGenerate();
                    _url = Json.Str(g, "data", "url");
                    string key = Json.Str(g, "data", "qrcode_key");
                    if (string.IsNullOrEmpty(_url) || string.IsNullOrEmpty(key))
                    {
                        SetStatus("获取二维码失败");
                        return;
                    }
                    QrCode qr = QrCode.Encode(_url, QrTables.LevelM);
                    Bitmap bmp = Tools.RenderQr(qr, 6, 4);
                    Ui(delegate
                    {
                        _qr.Image = bmp;
                        _browser.Enabled = true;
                        _copy.Enabled = true;
                    });
                    SetStatus("用「哔哩哔哩」App 扫一扫");

                    DateTime deadline = DateTime.Now.AddMinutes(3);
                    while (DateTime.Now < deadline)
                    {
                        Thread.Sleep(2000);
                        Dictionary<string, object> p;
                        try { p = _s.QrPoll(key); }
                        catch { continue; }
                        long code = Json.Num(p, -1, "data", "code");
                        if (code == 0)
                        {
                            _s.Init();
                            SetStatus("登录成功：" + _s.UName);
                            Thread.Sleep(400);
                            Ui(delegate { DialogResult = DialogResult.OK; Close(); });
                            return;
                        }
                        if (code == 86090) SetStatus("已扫码，请在手机上点「确认登录」");
                        else if (code == 86038) { SetStatus("二维码已失效，请重新打开本窗口"); return; }
                    }
                    SetStatus("等待超时，请重新打开本窗口");
                }
                catch (Exception ex) { SetStatus("出错：" + ex.Message); }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    // -------------------------------------------------------------------- main
    internal sealed class MainForm : Form
    {
        private readonly BiliSession _session = new BiliSession();
        private readonly DownloadSettings _settings = new DownloadSettings();
        private readonly string _cookieFile;
        private readonly string _configFile;

        private Label _lblAccount;
        private Button _btnLogin, _btnLogout;
        private TextBox _txtUrl;
        private Button _btnFetch, _btnPaste;
        private TextBox _txtOut;
        private Button _btnBrowse, _btnOpen;
        private ComboBox _cmbQuality;
        private CheckBox _chkAria2c;
        private Label _lblTools;
        private ListView _list;
        private Button _btnAll, _btnNone, _btnDownload, _btnStop;
        private ProgressBar _bar;
        private Label _lblSummary;
        private TextBox _txtLog;

        private volatile bool _busy;
        private volatile bool _stop;
        private Downloader _downloader;
        private List<DownloadItem> _items = new List<DownloadItem>();

        public MainForm()
        {
            _cookieFile = Path.Combine(Tools.BaseDir, "bili-cookies.txt");
            _configFile = Path.Combine(Tools.BaseDir, "bili-getter.ini");
            BuildUi();
            _settings.FfmpegPath = Tools.FindFfmpeg();
            _settings.Aria2cPath = Tools.FindAria2c();
            LoadConfig();

            string tools = "ffmpeg: " + (_settings.FfmpegPath == null ? "未找到！" : _settings.FfmpegPath);
            tools += "    aria2c: " + (_settings.Aria2cPath == null ? "未找到（将单线程下载）" : _settings.Aria2cPath);
            _lblTools.Text = tools;
            _chkAria2c.Enabled = _settings.Aria2cPath != null;
            if (_settings.Aria2cPath == null) _chkAria2c.Checked = false;

            Shown += delegate { RefreshAccount(); };
            FormClosing += delegate { SaveConfig(); };
        }

        // ------------------------------------------------------------------- ui
        private void BuildUi()
        {
            Text = "B 站下载器";
            ClientSize = new Size(1000, 720);
            MinimumSize = new Size(860, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
            Controls.Add(root);

            // ---- top ----
            Panel top = new Panel();
            top.Dock = DockStyle.Fill;
            root.Controls.Add(top, 0, 0);

            _lblAccount = new Label();
            _lblAccount.Text = "账号：未登录";
            _lblAccount.Location = new Point(12, 12);
            _lblAccount.Size = new Size(520, 22);
            top.Controls.Add(_lblAccount);

            _btnLogin = new Button();
            _btnLogin.Text = "登录 (扫码)";
            _btnLogin.Location = new Point(12, 40);
            _btnLogin.Size = new Size(110, 28);
            _btnLogin.Click += delegate { DoLogin(); };
            top.Controls.Add(_btnLogin);

            _btnLogout = new Button();
            _btnLogout.Text = "退出登录";
            _btnLogout.Location = new Point(130, 40);
            _btnLogout.Size = new Size(90, 28);
            _btnLogout.Click += delegate { DoLogout(); };
            top.Controls.Add(_btnLogout);

            Label l1 = new Label();
            l1.Text = "地址：";
            l1.Location = new Point(12, 82);
            l1.Size = new Size(44, 22);
            top.Controls.Add(l1);

            _txtUrl = new TextBox();
            _txtUrl.Location = new Point(58, 79);
            _txtUrl.Size = new Size(640, 24);
            _txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            top.Controls.Add(_txtUrl);

            _btnFetch = new Button();
            _btnFetch.Text = "解析";
            _btnFetch.Location = new Point(706, 78);
            _btnFetch.Size = new Size(90, 26);
            _btnFetch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnFetch.Click += delegate { DoFetch(); };
            top.Controls.Add(_btnFetch);

            _btnPaste = new Button();
            _btnPaste.Text = "粘贴";
            _btnPaste.Location = new Point(802, 78);
            _btnPaste.Size = new Size(90, 26);
            _btnPaste.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnPaste.Click += delegate
            {
                try { if (Clipboard.ContainsText()) _txtUrl.Text = Clipboard.GetText().Trim(); } catch { }
            };
            top.Controls.Add(_btnPaste);

            Label l2 = new Label();
            l2.Text = "输出：";
            l2.Location = new Point(12, 114);
            l2.Size = new Size(44, 22);
            top.Controls.Add(l2);

            _txtOut = new TextBox();
            _txtOut.Location = new Point(58, 111);
            _txtOut.Size = new Size(640, 24);
            _txtOut.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            top.Controls.Add(_txtOut);

            _btnBrowse = new Button();
            _btnBrowse.Text = "浏览...";
            _btnBrowse.Location = new Point(706, 110);
            _btnBrowse.Size = new Size(90, 26);
            _btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnBrowse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择下载保存目录";
                    if (Directory.Exists(_txtOut.Text)) d.SelectedPath = _txtOut.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _txtOut.Text = d.SelectedPath;
                }
            };
            top.Controls.Add(_btnBrowse);

            _btnOpen = new Button();
            _btnOpen.Text = "打开目录";
            _btnOpen.Location = new Point(802, 110);
            _btnOpen.Size = new Size(90, 26);
            _btnOpen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnOpen.Click += delegate
            {
                try
                {
                    string d = _txtOut.Text;
                    if (Directory.Exists(d)) Process.Start(new ProcessStartInfo(d) { UseShellExecute = true });
                    else MessageBox.Show(this, "目录还不存在。", "提示");
                }
                catch { }
            };
            top.Controls.Add(_btnOpen);

            // ---- middle: options + list ----
            Panel mid = new Panel();
            mid.Dock = DockStyle.Fill;
            root.Controls.Add(mid, 0, 1);

            // Explicit 2-row table: avoids relying on WinForms docking z-order.
            TableLayoutPanel midT = new TableLayoutPanel();
            midT.Dock = DockStyle.Fill;
            midT.ColumnCount = 1;
            midT.RowCount = 2;
            midT.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            midT.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mid.Controls.Add(midT);

            Panel opt = new Panel();
            opt.Dock = DockStyle.Fill;
            midT.Controls.Add(opt, 0, 0);

            Label l3 = new Label();
            l3.Text = "清晰度：";
            l3.Location = new Point(6, 6);
            l3.Size = new Size(58, 22);
            opt.Controls.Add(l3);

            _cmbQuality = new ComboBox();
            _cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbQuality.Location = new Point(66, 3);
            _cmbQuality.Size = new Size(120, 24);
            _cmbQuality.Items.AddRange(Quality.Names);
            _cmbQuality.SelectedIndex = 0;
            opt.Controls.Add(_cmbQuality);

            Label l4 = new Label();
            l4.Text = "（按当前账号权限自动取最高）";
            l4.Location = new Point(194, 6);
            l4.Size = new Size(220, 22);
            l4.ForeColor = Color.Gray;
            opt.Controls.Add(l4);

            _chkAria2c = new CheckBox();
            _chkAria2c.Text = "用 aria2c 多线程加速";
            _chkAria2c.Location = new Point(410, 4);
            _chkAria2c.Size = new Size(180, 24);
            _chkAria2c.Checked = true;
            opt.Controls.Add(_chkAria2c);

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.HideSelection = false;
            _list.Columns.Add("#", 46);
            _list.Columns.Add("标题", 380);
            _list.Columns.Add("分类", 200);
            _list.Columns.Add("时长", 70);
            _list.Columns.Add("清晰度", 150);
            _list.Columns.Add("状态", 110);
            midT.Controls.Add(_list, 0, 1);

            // ---- bottom ----
            Panel bot = new Panel();
            bot.Dock = DockStyle.Fill;
            root.Controls.Add(bot, 0, 2);

            _btnAll = new Button();
            _btnAll.Text = "全选";
            _btnAll.Location = new Point(12, 8);
            _btnAll.Size = new Size(70, 26);
            _btnAll.Click += delegate { SetAllChecked(true); };
            bot.Controls.Add(_btnAll);

            _btnNone = new Button();
            _btnNone.Text = "全不选";
            _btnNone.Location = new Point(88, 8);
            _btnNone.Size = new Size(70, 26);
            _btnNone.Click += delegate { SetAllChecked(false); };
            bot.Controls.Add(_btnNone);

            _btnDownload = new Button();
            _btnDownload.Text = "开始下载";
            _btnDownload.Location = new Point(178, 8);
            _btnDownload.Size = new Size(110, 26);
            _btnDownload.Click += delegate { DoDownload(); };
            bot.Controls.Add(_btnDownload);

            _btnStop = new Button();
            _btnStop.Text = "停止";
            _btnStop.Location = new Point(296, 8);
            _btnStop.Size = new Size(70, 26);
            _btnStop.Enabled = false;
            _btnStop.Click += delegate
            {
                _stop = true;
                if (_downloader != null) _downloader.Cancel();
                Log("正在停止...");
            };
            bot.Controls.Add(_btnStop);

            _bar = new ProgressBar();
            _bar.Location = new Point(12, 42);
            _bar.Size = new Size(600, 18);
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            bot.Controls.Add(_bar);

            _lblSummary = new Label();
            _lblSummary.Text = "未解析";
            _lblSummary.Location = new Point(624, 44);
            _lblSummary.Size = new Size(360, 20);
            _lblSummary.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bot.Controls.Add(_lblSummary);

            _lblTools = new Label();
            _lblTools.Location = new Point(12, 66);
            _lblTools.Size = new Size(972, 20);
            _lblTools.ForeColor = Color.DimGray;
            _lblTools.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblTools.AutoEllipsis = true;
            bot.Controls.Add(_lblTools);

            _txtLog = new TextBox();
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Location = new Point(12, 90);
            _txtLog.Size = new Size(972, 120);
            _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _txtLog.BackColor = Color.White;
            bot.Controls.Add(_txtLog);
        }

        private void SetAllChecked(bool v)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.Items[i].Checked = v;
        }

        // ------------------------------------------------------------------- log
        private void Log(string s)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Log), s); } catch { }
                return;
            }
            _txtLog.AppendText(s + "\r\n");
        }

        private void SetSummary(string s)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(SetSummary), s); } catch { } return; }
            _lblSummary.Text = s;
        }

        // --------------------------------------------------------------- account
        private void RefreshAccount()
        {
            try
            {
                if (File.Exists(_cookieFile)) _session.LoadCookies(File.ReadAllText(_cookieFile, System.Text.Encoding.UTF8));
            }
            catch { }
            try { _session.Init(); }
            catch (Exception ex) { Log("初始化会话失败：" + ex.Message); }
            UpdateAccountLabel();
        }

        private void UpdateAccountLabel()
        {
            if (_session.IsLogin)
                _lblAccount.Text = "账号：" + _session.UName + "  (mid=" + _session.Mid + ")  已登录";
            else
                _lblAccount.Text = "账号：未登录  —— 只能下到 480P，登录后可到 1080P+，大会员可到 4K/8K";
        }

        private void DoLogin()
        {
            using (LoginForm f = new LoginForm(_session))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
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
        }

        private void DoLogout()
        {
            try { if (File.Exists(_cookieFile)) File.Delete(_cookieFile); } catch { }
            Log("已删除本地保存的登录信息（浏览器里的登录不受影响）");
            MessageBox.Show(this, "已退出登录。重开程序生效。", "提示");
        }

        // ----------------------------------------------------------------- fetch
        private void DoFetch()
        {
            if (_busy) return;
            string input = _txtUrl.Text.Trim();
            if (input.Length == 0) { MessageBox.Show(this, "请先填写视频 / UP 主页 / 合集 / 课程地址。", "提示"); return; }
            if (_txtOut.Text.Trim().Length == 0)
                _txtOut.Text = Path.Combine(Tools.BaseDir, "downloads");
            _settings.OutDir = _txtOut.Text.Trim();
            _settings.QualityCap = Quality.CapForIndex(_cmbQuality.SelectedIndex);
            _settings.UseAria2c = _chkAria2c.Checked;

            Target t = TargetResolver.Resolve(input);
            if (t.Kind == TargetKind.Unknown)
            {
                MessageBox.Show(this, "无法识别的地址。\r\n支持：BV号 / 视频链接 / UP 主页 / 合集链接 / 系列链接 / 课堂链接", "提示");
                return;
            }

            _list.Items.Clear();
            _items = new List<DownloadItem>();
            _busy = true;
            _btnFetch.Enabled = false;
            SetSummary("解析中...");
            Log("---- 解析：" + input + " ----");
            _txtUrl.Text = input;

            Thread th = new Thread(delegate ()
            {
                try
                {
                    Fetcher f = new Fetcher(_session);
                    List<DownloadItem> items = f.Build(t, Log);
                    _items = items;
                    Populate(items);
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
                    try { BeginInvoke(new Action(delegate { _btnFetch.Enabled = true; })); } catch { }
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void Populate(List<DownloadItem> items)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action<List<DownloadItem>>(Populate), items); } catch { } return; }
            _list.BeginUpdate();
            _list.Items.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                DownloadItem it = items[i];
                ListViewItem lvi = new ListViewItem(new string[] {
                    (i + 1).ToString(), it.Title, it.Group, it.Duration, "", it.Warn
                });
                lvi.Checked = it.Selected;
                lvi.Tag = it;
                _list.Items.Add(lvi);
            }
            _list.EndUpdate();
        }

        /// Probes the first item so the user can see the best quality their
        /// account currently allows, before committing to a download.
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
                var data = Json.Member(play, "data") as Dictionary<string, object>;
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

        // -------------------------------------------------------------- download
        private void DoDownload()
        {
            if (_busy) return;
            if (_items.Count == 0) { MessageBox.Show(this, "请先点「解析」，确认列表后再下载。", "提示"); return; }
            if (string.IsNullOrEmpty(_settings.FfmpegPath))
            {
                MessageBox.Show(this, "找不到 ffmpeg.exe，无法合并音视频。\r\n请把 ffmpeg.exe 放到本程序目录，或保持它在 Source\\ffmpeg\\ 下。", "缺少 ffmpeg");
                return;
            }
            var sel = new List<DownloadItem>();
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (!_list.Items[i].Checked) continue;
                DownloadItem it = _list.Items[i].Tag as DownloadItem;
                if (it != null) { it.Selected = true; sel.Add(it); }
            }
            if (sel.Count == 0) { MessageBox.Show(this, "一项都没勾选。", "提示"); return; }

            _settings.OutDir = _txtOut.Text.Trim();
            _settings.QualityCap = Quality.CapForIndex(_cmbQuality.SelectedIndex);
            _settings.UseAria2c = _chkAria2c.Checked && _settings.Aria2cPath != null;
            try { Directory.CreateDirectory(_settings.OutDir); }
            catch (Exception ex) { MessageBox.Show(this, "输出目录无法创建：" + ex.Message, "错误"); return; }

            _busy = true;
            _stop = false;
            _btnDownload.Enabled = false;
            _btnFetch.Enabled = false;
            _btnStop.Enabled = true;
            _bar.Value = 0;
            _bar.Maximum = sel.Count;
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
                        int idx = i;
                        try
                        {
                            bool done = _downloader.DownloadOne(_session, it, delegate (int pct) { });
                            if (done) ok++; else fail++;
                            SetStatus2(it, done ? "完成" : "失败", it.QualityLabel);
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            Log("  ✗ " + it.Title + "：" + ex.Message);
                            SetStatus2(it, "失败", "");
                        }
                        int v = i + 1;
                        try { BeginInvoke(new Action(delegate { if (v <= _bar.Maximum) _bar.Value = v; })); } catch { }
                    }
                }
                finally
                {
                    _busy = false;
                    _stop = false;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            _btnDownload.Enabled = true;
                            _btnFetch.Enabled = true;
                            _btnStop.Enabled = false;
                            SetSummary("下载结束：成功 " + ok + " / 失败 " + fail);
                        }));
                    }
                    catch { }
                    Log("---- 下载结束：成功 " + ok + " / 失败 " + fail + " ----");
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void SetStatus2(DownloadItem it, string status, string quality)
        {
            if (InvokeRequired) { try { BeginInvoke(new Action<DownloadItem, string, string>(SetStatus2), it, status, quality); } catch { } return; }
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (object.ReferenceEquals(_list.Items[i].Tag, it))
                {
                    _list.Items[i].SubItems[4].Text = quality;
                    _list.Items[i].SubItems[5].Text = status;
                    break;
                }
            }
        }

        // ----------------------------------------------------------------- config
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
                    }
                }
            }
            catch { }
            _txtOut.Text = outDir;
            if (quality >= 0 && quality < _cmbQuality.Items.Count) _cmbQuality.SelectedIndex = quality;
            _chkAria2c.Checked = aria;
        }

        private void SaveConfig()
        {
            try
            {
                File.WriteAllText(_configFile,
                    "out=" + _txtOut.Text.Trim() + "\r\n"
                    + "quality=" + _cmbQuality.SelectedIndex + "\r\n"
                    + "aria2c=" + (_chkAria2c.Checked ? "1" : "0") + "\r\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
