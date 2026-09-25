using AnyCAD.Foundation;
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
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
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
