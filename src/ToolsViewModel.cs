// 「工具路径」窗口的 ViewModel：校验 + 选择文件，不直接弹文件对话框。
using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliGetter
{
    internal partial class ToolsViewModel : ObservableObject
    {
        private readonly IDialogService _dialogs;

        /// <summary>由宿主窗口设置：true 表示用户点了确定。</summary>
        public Action<bool> RequestClose;

        [ObservableProperty] private string title = "工具路径";
        [ObservableProperty] private string hintText = "";
        [ObservableProperty] private string ffmpegPath = "";
        [ObservableProperty] private string aria2cPath = "";

        public ToolsViewModel(IDialogService dialogs, bool startup)
        {
            _dialogs = dialogs;
            Title = startup ? "启动检查：需要指定工具位置" : "工具路径";
            HintText = startup
                ? "启动检查没找到工具。ffmpeg 是必需的（合并音视频），aria2c 是可选的（多线程加速）。\r\n"
                  + "请点「浏览…」指定；也可以点「稍后」先去用，之后在主窗口右上角「工具路径…」里设置。"
                : "ffmpeg 必需（合并音视频），aria2c 可选（多线程加速）。点「浏览…」选择。";
        }

        [RelayCommand]
        private void PickFfmpeg()
        {
            string p = _dialogs.PickFile("选择 ffmpeg.exe", "ffmpeg.exe");
            if (p != null) FfmpegPath = p;
        }

        [RelayCommand]
        private void PickAria2c()
        {
            string p = _dialogs.PickFile("选择 aria2c.exe", "aria2c.exe");
            if (p != null) Aria2cPath = p;
        }

        [RelayCommand]
        private void Ok()
        {
            string f = (FfmpegPath ?? "").Trim();
            string a = (Aria2cPath ?? "").Trim();

            if (f.Length == 0)
            {
                _dialogs.Info("ffmpeg 是必需的，请指定它的位置。", "还差一步");
                return;
            }
            if (!File.Exists(f))
            {
                _dialogs.Info("这个 ffmpeg 路径不存在：\r\n" + f, "路径无效");
                return;
            }
            if (a.Length > 0 && !File.Exists(a))
            {
                _dialogs.Info("这个 aria2c 路径不存在：\r\n" + a, "路径无效");
                return;
            }

            FfmpegPath = f;
            Aria2cPath = a;
            Fire(true);
        }

        [RelayCommand]
        private void Later() { Fire(false); }

        private void Fire(bool ok)
        {
            Action<bool> h = RequestClose;
            if (h != null) h(ok);
        }
    }
}
