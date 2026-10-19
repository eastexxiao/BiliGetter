// 命令行模式：由 Program.Main 在带 --cli 参数（或直接给出子命令）时调用。
// 与图形界面共用同一套核心代码（BiliApi / Fetcher / Downloader）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace BiliGetter
{
    internal static class Cli
    {
        /// <summary>支持的子命令（程序入口据此判断是否进入命令行模式）。</summary>
        public static readonly string[] Commands = new string[] {
            "tools", "list", "dl", "poll", "qr", "ui", "vmtest"
        };

        public static int Run(string[] args)
        {
            string cmd = args.Length > 0 ? args[0] : "help";
            // "ui" and "tools" need no network, so handle them first.
            if (cmd == "ui") return CmdUi(args.Length > 1 ? args[1] : "ui.png");
            if (cmd == "tools")
            {
                Console.WriteLine("exe dir  : " + Tools.BaseDir);
                Console.WriteLine("ffmpeg   : " + (Tools.FindFfmpeg() ?? "NOT FOUND"));
                Console.WriteLine("aria2c   : " + (Tools.FindAria2c() ?? "NOT FOUND"));
                return 0;
            }
            try
            {
                BiliSession s = new BiliSession();
                // Use the same saved login as the GUI, so the CLI can reach
                // member-only / charging-exclusive content too.
                string cookieFile = Path.Combine(Tools.BaseDir, "bili-cookies.txt");
                if (File.Exists(cookieFile))
                {
                    s.LoadCookies(File.ReadAllText(cookieFile, System.Text.Encoding.UTF8));
                    Console.WriteLine("[session] loaded cookies from " + cookieFile);
                }
                s.Init();
                Console.WriteLine("[session] login=" + s.IsLogin + "  wbi=" + (s.MixinKey != null) + "  user=" + s.UName);
                Console.WriteLine("[tools]   ffmpeg=" + (Tools.FindFfmpeg() ?? "NOT FOUND"));
                Console.WriteLine("[tools]   aria2c=" + (Tools.FindAria2c() ?? "NOT FOUND"));

                switch (cmd)
                {
                    case "qr": return CmdQr(s, args.Length > 1 ? args[1] : "login-qr.png");
                    case "list": return CmdList(s, args[1]);
                    case "dl": return CmdDl(s, args[1], args.Length > 2 ? int.Parse(args[2]) : 4, args.Length > 3 ? int.Parse(args[3]) : 1);
                    case "ui": return CmdUi(args.Length > 1 ? args[1] : "ui.png");
                    case "vmtest": return CmdVmTest(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
                    case "poll": return CmdPoll(s);
                    default:
                        Console.WriteLine("usage: qr <png> | poll | list <target> | dl <target> <capIndex> <maxItems>"
                            + " | ui <png> | vmtest <target> [maxItems]");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static int CmdQr(BiliSession s, string outPath)
        {
            Dictionary<string, object> g = s.QrGenerate();
            string url = Json.Str(g, "data", "url");
            string key = Json.Str(g, "data", "qrcode_key");
            Console.WriteLine("[qr] url len=" + (url == null ? 0 : url.Length) + "  key=" + (key == null ? "(none)" : key.Substring(0, Math.Min(8, key.Length)) + "..."));
            QrCode qr = QrCode.Encode(url, QrTables.LevelM);
            Console.WriteLine("[qr] version=v" + qr.TypeNumber + " modules=" + qr.ModuleCount);
            System.Windows.Media.Imaging.BitmapSource src = Tools.RenderQr(qr, 6, 4);
            Tools.SavePng(src, outPath);
            Console.WriteLine("[qr] wrote " + outPath + "  " + src.PixelWidth + "x" + src.PixelHeight);
            Console.WriteLine("[qr] scanning is up to you; the poll endpoint was verified separately.");
            return 0;
        }

        private static int CmdList(BiliSession s, string target)
        {
            Target t = TargetResolver.Resolve(target);
            Console.WriteLine("[target] kind=" + t.Kind + " mid=" + t.Mid + " sid=" + t.Sid + " bvid=" + t.Bvid);
            Fetcher f = new Fetcher(s);
            List<DownloadItem> items = f.Build(t, delegate (string m) { Console.WriteLine("  · " + m); });
            Console.WriteLine("[items] " + items.Count);
            int show = Math.Min(items.Count, 12);
            for (int i = 0; i < show; i++)
                Console.WriteLine("   " + (i + 1) + ". " + items[i].Title + "   [" + items[i].Group + "]  " + items[i].Duration);
            if (items.Count > show) Console.WriteLine("   ... (" + (items.Count - show) + " more)");

            // Same quality probe the GUI does, so `list` reports the best this
            // account can actually get before committing to a download.
            DownloadItem first = items[0];
            try
            {
                if (string.IsNullOrEmpty(first.Cid))
                    first.Cid = Json.Str(s.VideoInfo(first.Bvid), "data", "cid");
                Dictionary<string, object> play = first.Cheese
                    ? s.CheesePlayUrl(first.Aid, first.Cid, first.EpId)
                    : s.PlayUrl(first.Bvid, first.Cid, first.Aid);
                PickedStreams p = StreamPicker.Pick(Json.Member(play, "data") as Dictionary<string, object>, 0);
                Console.WriteLine("[quality] 当前账号最高可用：" + p.Label + "   音频 " + p.AudioCodecs);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[quality] 探测失败：" + ex.Message + Util.AccessHint(s, first));
            }
            return 0;
        }

        /// <summary>
        /// 读 bili-getter.ini，让 --cli 与图形界面共用同一份设置。
        ///
        /// 以前命令行版只做自动搜索，所以手工指定的工具路径、以及界面里存的
        /// 输出目录，对 --cli 都不起作用——两边行为不一致。
        /// 只接受「文件确实存在」的路径，避免一条失效配置把命令行也带坏。
        /// </summary>
        private static void ApplyIni(DownloadSettings ds)
        {
            try
            {
                string ini = Path.Combine(Tools.BaseDir, "bili-getter.ini");
                if (!File.Exists(ini)) return;
                foreach (string line in File.ReadAllLines(ini))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    // ini 里可能是相对路径：按程序目录解析，不要依赖当前工作目录
                    string full = Tools.Resolve(v);
                    if (k == "ffmpeg_path" && full.Length > 0 && File.Exists(full)) ds.FfmpegPath = full;
                    else if (k == "aria2c_path" && full.Length > 0 && File.Exists(full)) ds.Aria2cPath = full;
                    else if (k == "aria2c") ds.UseAria2c = (v == "1") && ds.Aria2cPath != null;
                }
            }
            catch { }
        }

        private static int CmdDl(BiliSession s, string target, int capIndex, int maxItems)
        {
            Target t = TargetResolver.Resolve(target);
            Fetcher f = new Fetcher(s);
            List<DownloadItem> items = f.Build(t, delegate (string m) { Console.WriteLine("  · " + m); });
            Console.WriteLine("[items] " + items.Count + "   capIndex=" + capIndex + "  downloading up to " + maxItems);
            if (items.Count == 0) return 1;

            DownloadSettings ds = new DownloadSettings();
            ds.OutDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cli-out");
            ds.FfmpegPath = Tools.FindFfmpeg();
            ds.Aria2cPath = Tools.FindAria2c();
            ds.UseAria2c = ds.Aria2cPath != null;
            ApplyIni(ds);
            ds.QualityCap = Quality.CapForIndex(capIndex);
            Console.WriteLine("[out]     " + ds.OutDir + "  useAria2c=" + ds.UseAria2c);
            Console.WriteLine("[tools]   ffmpeg=" + (ds.FfmpegPath ?? "NOT FOUND"));
            Console.WriteLine("[tools]   aria2c=" + (ds.Aria2cPath ?? "NOT FOUND"));

            Downloader d = new Downloader();
            d.Settings = ds;
            d.Log = delegate (string m) { Console.WriteLine("  " + m); };
            // 终端里用回车原地刷新；被重定向到文件时按 10% 采样打印，便于事后核对
            bool live = !Console.IsOutputRedirected;
            double lastPct = -1;
            long lastBytes = -1;
            d.Transfer = delegate (TransferProgress p)
            {
                if (live)
                {
                    Console.Write("\r    " + p.Stage + "  " + p.SizeText() + "  " + p.SpeedText() + "        ");
                    return;
                }
                double pct = p.Percent;
                if (pct < 0) return;
                // 数值没变就别重复打印（合并/收尾阶段会反复报同一帧）
                if (pct == lastPct && p.Received == lastBytes) return;
                if (lastPct < 0 || pct - lastPct >= 10 || pct >= 100)
                {
                    lastPct = pct;
                    lastBytes = p.Received;
                    Console.WriteLine("    " + p.Stage + " " + (int)pct + "%  " + p.SizeText() + "  " + p.SpeedText());
                }
            };

            int ok = 0, fail = 0;
            bool drm = false;
            for (int i = 0; i < items.Count && i < maxItems; i++)
            {
                try { if (d.DownloadOne(s, items[i], null)) ok++; else fail++; }
                catch (DrmProtectedException ex)
                {
                    // 同一门课都是同一套加密，继续下一项只是白烧流量
                    drm = true;
                    fail++;
                    Console.WriteLine("  FAIL " + items[i].Title + ": " + ex.Message);
                    Console.WriteLine("  → DRM 加密，整批停止（不是网络问题，重试无效）");
                    Console.WriteLine("  → 加密分片已自动清理");
                    if (live) Console.WriteLine();
                    break;
                }
                catch (Exception ex) { fail++; Console.WriteLine("  FAIL " + items[i].Title + ": " + ex.Message); }
                if (live) Console.WriteLine();
            }
            Console.WriteLine("[done]    ok=" + ok + " fail=" + fail + (drm ? "  (drm-protected)" : ""));
            return fail == 0 ? 0 : 1;
        }
        /// Verifies the login polling path: generate a QR, then poll once.
        /// The expected answer before scanning is data.code 86101.
        private static int CmdPoll(BiliSession s)
        {
            Dictionary<string, object> g = s.QrGenerate();
            string key = Json.Str(g, "data", "qrcode_key");
            Console.WriteLine("[poll] generated key=" + key.Substring(0, 8) + "...  polling once");
            Dictionary<string, object> p = s.QrPoll(key);
            long code = Json.Num(p, -1, "data", "code");
            Console.WriteLine("[poll] data.code=" + code + "  message='" + Json.Str(p, "data", "message") + "'");
            Console.WriteLine(code == 86101 || code == 86090 || code == 0
                ? "[poll] OK - polling endpoint works (86101=not scanned yet)"
                : "[poll] UNEXPECTED code");
            return 0;
        }

        /// <summary>
        /// 把主窗口真实渲染成 PNG，不需要可见桌面，用于界面回归比对。
        /// 用 renderOnly 构造，避免启动时的网络请求和可能的模态对话框卡住。
        /// </summary>
        private static int CmdUi(string outPath)
        {
            // 必须有 Application 实例，否则 Window 的 ThemeMode（Fluent 主题）
            // 和隐式样式都不会加载，渲染出来的外观和真实运行不一致。
            if (System.Windows.Application.Current == null)
            {
                System.Windows.Application app = new System.Windows.Application();
                app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            }

            MainViewModel vm = AppHost.CreateMainViewModel(false);
            MainWindow w = new MainWindow(vm);
            w.Closing += delegate { vm.SaveState(); };
            w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            w.Left = -30000;
            w.Top = -30000;
            w.ShowInTaskbar = false;
            w.Show();
            w.UpdateLayout();

            // DataGrid 的星号列宽是延迟计算的：只 UpdateLayout 一次的话，
            // 截图会拍到「星号列塌成 10px」的中间态。这里把消息队列跑到空闲，
            // 让布局真正稳定下来再截。
            for (int i = 0; i < 4; i++)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    new Action(delegate { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                System.Threading.Thread.Sleep(80);
            }

            System.Windows.FrameworkElement root = w.Content as System.Windows.FrameworkElement;
            if (root == null)
            {
                Console.Error.WriteLine("[ui] 没有根视觉元素");
                return 1;
            }
            root.UpdateLayout();

            // 截图里带上「下载中」的示例数据，才能检查这一行的排布
            vm.FillDemoTransfer();
            root.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                new Action(delegate { }), System.Windows.Threading.DispatcherPriority.ContextIdle);

            int cw = (int)Math.Ceiling(root.ActualWidth);
            int ch = (int)Math.Ceiling(root.ActualHeight);
            if (cw <= 0 || ch <= 0)
            {
                // 极端情况下布局还没算出来，退回窗口外框尺寸
                cw = (int)w.Width;
                ch = (int)w.Height;
            }
            Console.WriteLine("[ui] client=" + cw + "x" + ch);

            System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                new System.Windows.Media.Imaging.RenderTargetBitmap(
                    cw, ch, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(root);
            Tools.SavePng(rtb, outPath);
            Console.WriteLine("[ui] wrote " + outPath);
            w.Close();
            return 0;
        }

        /// <summary>vmtest 用的对话框实现：只打印，不弹窗、不阻塞。</summary>
        private sealed class HeadlessDialogs : IDialogService
        {
            public void Info(string text, string title)
            {
                Console.WriteLine("[dialog] " + title + ": " + (text ?? "").Replace("\r\n", " / "));
            }
            public string PickFolder(string initialDir) { return null; }
            public string PickFile(string title, string exeName) { return null; }
            public void OpenFolder(string path) { Console.WriteLine("[dialog] 打开目录: " + path); }
            public void CopyText(string text) { }
            public string PasteText() { return null; }
            public bool ShowLogin(LoginViewModel vm)
            {
                Console.WriteLine("[dialog] 需要登录（vmtest 不弹窗）");
                return false;
            }
            public bool ShowTools(ToolsViewModel vm)
            {
                Console.WriteLine("[dialog] 需要指定工具（vmtest 不弹窗）");
                return false;
            }
        }

        /// <summary>
        /// 不开窗口直接驱动 MainViewModel：解析 → 勾选 → 下载。
        ///
        /// 这是 MVVM 重构的收益点：界面逻辑脱离了 Window/控件，
        /// 就能在命令行里当普通对象测。跑通说明 VM 与服务层的接线是好的。
        /// </summary>
        private static int CmdVmTest(string target, int maxItems)
        {
            Console.WriteLine("[vmtest] 无窗口驱动 MainViewModel");

            HeadlessDialogs dialogs = new HeadlessDialogs();
            IBiliService bili = new BiliService();
            IDownloadService download = new DownloadService(bili);
            IConfigService config = new ConfigService();
            IToolLocator tools = new ToolLocator();

            bili.LoadCookiesFrom(config.CookieFile);
            try { bili.Init(); } catch { }
            Console.WriteLine("[vmtest] login=" + bili.IsLogin + "  user=" + bili.UserName);

            MainViewModel vm = new MainViewModel(bili, download, config, tools, dialogs, new QrImageFactory(), false);
            vm.Initialize();
            Console.WriteLine("[vmtest] " + vm.ToolText);

            vm.Url = target;
            vm.FetchAsync().GetAwaiter().GetResult();
            Console.WriteLine("[vmtest] 解析结果: items=" + vm.Items.Count + "   " + vm.Summary);
            if (vm.Items.Count > 0)
                Console.WriteLine("[vmtest]   1. " + vm.Items[0].Title + "   默认选中=" + vm.Items[0].IsSelected);

            if (maxItems > 0 && vm.Items.Count > 0)
            {
                vm.SelectNoneCommand.Execute(null);
                int n = Math.Min(maxItems, vm.Items.Count);
                for (int i = 0; i < n; i++) vm.Items[i].IsSelected = true;
                Console.WriteLine("[vmtest] 开始下载前 " + n + " 项");
                vm.DownloadAsync().GetAwaiter().GetResult();
                Console.WriteLine("[vmtest] 下载结果: " + vm.Summary);
                for (int i = 0; i < n; i++)
                    Console.WriteLine("[vmtest]   " + vm.Items[i].Index + ". " + vm.Items[i].Status
                        + "  " + vm.Items[i].Quality + "  " + vm.Items[i].Title);
            }

            string[] lines = (vm.LogText ?? "").Replace("\r\n", "\n").Split('\n');
            int from = Math.Max(0, lines.Length - 10);
            for (int i = from; i < lines.Length; i++)
                if (lines[i].Trim().Length > 0) Console.WriteLine("[log] " + lines[i]);
            return 0;
        }
    }
}
