// 组合根：唯一一处把具体实现装配起来的地方。
// 不引 DI 容器 —— 只有五个服务，手工装配比配置一个容器更清楚。
namespace BiliGetter
{
    internal static class AppHost
    {
        /// <param name="autoStart">
        /// false 时不做启动动作（不读 Cookie、不检查工具、不弹窗），
        /// 供 cli ui 渲染界面用。
        /// </param>
        public static MainViewModel CreateMainViewModel(bool autoStart)
        {
            IBiliService bili = new BiliService();
            IDownloadService download = new DownloadService(bili);
            IConfigService config = new ConfigService();
            IToolLocator tools = new ToolLocator();
            IDialogService dialogs = new WpfDialogService();
            IQrImageFactory qr = new QrImageFactory();

            MainViewModel vm = new MainViewModel(bili, download, config, tools, dialogs, qr, autoStart);
            vm.Initialize();
            return vm;
        }
    }
}
