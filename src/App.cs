// WPF 前端的入口：命令行 / 图形界面分流，以及共用的小工具（找 ffmpeg、画二维码）。
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BiliGetter
{
    internal static class Program
    {
        /// <summary>
        /// 单一入口：带 --cli（或直接给出已知子命令）走命令行，否则启动图形界面。
        ///
        /// 输出类型是 WinExe（双击不弹黑框），所以命令行模式必须先挂到
        /// 父进程的控制台上，否则 Console.WriteLine 会全部丢失。
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            if (IsCliInvocation(args))
            {
                ConsoleBridge.Attach();
                try { return Cli.Run(StripCliSwitch(args)); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("FATAL: " + ex.GetType().Name + ": " + ex.Message);
                    return 1;
                }
            }

            try
            {
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.Run(new MainWindow());
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.GetType().Name + "：" + ex.Message, "B 站下载器");
                return 1;
            }
            return 0;
        }

        /// <summary>第一个参数是 --cli，或本身就是已知子命令时，走命令行模式。</summary>
        private static bool IsCliInvocation(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            string a = args[0].TrimStart('-').ToLowerInvariant();
            if (a == "cli" || a == "help" || a == "?") return true;
            return Array.IndexOf(Cli.Commands, a) >= 0;
        }

        /// <summary>去掉开头的 --cli，剩下的才是真正的子命令与参数。</summary>
        private static string[] StripCliSwitch(string[] args)
        {
            if (args.Length > 0 && args[0].TrimStart('-').ToLowerInvariant() == "cli")
            {
                string[] rest = new string[args.Length - 1];
                Array.Copy(args, 1, rest, 0, rest.Length);
                return rest;
            }
            return args;
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
        ///   1. a bare "ffmpeg.exe", starting beside the exe and walking up to 8
        ///      parents — enough to climb out of every build layout we produce,
        ///      including bin\Release\net10.0-windows\win-x64\publish\;
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
            const int MaxParentLevels = 8;
            string baseDir = BaseDir;

            // Pass 1: the plain file name, nearest directory first.
            DirectoryInfo dir = new DirectoryInfo(baseDir);
            for (int up = 0; up < MaxParentLevels && dir != null; up++)
            {
                string p = Path.Combine(dir.FullName, exeName);
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }

            // Pass 2: the legacy Source\... layouts.
            dir = new DirectoryInfo(baseDir);
            for (int up = 0; up < MaxParentLevels && dir != null; up++)
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

        /// <summary>
        /// 把二维码矩阵画成 WPF 位图。
        /// 用 Bgra32 直接填像素，替代原来的 System.Drawing.Bitmap + Graphics ——
        /// WPF 版不需要再引 System.Drawing，单文件体积和依赖都更干净。
        /// </summary>
        public static BitmapSource RenderQr(QrCode qr, int moduleSize, int quietZone)
        {
            int n = qr.ModuleCount;
            int size = (n + quietZone * 2) * moduleSize;
            int stride = size * 4;
            byte[] px = new byte[stride * size];
            for (int i = 0; i < px.Length; i++) px[i] = 255;   // 白色且不透明

            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    if (!qr.IsDark(r, c)) continue;
                    int x0 = (c + quietZone) * moduleSize;
                    int y0 = (r + quietZone) * moduleSize;
                    for (int y = y0; y < y0 + moduleSize; y++)
                    {
                        int rowBase = y * stride + x0 * 4;
                        for (int x = 0; x < moduleSize; x++)
                        {
                            int p = rowBase + x * 4;
                            px[p] = 0; px[p + 1] = 0; px[p + 2] = 0; px[p + 3] = 255;
                        }
                    }
                }
            }

            BitmapSource src = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, stride);
            src.Freeze();
            return src;
        }

        /// <summary>把位图存成 PNG（不依赖 System.Drawing）。</summary>
        public static void SavePng(BitmapSource src, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                enc.Save(fs);
        }

        public static void OpenUrl(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }
    }
}
