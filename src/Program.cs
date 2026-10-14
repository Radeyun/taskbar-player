namespace TaskbarPlayer;

internal static class Program
{
    private const string AppTitle = "Taskbar Player";

    [STAThread]
    private static void Main(string[] args)
    {
        AppConfig.Parse(args);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Started from a download folder etc.: offer to install (or update) and start with Windows
        bool declinedInstall = false;
        if (!AppConfig.Portable && !Installer.IsInstalledCopy)
        {
            if (Installer.OfferInstall(out declinedInstall)) return;
        }

        // One instance per user session
        using var mutex = new Mutex(true, @"Local\TaskbarPlayer.SingleInstance.6F1B0C2E", out bool createdNew);
        if (!createdNew)
        {
            if (declinedInstall)
                MessageBox.Show("Taskbar Player is already running.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Errors on the UI thread are logged and the app keeps running
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Fatal("UI thread", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task", e.Exception);
            e.SetObserved();
        };

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var app = new TrayApp();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal("Unhandled", e.ExceptionObject as Exception);
            app.EmergencyRestore();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => app.EmergencyRestore();

        Application.Run(app);
        GC.KeepAlive(mutex);
    }
}
