// Console harness used to verify the non-GUI core against the live API.
// Compiled from the same sources as the GUI but with /main:BiliGetter.CliTest.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace BiliGetter
{
    internal static class CliTest
    {
        [STAThread]
        private static int Main(string[] args)
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
            Bitmap bmp = Tools.RenderQr(qr, 6, 4);
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("[qr] wrote " + outPath + "  " + bmp.Width + "x" + bmp.Height);
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
            ds.QualityCap = Quality.CapForIndex(capIndex);
            Console.WriteLine("[out]     " + ds.OutDir + "  useAria2c=" + ds.UseAria2c);

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

        /// Renders the real MainForm to a PNG without needing a visible desktop,
        /// and dumps the computed layout so it can be checked deterministically.
        private static int CmdUi(string outPath)
        {
            MainForm f = new MainForm();
            f.ClientSize = new System.Drawing.Size(1000, 720);
            IntPtr h = f.Handle;          // force handle + layout
            f.CreateControl();
            f.PerformLayout();
            System.Windows.Forms.Application.DoEvents();
            Console.WriteLine("[ui] client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
            Dump(f, 0);

            f.Show();
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(400);
            System.Windows.Forms.Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("[ui] wrote " + outPath);
            }
            f.Close();
            f.Dispose();
            return 0;
        }

        private static void Dump(Control c, int depth)
        {
            string pad = new string(' ', depth * 2);
            string txt = c.Text == null ? "" : c.Text.Replace("\r", " ").Replace("\n", " ");
            if (txt.Length > 34) txt = txt.Substring(0, 34) + "..";
            Console.WriteLine(pad + c.GetType().Name
                + " [" + c.Left + "," + c.Top + " " + c.Width + "x" + c.Height + "]"
                + " dock=" + c.Dock + " vis=" + c.Visible
                + (txt.Length > 0 ? " \"" + txt + "\"" : ""));
            foreach (Control ch in c.Controls) Dump(ch, depth + 1);
        }
    }
}
