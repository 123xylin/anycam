using AnyCAD.Foundation;
using AnyCAD.Robot.Studio;
using AnyCAD.Simulate;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using IOPath = System.IO.Path;

namespace RapidRobot.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            GlobalInstance.Initialize();
            ExchangeInstance.Initialize();
            LoadSimulationBackends();

            // CLI self-tests mirror the WinForms Program.cs dispatch so the two
            // frontends share the same AnyCAD.Robot.Studio acceptance suite.
            if (TryRunSelfTest(e.Args))
                return;

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private bool TryRunSelfTest(string[] args)
        {
            string modelRoot = IOPath.Combine(AppContext.BaseDirectory, "Model");
            int exitCode = -1;
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--sdk-self-test")
                exitCode = SimulationSdkAcceptance.Run(args[1], args.Length == 3 ? args[2] : null);
            else if (args.Length == 3 && args[0] == "--collision-self-test")
                exitCode = CollisionDemoAcceptance.Run(args[1], args[2]);
            else if (args.Length == 3 && args[0] == "--manual-kr500-self-test")
                exitCode = ManualKr500Acceptance.Run(args[1], args[2]);
            else if (args.Length == 3 && args[0] == "--manual-3a-self-test")
                exitCode = Manual3AAcceptance.Run(args[1], args[2]);
            if (exitCode < 0)
                return false;
            Environment.ExitCode = exitCode;
            Shutdown();
            return true;
        }

        private static void LoadSimulationBackends()
        {
            var nativeDirectory = IOPath.Combine(AppContext.BaseDirectory,
                "runtimes", "win-x64", "native");
            foreach (var pluginName in new[] {
                "AnyCAD.Simulate.Ik.dll",
                "AnyCAD.Simulate.RL.dll",
                "AnyCAD.Simulate.FCL.dll",
                "AnyCAD.Simulate.Plan.dll"
            })
            {
                var pluginPath = IOPath.Combine(nativeDirectory, pluginName);
                try
                {
                    NativeLibrary.Load(pluginPath);
                    SimulationBackends.LoadPlugin(pluginPath);
                }
                catch (Exception error)
                {
                    Debug.WriteLine(pluginName + ": " + error.Message);
                }
            }

            using var defaults = new BackendSelection {
                Planner = "OMPL",
                Collision = "FCL"
            };
            SimulationBackends.SetDefaults(defaults);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                base.OnExit(e);
            }
            finally
            {
                GlobalInstance.Destroy();
            }
        }
    }
}
