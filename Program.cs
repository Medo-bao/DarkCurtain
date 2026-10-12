using ScreenBlank;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A one-shot cleanup helper must bypass the normal single-instance activation.
        if (args.Length > 0 && args[0] == MemoryCleanupProcess.Argument)
            return MemoryCleanupProcess.RunHelper(args);

        using EventWaitHandle activate = new(false, EventResetMode.AutoReset, "ScreenBlank_Activate");
        using Mutex mutex = new(true, "ScreenBlank_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            IntPtr window = NativeMethods.FindWindow(null, "暗黑天幕");
            if (window != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(window, out uint processId);
                NativeMethods.AllowSetForegroundWindow(processId);
            }
            activate.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Settings settings = SettingsStore.Load();
        StartupManager.Apply(settings.AutoStart);
        using MainForm main = new(settings);
        // Consume activation on the UI thread, including requests received during startup.
        using System.Windows.Forms.Timer activationTimer = new() { Interval = 200 };
        activationTimer.Tick += (_, _) =>
        {
            if (activate.WaitOne(0)) main.ShowMainWindow();
        };
        activationTimer.Start();
        Application.Run(main);
        return 0;
    }
}
