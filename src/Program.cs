using System.IO;

namespace AirDesk.Companion;

internal static class Program
{
    private const string InstanceName = @"Local\AirDeskCompanion.Windows";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool trayOnly = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            if (!trayOnly)
            {
                try { using var signal = EventWaitHandle.OpenExisting(InstanceName + ".Show"); signal.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            return;
        }

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirDesk Companion");
        try
        {
            using var companion = new Companion(new LocalData(root));
            using var window = new CompanionWindow(companion, show);
            window.Start(trayOnly);
            Application.Run();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            MessageBox.Show("AirDesk ne peut pas ouvrir ses données locales. Vérifiez le dossier et les droits d’accès.\n\n" + root,
                "AirDesk Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
