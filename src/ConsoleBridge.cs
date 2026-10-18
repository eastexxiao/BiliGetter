// WinExe 程序默认没有控制台。以命令行模式启动时，把自己挂到父进程
// （也就是调用它的那个终端）的控制台上，Console.WriteLine 才看得到。
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BiliGetter
{
    internal static class ConsoleBridge
    {
        private const int AttachParentProcess = -1;
        private const uint CodePageUtf8 = 65001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleOutputCP();

        private static bool _attached;

        /// <summary>
        /// 挂接到父进程的控制台，并把 Console.Out/Error 重新绑定到该控制台。
        ///
        /// 本程序是 WinExe：双击时不弹黑框（图形界面需要这样），
        /// 但也就意味着进程启动时没有标准输出。命令行模式下如果只
        /// AttachConsole 而不重新绑定，输出仍然会丢。
        ///
        /// 从资源管理器直接运行（没有父控制台）时挂接失败，属预期行为。
        /// </summary>
        public static void Attach()
        {
            if (_attached) return;
            _attached = true;

            bool attached;
            try { attached = AttachConsole(AttachParentProcess); }
            catch { attached = false; }
            if (!attached) return;

            try
            {
                Encoding enc = ConsoleEncoding();

                Stream stdout = Console.OpenStandardOutput();
                if (stdout != null)
                    Console.SetOut(new StreamWriter(stdout, enc) { AutoFlush = true });

                Stream stderr = Console.OpenStandardError();
                if (stderr != null)
                    Console.SetError(new StreamWriter(stderr, enc) { AutoFlush = true });
            }
            catch
            {
                // 绑定失败不影响功能，只是可能看不到输出
            }
        }

        /// <summary>
        /// 取控制台当前代码页对应的编码。
        ///
        /// 这里有两个坑，都踩过：
        ///  1) <c>new StreamWriter(Stream)</c> 的默认编码是 UTF-8，必须显式指定；
        ///  2) 更隐蔽的是，现代 .NET 的 <c>Console.OutputEncoding</c> 默认值本身就是
        ///     UTF-8，不能拿它当「控制台编码」用——中文 Windows 的控制台是 GBK(936)，
        ///     写 UTF-8 字节过去就成了乱码。所以直接向系统询问控制台代码页。
        /// </summary>
        private static Encoding ConsoleEncoding()
        {
            try
            {
                uint codePage = GetConsoleOutputCP();
                if (codePage != 0 && codePage != CodePageUtf8)
                {
                    // GBK 等传统代码页在现代 .NET 中需要显式注册提供程序
                    try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
                    catch { }
                    return Encoding.GetEncoding((int)codePage);
                }
            }
            catch { }
            return new UTF8Encoding(false);
        }
    }
}
