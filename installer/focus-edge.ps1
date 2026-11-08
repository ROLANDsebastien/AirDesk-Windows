param([int]$ProcessId)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AirDeskTestFocus {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@
$window = (Get-Process -Id $ProcessId).MainWindowHandle
if ($window -eq [IntPtr]::Zero) { throw 'Edge window unavailable.' }
[AirDeskTestFocus]::keybd_event(18, 0, 0, [UIntPtr]::Zero)
[AirDeskTestFocus]::keybd_event(18, 0, 2, [UIntPtr]::Zero)
[void][AirDeskTestFocus]::SetForegroundWindow($window)
[AirDeskTestFocus]::mouse_event(1, 1, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 250
[uint32]$foreground = 0
[void][AirDeskTestFocus]::GetWindowThreadProcessId([AirDeskTestFocus]::GetForegroundWindow(), [ref]$foreground)
if ($foreground -ne $ProcessId) { throw "Could not focus Edge ($foreground vs $ProcessId)." }
