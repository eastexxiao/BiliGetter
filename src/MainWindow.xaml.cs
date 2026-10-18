// 主窗口：只负责装配与纯视图行为（日志自动滚到底）。
// 全部业务在 MainViewModel 里，这里没有任何状态。
using System.Windows;
using System.Windows.Controls;

namespace BiliGetter
{
    public partial class MainWindow : Window
    {
        internal MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            // 登录态与工具检查放到窗口可见之后（可能弹模态窗）
            Loaded += delegate { vm.OnLoaded(); };
        }

        /// <summary>日志变长时滚到底——这是纯视图行为，留在 code-behind 合理。</summary>
        private void LogTextChanged(object sender, TextChangedEventArgs e)
        {
            TxtLog.ScrollToEnd();
        }
    }
}
