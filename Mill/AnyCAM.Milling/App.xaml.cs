using AnyCAD.Foundation;
using AnyCAM.Milling.Engine;
using System.Threading.Tasks;
using WpfApplication = System.Windows.Application;

namespace AnyCAM.Milling
{
    public partial class App : WpfApplication
    {
        internal static bool AnyCadInitialized { get; private set; }

        private void Application_Startup(object sender, System.Windows.StartupEventArgs e)
        {
            if (EngineProbe.TryRunCli(e.Args, out var cliExit))
            {
                Shutdown(cliExit);
                return;
            }

            // sample 实际走 OpenGL/SDL 视口；EnableD3DWPF=true 在本机 Intel UHD 上会走 D3DImageHost
            // 并在 wglDXRegisterObjectNV 处失败（CreateView(D3DGL) null）。
            GlobalInstance.LogFileName = System.IO.Path.Combine(AppContext.BaseDirectory, "anycad.log");
            GlobalInstance.EnableD3DWPF = false;
            GlobalInstance.Initialize();
            ExchangeInstance.Initialize();
            ModelingEngine.EnableSnapShape();
            AnyCadInitialized = true;

            SessionLog.Info($"启动 OK · 日志目录 {AppContext.BaseDirectory}");
            SessionLog.Info("会话日志 anycam-app.log · 严重错误 anycam-crash.log · SDK anycad.log");

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                SessionLog.Crash("UnhandledException", args.ExceptionObject as Exception);
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                SessionLog.Warn("ProcessExit · LastAction=" + (SessionLog.LastAction ?? "(无)"));
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                SessionLog.Crash("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            DispatcherUnhandledException += (_, args) =>
            {
                SessionLog.Crash("DispatcherUnhandledException", args.Exception);
                System.Windows.MessageBox.Show(
                    args.Exception.ToString(),
                    "AnyCAM Mill",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                args.Handled = true;
            };
        }

        private void Application_Exit(object sender, System.Windows.ExitEventArgs e)
        {
            SessionLog.Info($"Application_Exit code={e.ApplicationExitCode}");
            if (AnyCadInitialized)
                GlobalInstance.Destroy();
        }
    }
}
