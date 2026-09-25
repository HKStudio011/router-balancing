using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Server;

namespace router_balancing
{
    public partial class App : Application
    {
        private readonly ILogService _log;
        private readonly IProxyHost _proxyHost;

        public App(ILogService log, IProxyHost proxyHost)
        {
            InitializeComponent();
            _log = log;
            _proxyHost = proxyHost;

            _log.Info("router-balancing khởi động.");
            // Start nền để UI render không bị Kestrel block; lỗi chỉ log, không crash app
            _ = Task.Run(async () =>
            {
                try
                {
                    await _proxyHost.StartAsync();
                }
                catch (Exception ex)
                {
                    _log.Error("Không khởi động được proxy server.", ex);
                }
            });
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new MainPage()) { Title = "router-balancing" };
        }
    }
}
