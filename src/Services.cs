// 服务层契约。
//
// 目的：把「ViewModel 不该直接做的事」抽成接口 —— 文件、进程、对话框、
// 网络会话都藏到实现里。这样 ViewModel 只依赖接口，可以脱离界面驱动
// （见 cli vmtest），也便于替换。
//
// 全部 internal：这是一个应用程序而不是类库，整个 exe 就是一个程序集，
// 没有必要为对外 API 的整洁性付出可访问性代价。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BiliGetter
{
    /// <summary>ini 里的全部配置项（键名与旧版完全一致，便于沿用旧配置）。</summary>
    internal sealed class AppConfig
    {
        public string OutDir = "";
        public int QualityIndex;
        public bool UseAria2c = true;
        public string FfmpegPath;
        public string Aria2cPath;
    }

    /// <summary>清晰度探测结果（以下载列表第 1 项为准）。</summary>
    internal sealed class QualityProbe
    {
        public string Label = "";
        public string AudioCodecs = "";
    }

    /// <summary>一批下载的汇总结果。</summary>
    internal sealed class DownloadBatchResult
    {
        public int Ok;
        public int Fail;
        public bool Cancelled;
        /// <summary>因为遇到 DRM 加密而整体中止（此时不必再继续下一项）。</summary>
        public bool DrmProtected;
    }

    /// <summary>
    /// 下载过程中回调给 ViewModel 的观察者。
    ///
    /// 所有方法都在后台线程触发，实现方负责切回界面线程
    /// （MainViewModel 用 SynchronizationContext 统一处理）。
    /// </summary>
    internal interface IDownloadObserver
    {
        void ItemStarting(int ordinal, int total, DownloadItem item);
        void ItemFinished(DownloadItem item, bool success);
        void Transferred(TransferProgress p);
        void Log(string line);
    }

    /// <summary>账号与解析：登录态、二维码、目标解析、清晰度探测。</summary>
    internal interface IBiliService
    {
        bool IsLogin { get; }
        string UserName { get; }
        long Mid { get; }

        /// <summary>Downloader 需要一个 BiliSession；服务层本就构建在核心类型之上。</summary>
        BiliSession Session { get; }

        void Init();
        void LoadCookiesFrom(string path);
        void SaveCookiesTo(string path);
        void ClearCookies(string path);

        Dictionary<string, object> QrGenerate();
        Dictionary<string, object> QrPoll(string key);

        Target Resolve(string input);
        List<DownloadItem> Build(Target target, Action<string> log);

        /// <summary>用第一项探一次播放地址；失败抛异常，返回 null 表示拿不到 dash。</summary>
        QualityProbe ProbeQuality(DownloadItem first, int qualityCap);
    }

    /// <summary>跑一批下载，并把进度/结果回调出去。</summary>
    internal interface IDownloadService
    {
        Task<DownloadBatchResult> RunAsync(
            IList<DownloadItem> items,
            DownloadSettings settings,
            IDownloadObserver observer,
            CancellationToken ct);
    }

    /// <summary>配置与路径：ini 读写、相对路径解析、几个约定的文件位置。</summary>
    internal interface IConfigService
    {
        string BaseDir { get; }
        string ConfigFile { get; }
        string CookieFile { get; }
        string DefaultOutDir { get; }

        AppConfig Load();
        void Save(AppConfig cfg);

        /// <summary>ini 里的相对路径按程序目录解析。</summary>
        string Resolve(string stored);

        /// <summary>程序目录内的路径存成相对形式。</summary>
        string Storable(string full);
    }

    /// <summary>找 ffmpeg / aria2c，并判断它们是否在程序目录内。</summary>
    internal interface IToolLocator
    {
        string FindFfmpeg();
        string FindAria2c();
        bool IsInsideApp(string path);
    }

    /// <summary>
    /// 界面边界：消息框、选择器、打开目录、剪贴板，以及两个模态窗口。
    /// ViewModel 只通过它跟用户交互，自身不 new Window、不碰 MessageBox。
    /// </summary>
    internal interface IDialogService
    {
        void Info(string text, string title);
        string PickFolder(string initialDir);
        string PickFile(string title, string exeName);
        void OpenFolder(string path);
        void CopyText(string text);
        string PasteText();

        /// <summary>模态显示扫码登录窗；返回 true 表示登录成功。</summary>
        bool ShowLogin(LoginViewModel vm);

        /// <summary>模态显示工具路径窗；返回 true 表示用户点了确定。</summary>
        bool ShowTools(ToolsViewModel vm);
    }

    /// <summary>
    /// 把二维码文本渲染成可绑定的图像对象。
    /// 返回 object 而不是 BitmapSource，是为了让 ViewModel 不依赖 WPF 类型。
    /// </summary>
    internal interface IQrImageFactory
    {
        object Create(string text, int moduleSize, int quietZone);
    }
}
