using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
