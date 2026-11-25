using System;
using System.Diagnostics;
using Corney.Core.Features.Cron.Models;
using Corney.Features.Processes;
using Corney.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Corney.Tests.Specification;

/// <summary>
///     RULE 2: processes are started fire-and-forget. Corney starts the process and moves on:
///     it never waits for it, never kills it, does not read its output, ignores its exit code
///     and never starts it again because of how it ended.
/// </summary>
public class FireAndForgetTests
{
    private static DateTime At(int hour, int minute, int second = 0)
    {
        return new DateTime(2026, 11, 26, hour, minute, second);
    }

    private static ExecuteItem LongRunning(int seconds)
    {
        return OperatingSystem.IsWindows()
            ? new ExecuteItem { Program = "ping.exe", Arguments = $"-n {seconds} 127.0.0.1" }
            : new ExecuteItem { Program = "/bin/sleep", Arguments = seconds.ToString() };
    }

    private static ExecuteItem FailingWithExitCode1()
    {
        return OperatingSystem.IsWindows()
            ? new ExecuteItem { Program = "cmd.exe", Arguments = "/c exit 1" }
            : new ExecuteItem { Program = "/bin/sh", Arguments = "-c \"exit 1\"" };
    }

    [Fact]
    public void Start_ReturnsImmediately_AndTheProcessKeepsRunning()
    {
        var stopwatch = Stopwatch.StartNew();
        var processId = new ProcessWrapper(NullLogger<ProcessWrapper>.Instance).Start(LongRunning(60));
        stopwatch.Stop();

        Assert.NotNull(processId);
        using var process = Process.GetProcessById(processId.Value);
        try
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Start waited {stopwatch.Elapsed}");
            Assert.False(process.WaitForExit(2000), "the process was stopped by Corney");
        }
        finally
        {
            process.Kill(true);
        }
    }

    [Fact]
    public void ExitCode_IsIgnored_AndTheProcessIsStartedOnlyOnce()
    {
        var starts = 0;
        Process started = null;
        var wrapper = new ProcessWrapper(NullLogger<ProcessWrapper>.Instance, null, info =>
        {
            starts++;
            started = Process.Start(info); // the test keeps its own handle to read the exit code
            return null;
        });

        wrapper.Start(FailingWithExitCode1()); // no exception for a process that fails

        Assert.True(started!.WaitForExit(10000));
        Assert.Equal(1, started.ExitCode);
        Assert.Equal(1, starts); // no retry
        started.Dispose();
    }

    [Fact]
    public void Output_IsNotCaptured()
    {
        var startInfo = ProcessWrapper.CreateStartInfo(new ExecuteItem { Program = "job.exe" });

        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
    }

    [Fact]
    public void JobThatCannotStart_DoesNotStopOtherJobsOrTheSchedule()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab("* * * * * missing.exe", "* * * * * job.exe");
        cron.FailingPrograms.Add("missing.exe");
        cron.Start();

        cron.RunUntil(At(10, 5, 30));

        Assert.Equal(5, cron.StartTimes("job.exe").Count);
    }
}
