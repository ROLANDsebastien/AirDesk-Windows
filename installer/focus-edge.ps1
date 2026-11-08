param([int]$ProcessId)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AirDeskTestFocus {
    public delegate bool VisitWindow(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(VisitWindow visit, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder name, int length);

    public static IntPtr FindFixture(uint expectedProcess) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, _) => {
            uint process;
            GetWindowThreadProcessId(window, out process);
            if (process != expectedProcess) return true;
            var name = new StringBuilder(512);
            GetWindowText(window, name, name.Capacity);
            if (!name.ToString().Contains("AirDesk fixture")) return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Bounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@
$window = [AirDeskTestFocus]::FindFixture([uint32]$ProcessId)
if ($window -eq [IntPtr]::Zero) { throw 'Edge window unavailable.' }
[AirDeskTestFocus]::keybd_event(18, 0, 0, [UIntPtr]::Zero)
[AirDeskTestFocus]::keybd_event(18, 0, 2, [UIntPtr]::Zero)
[void][AirDeskTestFocus]::SetForegroundWindow($window)
$bounds = [AirDeskTestFocus+Bounds]::new()
if ([AirDeskTestFocus]::GetWindowRect($window, [ref]$bounds)) {
    [void][AirDeskTestFocus]::SetCursorPos([int](($bounds.Left + $bounds.Right) / 2), [int](($bounds.Top + $bounds.Bottom) / 2))
    [AirDeskTestFocus]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [AirDeskTestFocus]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
Start-Sleep -Milliseconds 250
[uint32]$foreground = 0
[void][AirDeskTestFocus]::GetWindowThreadProcessId([AirDeskTestFocus]::GetForegroundWindow(), [ref]$foreground)
if ([AirDeskTestFocus]::GetForegroundWindow() -ne $window) { throw "Could not focus the Edge fixture window ($foreground vs $ProcessId)." }
