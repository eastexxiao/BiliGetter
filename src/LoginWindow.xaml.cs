// 扫码登录窗口：取二维码 → 每 2 秒轮询一次 → 成功后由调用方保存 Cookie。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace BiliGetter
{
    public partial class LoginWindow : Window
    {
        private readonly BiliSession _s;
        private string _url = "";
        private volatile bool _closed;

        public LoginWindow(BiliSession s)
        {
            InitializeComponent();
            _s = s;
            Loaded += LoginWindow_Loaded;
            Closed += delegate { _closed = true; };
        }

        private void Ui(Action a)
        {
            if (Dispatcher.CheckAccess()) { a(); return; }
            Dispatcher.InvokeAsync(a);
        }

        private void SetStatus(string s) { Ui(delegate { TxtStatus.Text = s; }); }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
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
                    System.Windows.Media.Imaging.BitmapSource src = Tools.RenderQr(qr, 6, 4);
                    Ui(delegate
                    {
                        ImgQr.Source = src;
                        BtnBrowser.IsEnabled = true;
                        BtnCopy.IsEnabled = true;
                    });
                    SetStatus("用「哔哩哔哩」App 扫一扫");

                    DateTime deadline = DateTime.Now.AddMinutes(3);
                    while (DateTime.Now < deadline && !_closed)
                    {
                        Thread.Sleep(2000);
                        if (_closed) return;

                        Dictionary<string, object> p;
                        try { p = _s.QrPoll(key); }
                        catch { continue; }

                        long code = Json.Num(p, -1, "data", "code");
                        if (code == 0)
                        {
                            _s.Init();
                            SetStatus("登录成功：" + _s.UName);
                            Thread.Sleep(400);
                            Ui(delegate { if (!_closed) DialogResult = true; });
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

        private void BtnBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (_url.Length > 0) Tools.OpenUrl(_url);
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try { if (_url.Length > 0) Clipboard.SetText(_url); } catch { }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _closed = true;
            DialogResult = false;
        }
    }
}
