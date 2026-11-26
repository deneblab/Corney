#r "System.IO"

using System.Diagnostics;
using System.IO;

// Runs BEFORE SyrupEvolve swaps app\Corney (SyrupEvolve with issue #2): stops corney so the folder can be renamed.
// If corney is still running, SyrupEvolve retries the rename, then rolls back and runs after-make-current.csx
// of the active version, which starts corney again.

Console.WriteLine("Execute file: before-make-current.csx");

string appName = "Corney";
// Syrup.CurrentAppPath = app\Corney.<version> (the new version) -> its parent is app
string appDirPath = Directory.GetParent(Syrup.CurrentAppPath).FullName;
string activeDirPath = Path.Combine(appDirPath, appName) + Path.DirectorySeparatorChar;

foreach (var process in Process.GetProcessesByName(appName))
{
    using (process)
    {
        string exePath = null;
        try
        {
            exePath = process.MainModule?.FileName;
        }
        catch (Exception e)
        {
            // another user's corney (access denied) is not ours to stop
            Console.WriteLine($"Skip process {process.Id}: {e.Message}");
            continue;
        }

        if (exePath == null || !exePath.StartsWith(activeDirPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Skip process {process.Id}: not started from {activeDirPath} ({exePath})");
            continue;
        }

        try
        {
            Console.WriteLine($"Stop process {process.Id}: {exePath}");
            process.Kill();
            if (!process.WaitForExit(10000))
                Console.WriteLine($"Process {process.Id} did not exit within 10 s");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Cannot stop process {process.Id}: {e.Message}");
        }
    }
}
