// 扫码登录的 ViewModel：取二维码 → 每 2 秒轮询 → 成功后通知宿主。
//
// 这里是原来 LoginForm 里那段线程代码的等价物，但不再碰任何控件：
// 图像通过 IQrImageFactory 拿成 object，状态通过属性暴露，剪贴板走 IDialogService。
using System;
using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliGetter
{
    internal partial class LoginViewModel : ObservableObject
    {
        private readonly IBiliService _bili;
        private readonly IQrImageFactory _qr;
        private readonly IDialogService _dialogs;
        private readonly SynchronizationContext _ui;
        private volatile bool _cancelled;
        private string _url = "";

        /// <summary>由宿主窗口设置：true 表示登录成功。</summary>
        public Action<bool> RequestClose;

        /// <summary>登录是否成功（宿主据此决定要不要保存 Cookie）。</summary>
        public bool Succeeded { get; private set; }

        [ObservableProperty] private object qrImage;
        [ObservableProperty] private string statusText = "正在获取二维码…";
        [ObservableProperty] private bool canUseLink;

        public LoginViewModel(IBiliService bili, IQrImageFactory qr, IDialogService dialogs)
        {
            _bili = bili;
            _qr = qr;
            _dialogs = dialogs;
            _ui = SynchronizationContext.Current;
        }

        /// <summary>窗口 Loaded 时调用（由 IDialogService 触发）。</summary>
        public void Initialize()
        {
            Thread t = new Thread(delegate () { Run(); });
            t.IsBackground = true;
            t.Start();
        }

        private void OnUi(Action a)
        {
            if (_ui == null) { a(); return; }
            _ui.Post(delegate { a(); }, null);
        }

        private void SetStatus(string s) { OnUi(delegate { StatusText = s; }); }

        private void Run()
        {
            try
            {
                Dictionary<string, object> g = _bili.QrGenerate();
                _url = Json.Str(g, "data", "url");
                string key = Json.Str(g, "data", "qrcode_key");
                if (string.IsNullOrEmpty(_url) || string.IsNullOrEmpty(key))
                {
                    SetStatus("获取二维码失败");
                    return;
                }

                object img = _qr.Create(_url, 6, 4);
                OnUi(delegate { QrImage = img; CanUseLink = true; });
                SetStatus("用「哔哩哔哩」App 扫一扫");

                DateTime deadline = DateTime.Now.AddMinutes(3);
                while (DateTime.Now < deadline && !_cancelled)
                {
                    Thread.Sleep(2000);
                    if (_cancelled) return;

                    Dictionary<string, object> p;
                    try { p = _bili.QrPoll(key); }
                    catch { continue; }

                    long code = Json.Num(p, -1, "data", "code");
                    if (code == 0)
                    {
                        _bili.Init();
                        SetStatus("登录成功：" + _bili.UserName);
                        Thread.Sleep(400);
                        OnUi(delegate { if (!_cancelled) Finish(true); });
                        return;
                    }
                    if (code == 86090) SetStatus("已扫码，请在手机上点「确认登录」");
                    else if (code == 86038) { SetStatus("二维码已失效，请重新打开本窗口"); return; }
                }
                SetStatus("等待超时，请重新打开本窗口");
            }
            catch (Exception ex) { SetStatus("出错：" + ex.Message); }
        }

        [RelayCommand]
        private void OpenInBrowser()
        {
            if (_url.Length > 0) Tools.OpenUrl(_url);
        }

        [RelayCommand]
        private void CopyLink()
        {
            if (_url.Length > 0) _dialogs.CopyText(_url);
        }

        [RelayCommand]
        private void Cancel()
        {
            _cancelled = true;
            Finish(false);
        }

        private void Finish(bool ok)
        {
            Succeeded = ok;
            Action<bool> h = RequestClose;
            if (h != null) h(ok);
        }
    }
}
