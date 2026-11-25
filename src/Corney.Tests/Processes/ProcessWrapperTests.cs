using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Corney.Core.Features.Cron.Models;
using Corney.Features.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Corney.Tests.Processes;

/// <summary>
///     CORNEY-008: Corney starts processes like crontab (fire-and-forget) and handles .lnk shortcuts.
/// </summary>
public class ProcessWrapperTests
{
    private static ProcessWrapper CreateWrapper()
    {
        return new ProcessWrapper(NullLogger<ProcessWrapper>.Instance);
    }

    [Theory]
    [InlineData(@"C:\work\app\x\RobeNova.Updater.exe")]
    [InlineData(@"C:\work\app\heartbeat\Crontab.bat")]
    [InlineData(@"C:\work\app\x\run.CMD")]
    [InlineData("powershell")]
    public void CreateStartInfo_Executables_StartDirectlyWithoutWindow(string program)
    {
        var startInfo = ProcessWrapper.CreateStartInfo(new ExecuteItem { Program = program });

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Theory]
    [InlineData(@"C:\work\app\syrup-evolve\SyrupEvolve.lnk")]
    [InlineData(@"C:\work\app\syrup-evolve\SyrupEvolve.LNK")]
    [InlineData(@"C:\scripts\job.ps1")]
    public void CreateStartInfo_ShortcutsAndOtherFiles_UseShell(string program)
    {
        var startInfo = ProcessWrapper.CreateStartInfo(new ExecuteItem { Program = program });

        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_DoesNotRedirectOutput()
    {
        var startInfo = ProcessWrapper.CreateStartInfo(new ExecuteItem { Program = "app.exe" });

        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
    }

    [Fact]
    public void CreateStartInfo_PassesArgumentsAsWrittenInCrontab()
    {
        var item = Pharse.Tokenize2(
            @"C:\work\app\syrup-evolve\SyrupEvolve.lnk update -p simple-scraper,robe-nova --path ""C:\a  b\c""");

        var startInfo = ProcessWrapper.CreateStartInfo(item);

        Assert.Equal(@"C:\work\app\syrup-evolve\SyrupEvolve.lnk", startInfo.FileName);
        Assert.Equal(@"update -p simple-scraper,robe-nova --path ""C:\a  b\c""", startInfo.Arguments);
    }

    [Fact]
    public void CreateStartInfo_DefaultsWorkingDirectoryToProgramDirectory()
    {
        var program = Path.Combine(Path.GetTempPath(), "corney", "tool.exe");

        var startInfo = ProcessWrapper.CreateStartInfo(new ExecuteItem { Program = program });

        Assert.Equal(Path.GetDirectoryName(program), startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateStartInfo_KeepsExplicitWorkingDirectory()
    {
        var startInfo = ProcessWrapper.CreateStartInfo(
            new ExecuteItem { Program = "tool.exe", WorkingDirectory = "work-dir" });

        Assert.Equal("work-dir", startInfo.WorkingDirectory);
    }

    [Fact]
    public void Start_ReturnsWithoutWaitingForProcessToExit()
    {
        var item = OperatingSystem.IsWindows()
            ? new ExecuteItem { Program = "ping.exe", Arguments = "-n 30 127.0.0.1" }
            : new ExecuteItem { Program = "/bin/sleep", Arguments = "30" };

        var stopwatch = Stopwatch.StartNew();
        var processId = CreateWrapper().Start(item);
        stopwatch.Stop();

        Assert.NotNull(processId);
        using var process = Process.GetProcessById(processId.Value);
        try
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Start took {stopwatch.Elapsed}");
            Assert.False(process.HasExited);
        }
        finally
        {
            process.Kill(true);
        }
    }

    /// <summary>
    ///     Real .lnk on Windows. Run through scripts/test-lnk.ps1, which creates the shortcut
    ///     (test.lnk -> target.cmd writing its arguments to result.txt) and sets CORNEY_TEST_LNK.
    /// </summary>
    [WindowsShortcutFact]
    public void Start_Shortcut_RunsTargetWithArguments()
    {
        var shortcut = Environment.GetEnvironmentVariable(WindowsShortcutFactAttribute.EnvVar)!;
        var result = Path.Combine(Path.GetDirectoryName(shortcut)!, "result.txt");
        if (File.Exists(result))
            File.Delete(result);

        // Same shape as the SyrupEvolve crontab line, plus a quoted argument with a space.
        var item = Pharse.Tokenize2($"\"{shortcut}\" update -p simple-scraper,robe-nova \"a b\"");

        var stopwatch = Stopwatch.StartNew();
        CreateWrapper().Start(item);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Start took {stopwatch.Elapsed}");

        var text = WaitForText(result, "args=", TimeSpan.FromSeconds(15));

        Assert.Contains("args=update -p simple-scraper,robe-nova \"a b\"", text);
    }

    private static string WaitForText(string path, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    text = File.ReadAllText(path);
                    if (text.Contains(expected))
                        return text;
                }
            }
            catch (IOException)
            {
                // target.cmd is still writing
            }

            Thread.Sleep(200);
        }

        return text;
    }

    [Fact]
    public void Start_MissingProgram_ThrowsWithRealCause()
    {
        var item = new ExecuteItem
        {
            Program = Path.Combine(Path.GetTempPath(), "corney-missing-" + Guid.NewGuid().ToString("N") + ".exe")
        };

        Assert.Throws<Win32Exception>(() => CreateWrapper().Start(item));
    }
}

/// <summary>
///     Runs only on Windows with CORNEY_TEST_LNK set (scripts/test-lnk.ps1); skipped otherwise.
/// </summary>
public sealed class WindowsShortcutFactAttribute : FactAttribute
{
    public const string EnvVar = "CORNEY_TEST_LNK";

    public WindowsShortcutFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Set {EnvVar} to a test .lnk; run scripts/test-lnk.ps1";
    }
}
