// 「指定工具位置」对话框：让用户用文件对话框直接挑 ffmpeg / aria2c，
// 而不是只能把文件放到程序目录里。启动检查失败时会自动弹出。
using System;
using System.IO;
using System.Windows;

namespace BiliGetter
{
    public partial class ToolsWindow : Window
    {
        /// <summary>用户确认后的 ffmpeg 路径（必填）。</summary>
        public string FfmpegPath;
        /// <summary>用户确认后的 aria2c 路径（null 表示不指定，走单线程）。</summary>
        public string Aria2cPath;

        public ToolsWindow(string ffmpeg, string aria2c, bool startup)
        {
            InitializeComponent();
            Title = startup ? "启动检查：需要指定工具位置" : "工具路径";
            TxtHint.Text = startup
                ? "启动检查没找到工具。ffmpeg 是必需的（合并音视频），aria2c 是可选的（多线程加速）。\r\n"
                  + "请点「浏览…」指定；也可以点「稍后」先去用，之后在主窗口右上角「工具路径…」里设置。"
                : "ffmpeg 必需（合并音视频），aria2c 可选（多线程加速）。点「浏览…」选择。";

            TxtFfmpeg.Text = ffmpeg ?? "";
            TxtAria2c.Text = aria2c ?? "";
        }

        private static string PickFile(Window owner, string exeName)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选择 " + exeName;
            dlg.Filter = exeName + "|" + exeName + "|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
            dlg.CheckFileExists = true;
            if (dlg.ShowDialog(owner) == true) return dlg.FileName;
            return null;
        }

        private void PickFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            string p = PickFile(this, "ffmpeg.exe");
            if (p != null) TxtFfmpeg.Text = p;
        }

        private void PickAria2c_Click(object sender, RoutedEventArgs e)
        {
            string p = PickFile(this, "aria2c.exe");
            if (p != null) TxtAria2c.Text = p;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string f = TxtFfmpeg.Text.Trim();
            string a = TxtAria2c.Text.Trim();

            if (f.Length == 0)
            {
                MessageBox.Show(this, "ffmpeg 是必需的，请指定它的位置。", "还差一步");
                return;
            }
            if (!File.Exists(f))
            {
                MessageBox.Show(this, "这个 ffmpeg 路径不存在：\r\n" + f, "路径无效");
                return;
            }
            if (a.Length > 0 && !File.Exists(a))
            {
                MessageBox.Show(this, "这个 aria2c 路径不存在：\r\n" + a, "路径无效");
                return;
            }

            FfmpegPath = f;
            Aria2cPath = a.Length == 0 ? null : a;
            DialogResult = true;
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
