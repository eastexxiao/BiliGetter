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
            "tools", "list", "dl", "poll", "qr", "ui"
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
                    case "poll": return CmdPoll(s);
                    default:
                        Console.WriteLine("usage: qr <png> | poll | list <target> | dl <target> <capIndex> <maxItems> | ui <png>");
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
                    if (k == "ffmpeg_path" && v.Length > 0 && File.Exists(v)) ds.FfmpegPath = v;
                    else if (k == "aria2c_path" && v.Length > 0 && File.Exists(v)) ds.Aria2cPath = v;
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

            int ok = 0, fail = 0;
            for (int i = 0; i < items.Count && i < maxItems; i++)
            {
                try { if (d.DownloadOne(s, items[i], null)) ok++; else fail++; }
                catch (Exception ex) { fail++; Console.WriteLine("  FAIL " + items[i].Title + ": " + ex.Message); }
            }
            Console.WriteLine("[done]    ok=" + ok + " fail=" + fail);
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

            MainWindow w = new MainWindow(true);
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
    }
}
