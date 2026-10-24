using System.Windows;
using AnyCAD.Foundation;
using AnyCAM.Milling.Engine;

namespace AnyCAM.Milling
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 命令行模式：不建界面，跑完引擎链路直接退出（排查工序失败用）
            if (EngineProbe.WantsProbe(e.Args))
            {
                EngineProbe.Run();
                Shutdown(0);
                return;
            }

            base.OnStartup(e);
            GlobalInstance.EnableD3DWPF = true;
            AnyCAD.Foundation.GlobalInstance.Initialize();
            AnyCAD.Foundation.ExchangeInstance.Initialize();

            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(args.Exception.Message, "AnyCAM Mill",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            AnyCAD.Foundation.GlobalInstance.Destroy();
        }
    }
}
