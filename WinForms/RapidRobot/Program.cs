using System;
using System.Windows.Forms;

namespace RapidRobot
{
    static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AnyCAD.Foundation.GlobalInstance.Initialize();
            AnyCAD.Foundation.ExchangeInstance.Initialize();
            if (!AnyCAD.Simulate.SimulateInstance.Instance().Initialize())
            {
                MessageBox.Show(
                    AnyCAD.Simulate.SimulateInstance.Instance().GetInitializationDiagnostic(),
                    "Simulation initialization",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            try
            {
                if ((args.Length == 2 || args.Length == 3) && args[0] == "--sdk-self-test")
                    return SimulationSdkAcceptance.Run(args[1], args.Length == 3 ? args[2] : null);
                Application.Run(new MainForm());
                return 0;
            }
            finally { AnyCAD.Foundation.GlobalInstance.Destroy(); }
        }
    }
}
