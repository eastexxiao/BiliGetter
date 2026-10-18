// 工具定位：沿用 Tools.Find 的查找顺序（程序目录 → 逐级向上 8 层 → Source\ 布局 → PATH）。
using System;
using System.IO;

namespace BiliGetter
{
    internal sealed class ToolLocator : IToolLocator
    {
        public string FindFfmpeg() { return Tools.FindFfmpeg(); }
        public string FindAria2c() { return Tools.FindAria2c(); }

        /// <summary>
        /// 判断某个 exe 是否在程序目录内。工具放外面时，Windows 的
        /// 「受控文件夹访问」可能拦住它写文件（Access is denied），
        /// 界面据此给一句提醒。
        /// </summary>
        public bool IsInsideApp(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            try
            {
                string root = Path.GetFullPath(Tools.BaseDir).TrimEnd('\\') + "\\";
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }   // 判断不出来就别打扰用户
        }
    }
}
