param([string]$Executable = "$PSScriptRoot/../artifacts/win-x64/MusicLyrics.exe")

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class MusicLyricsSmoke
{
    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int size);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLong(IntPtr window, int index);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wp, IntPtr lp);

    public static IntPtr FindWindow(uint id, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process != id) return true;
            var text = new StringBuilder(128);
            GetWindowText(window, text, text.Capacity);
            if (text.ToString() != title) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Wait-For([scriptblock]$Check, [string]$Failure) {
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if (& $Check) { return }
        Start-Sleep -Milliseconds 100
    }
    throw $Failure
}

$exe = (Resolve-Path -LiteralPath $Executable).Path
$log = Join-Path (Split-Path -Parent $exe) ("smoke-" + [Guid]::NewGuid().ToString("N") + ".jsonl")
$foreground = [MusicLyricsSmoke]::GetForegroundWindow()
$app = Start-Process -FilePath $exe -ArgumentList ('--probe --probe-log "' + $log + '"') -WindowStyle Hidden -PassThru
try {
    $appId = $app.Id
    Wait-For {
        $window = [MusicLyricsSmoke]::FindWindow($appId, "MusicLyrics overlay")
        $window -ne [IntPtr]::Zero -and [MusicLyricsSmoke]::IsWindowVisible($window)
    } "The playback probe did not show its sample overlay."
    $overlay = [MusicLyricsSmoke]::FindWindow($appId, "MusicLyrics overlay")
    $messages = [MusicLyricsSmoke]::FindWindow($appId, "MusicLyrics messages")
    $styles = [MusicLyricsSmoke]::GetWindowLong($overlay, -20).ToInt64()
    $expected = 0x080800A8
    if (($styles -band $expected) -ne $expected) { throw "Overlay window style flags are missing." }
    if ($foreground -ne [IntPtr]::Zero -and [MusicLyricsSmoke]::GetForegroundWindow() -ne $foreground) {
        throw "Showing the probe changed foreground focus."
    }
    if (-not [MusicLyricsSmoke]::PostMessage($messages, 0x0312, [IntPtr]1, [IntPtr]0)) {
        throw "Could not deliver the hotkey message."
    }
    Wait-For { -not [MusicLyricsSmoke]::IsWindowVisible($overlay) } "Hotkey message did not hide the overlay."
    [void][MusicLyricsSmoke]::PostMessage($messages, 0x0312, [IntPtr]1, [IntPtr]0)
    Wait-For { [MusicLyricsSmoke]::IsWindowVisible($overlay) } "Hotkey message did not restore the overlay."
    Get-Content -LiteralPath $log
    "Passed Windows overlay smoke check."
}
finally {
    if (-not $app.HasExited) {
        Stop-Process -InputObject $app
        [void]$app.WaitForExit(5000)
    }
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
}
