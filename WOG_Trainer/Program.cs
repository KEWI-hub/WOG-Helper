namespace WOG_Trainer;

internal static class Program
{
    private const string InstanceMutex = "WOGHelper_SingleInstance";
    internal const string ShowEventName = "WOGHelper_Show";

    [STAThread]
    static void Main()
    {
        // One instance only: a second launch (often while the first sits hidden in the tray)
        // would run its own automation against the same game. Ask the first to show itself.
        using var mutex = new Mutex(true, InstanceMutex, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* first is still starting */ }
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ShowFatal(ex);
        };

        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    static void ShowFatal(Exception ex)
    {
        var log = Path.Combine(AppContext.BaseDirectory, "WOG_Helper_crash.log");
        try { File.WriteAllText(log, ex + Environment.NewLine); }
        catch { /* ignore */ }
        MessageBox.Show(ex.Message + "\n\nDetails written to:\n" + log, "WOG Helper - error",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
