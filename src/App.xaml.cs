using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Cre8Labs.IPSwitcher
{
    public partial class App : Application
    {
        public static string CrashLogPath => Path.Combine(Path.GetTempPath(), "Cre8Labs-IPSwitcher-crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) => WriteCrash(args.ExceptionObject as Exception);
            Core.NetworkService.DemoMode = Array.IndexOf(e.Args, "--demo") >= 0;
            base.OnStartup(e);
        }

        void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrash(e.Exception);
            MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were saved to:\n" + CrashLogPath,
                "Cre8Labs IP Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        static void WriteCrash(Exception ex)
        {
            try { File.AppendAllText(CrashLogPath, $"[{DateTime.Now:u}]\n{ex}\n\n"); }
            catch (IOException) { }
        }
    }
}
