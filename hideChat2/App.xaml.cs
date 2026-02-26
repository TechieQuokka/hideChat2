using System;
using System.Windows;

namespace hideChat2
{
    public partial class App : Application
    {
        public static int BasePort { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Parse command line arguments or use default port
            if (e.Args.Length > 0 && int.TryParse(e.Args[0], out int port))
            {
                BasePort = port;
            }
            else
            {
                BasePort = Config.DEFAULT_BASE_PORT;
            }

            // Create and show main window
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }
    }
}
