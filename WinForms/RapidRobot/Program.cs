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
            // The 2026.9.22.1736 SDK's SimulateInstance.Initialize() blocks while
            // loading Physics. Load the planner and FCL plugins explicitly instead.
            var native = System.IO.Path.Combine(AppContext.BaseDirectory,
                "runtimes", "win-x64", "native");
            foreach (var plugin in new[] { "AnyCAD.Simulate.FCL.dll", "AnyCAD.Simulate.Plan.dll" })
            {
                var path = System.IO.Path.Combine(native, plugin);
                try
                {
                    System.Runtime.InteropServices.NativeLibrary.Load(path);
                    AnyCAD.Simulate.SimulationBackends.LoadPlugin(path);
                }
                catch (Exception error)
                {
                    System.Diagnostics.Debug.WriteLine(plugin + ": " + error.Message);
                }
            }
            using (var defaults = new AnyCAD.Simulate.BackendSelection {
                Planner = "OMPL", Collision = "FCL"
            })
                AnyCAD.Simulate.SimulationBackends.SetDefaults(defaults);
            try
            {
                if ((args.Length == 2 || args.Length == 3) && args[0] == "--sdk-self-test")
                    return SimulationSdkAcceptance.Run(args[1], args.Length == 3 ? args[2] : null);
                if (args.Length == 3 && args[0] == "--collision-self-test")
                    return CollisionDemoAcceptance.Run(args[1], args[2]);
                Application.Run(new MainForm());
                return 0;
            }
            finally { AnyCAD.Foundation.GlobalInstance.Destroy(); }
        }
    }
}
