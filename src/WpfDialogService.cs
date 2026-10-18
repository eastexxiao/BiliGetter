// WPF 侧的对话框实现：把 IDialogService 落到 MessageBox / 文件选择器 / 模态窗口上。
using System;
using System.IO;
using System.Windows;

namespace BiliGetter
{
    internal sealed class WpfDialogService : IDialogService
    {
        public void Info(string text, string title)
        {
            MessageBox.Show(text, title);
        }

        public string PickFolder(string initialDir)
        {
            Microsoft.Win32.OpenFolderDialog dlg = new Microsoft.Win32.OpenFolderDialog();
            dlg.Title = "选择下载目录";
            dlg.Multiselect = false;
            try
            {
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                    dlg.InitialDirectory = initialDir;
            }
            catch { }
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }

        public string PickFile(string title, string exeName)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = title;
            dlg.Filter = exeName + "|" + exeName + "|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
            dlg.CheckFileExists = true;
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        public void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Tools.OpenUrl(path);
            }
            catch { }
        }

        public void CopyText(string text)
        {
            try { if (!string.IsNullOrEmpty(text)) System.Windows.Clipboard.SetText(text); }
            catch { }
        }

        public string PasteText()
        {
            try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
            catch { return null; }
        }

        public bool ShowLogin(LoginViewModel vm)
        {
            LoginWindow w = new LoginWindow();
            w.DataContext = vm;
            vm.RequestClose = delegate (bool ok)
            {
                try { w.DialogResult = ok; } catch { }
            };
            return w.ShowDialog() == true;
        }

        public bool ShowTools(ToolsViewModel vm)
        {
            ToolsWindow w = new ToolsWindow();
            w.DataContext = vm;
            vm.RequestClose = delegate (bool ok)
            {
                try { w.DialogResult = ok; } catch { }
            };
            return w.ShowDialog() == true;
        }
    }

    /// <summary>把二维码文本渲染成 WPF 位图（ViewModel 只见到 object）。</summary>
    internal sealed class QrImageFactory : IQrImageFactory
    {
        public object Create(string text, int moduleSize, int quietZone)
        {
            QrCode qr = QrCode.Encode(text, QrTables.LevelM);
            return Tools.RenderQr(qr, moduleSize, quietZone);
        }
    }
}
