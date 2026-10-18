// 扫码登录窗口：只把 ViewModel 挂上去，并在加载时让 VM 开始取码/轮询。
using System.Windows;

namespace BiliGetter
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            Loaded += delegate
            {
                LoginViewModel vm = DataContext as LoginViewModel;
                if (vm != null) vm.Initialize();
            };
        }
    }
}
