using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AirDesk.Companion;

internal sealed record Foreground(IntPtr Window, string Process, string Id, string Name);
internal sealed record Website(string Domain, string? Url);
internal sealed record Battery(int Percent, bool Charging, bool ExternalPower);

internal sealed record InstalledApp(string BundleID, string Name, string? Version);
internal sealed record WindowsInventory(DateTimeOffset CollectedAt, [property: System.Text.Json.Serialization.JsonPropertyName("macOSVersion")] string OsVersion, InstalledApp[] Applications);

internal static partial class WindowsTelemetry
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Tick; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint bytes, out uint needed);
    [DllImport("kernel32.dll")] private static extern bool QueryUnbiasedInterruptTime(out ulong ticks);
    [DllImport("user32.dll")] internal static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, in Guid setting, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("wtsapi32.dll")] internal static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(IntPtr window);

    private delegate bool WindowVisitor(IntPtr window, IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visit, IntPtr context);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);

    internal static readonly Guid SessionDisplayStatus = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    public static DateTimeOffset BootAt { get; } = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static double? AwakeSeconds() =>
        QueryUnbiasedInterruptTime(out var ticks) ? ticks / (double)TimeSpan.TicksPerSecond : null;

    public static double? IdleSeconds()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Tick) / 1000d : null;
    }

    public static bool IsInputDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(128);
            return GetUserObjectInformation(desktop, 2, name, (uint)(name.Capacity * sizeof(char)), out _) &&
                name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    public static Foreground? ReadForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var id) == 0 || id == (uint)Environment.ProcessId)
            return null;
        try
        {
            using var process = Process.GetProcessById((int)id);
            var name = process.ProcessName;
            var appId = "windows." + InvalidAppId().Replace(name.ToLowerInvariant(), "_");
            string? label = null;
            try { label = process.MainModule?.FileVersionInfo.FileDescription; }
            catch (System.ComponentModel.Win32Exception) { }
            return new(window, name, Clip(appId, 200), Clip(Clean(label) ?? name, 100));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static bool IsSupportedBrowser(Foreground app) =>
        app.Process.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
        app.Process.Equals("chrome", StringComparison.OrdinalIgnoreCase);

    public static bool HasSingleBrowserWindow(Foreground browser)
    {
        int count = 0;
        bool foundForeground = false;
        var name = new StringBuilder(64);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window) ||
                GetClassName(window, name, name.Capacity) == 0 || name.ToString() != "Chrome_WidgetWin_1" ||
                GetWindowThreadProcessId(window, out var id) == 0) return true;
            try
            {
                using var process = Process.GetProcessById((int)id);
                if (process.ProcessName.Equals(browser.Process, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    foundForeground |= window == browser.Window;
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { count = 2; }
            return count < 2;
        }, IntPtr.Zero);
        return count == 1 && foundForeground;
    }

    public static bool MatchesBrowserTitle(Foreground browser, string? tabTitle)
    {
        if (string.IsNullOrWhiteSpace(tabTitle) || tabTitle.Length > 500 ||
            GetForegroundWindow() != browser.Window || !HasSingleBrowserWindow(browser)) return false;
        var caption = new StringBuilder(1024);
        if (GetWindowText(browser.Window, caption, caption.Capacity) == 0) return false;
        var title = caption.ToString();
        // Only corroborate the active browser frame. Never retain its window title.
        return title == tabTitle || title.StartsWith(tabTitle + " - ", StringComparison.Ordinal) ||
            title.StartsWith(tabTitle + " – ", StringComparison.Ordinal);
    }

    public static Website? ParseWebsite(string? value, bool fullLinks, string[] excluded)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsWhiteSpace)) return null;
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (value.Contains(':')) return null;
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.UserInfo)) return null;
        string? domain = NormalizeDomain(uri.IdnHost);
        if (domain is null || excluded.Any(item => domain == item || domain.EndsWith("." + item, StringComparison.Ordinal)))
            return null;
        var address = new UriBuilder(uri) { Host = domain }.Uri.AbsoluteUri;
        return new(domain, fullLinks && Encoding.UTF8.GetByteCount(address) <= 8192 ? address : null);
    }

    public static string? NormalizeDomain(string value)
    {
        try
        {
            var domain = new IdnMapping().GetAscii(value.Trim().ToLowerInvariant());
            return domain.Length <= 253 && DomainPattern().IsMatch(domain) ? domain : null;
        }
        catch (ArgumentException) { return null; }
    }

    public static Battery? ReadBattery()
    {
        var status = SystemInformation.PowerStatus;
        var flags = status.BatteryChargeStatus;
        if (flags == BatteryChargeStatus.Unknown || flags.HasFlag(BatteryChargeStatus.NoSystemBattery) ||
            status.PowerLineStatus == PowerLineStatus.Unknown || status.BatteryLifePercent is < 0 or > 1) return null;
        var pluggedIn = status.PowerLineStatus == PowerLineStatus.Online;
        return new((int)Math.Round(status.BatteryLifePercent * 100), pluggedIn && flags.HasFlag(BatteryChargeStatus.Charging), pluggedIn);
    }

    public static byte[] Screenshot()
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("Aucun écran disponible.");
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

        var scale = Math.Min(1d, 1920d / Math.Max(bounds.Width, bounds.Height));
        using var image = new Bitmap(source, new Size(Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale))));
        using var buffer = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
        image.Save(buffer, ImageCodecInfo.GetImageEncoders().Single(item => item.FormatID == ImageFormat.Jpeg.Guid), parameters);
        if (buffer.Length > 2_000_000) throw new InvalidOperationException("Capture trop volumineuse.");
        return buffer.ToArray();
    }

    public static WindowsInventory Inventory()
    {
        var applications = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var key in uninstall?.GetSubKeyNames() ?? [])
            {
                using var entry = uninstall?.OpenSubKey(key);
                var name = Clean(entry?.GetValue("DisplayName") as string);
                if (name is null || !InventoryName().IsMatch(name)) continue;
                var id = Clip("windows." + InvalidAppId().Replace(name.ToLowerInvariant(), "_"), 200);
                var version = Clean(entry?.GetValue("DisplayVersion") as string);
                applications.TryAdd(id, new(id, Clip(name, 100), version is null ? null : Clip(version, 100)));
            }
        }

        // The existing supervisor decodes the OS string using this wire key.
        return new(DateTimeOffset.UtcNow, "Windows " + Environment.OSVersion.Version, applications.Values.Take(10).ToArray());
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
    private static string? Clean(string? value)
    {
        var cleaned = value?.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    [GeneratedRegex("[^a-z0-9._-]")] private static partial Regex InvalidAppId();
    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DomainPattern();
    [GeneratedRegex(@"^(Microsoft (365|Office|Word|Excel)|Horus|(?:Omnissa|VMware) Horizon|Google Chrome|Microsoft Edge)", RegexOptions.IgnoreCase)]
    private static partial Regex InventoryName();
}
