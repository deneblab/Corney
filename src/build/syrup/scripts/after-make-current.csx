#r "System.IO"
#r "Microsoft.Win32.Registry"

using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

// Runs after SyrupEvolve swapped app\Corney, and also after a failed swap (SyrupEvolve with issue #2), then from the
// still active version: sets the autostart and starts corney unless it is already running.

Console.WriteLine("Execute file: after-make-current.csx");

string appName = "Corney";
// Syrup.CurrentAppPath = app\Corney (the active copy) -> its parent is app
string appDirPath = Directory.GetParent(Syrup.CurrentAppPath).FullName;
string corneyDir = Path.Combine(appDirPath, appName, "main", appName);
string corneyExe = Path.Combine(corneyDir, "Corney.exe");

try
{
    using (var rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
    {
        rk?.SetValue(appName, $"\"{corneyExe}\"");
    }
}
catch (Exception e)
{
    // still start corney below; it only would not start at the next logon
    Console.WriteLine($"Cannot set the autostart value: {e.Message}");
}

bool IsActiveCorney(Process process)
{
    using (process)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, corneyExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false; // another user's process
        }
    }
}

if (Process.GetProcessesByName(appName).Any(IsActiveCorney))
{
    Console.WriteLine($"Corney is already running: {corneyExe}");
}
else
{
    Console.WriteLine($"Start {corneyExe}");
    var process = Process.Start(new ProcessStartInfo(corneyExe) { WorkingDirectory = corneyDir });
    process?.Dispose();
}
