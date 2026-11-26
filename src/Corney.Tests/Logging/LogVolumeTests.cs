using System;
using System.ComponentModel;
using System.IO;
using Corney.Common.Performance;
using Corney.Features.Cron;
using Corney.Features.Processes;
using Corney.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Corney.Tests.Logging;

/// <summary>
///     CORNEY-013: the log file (Debug and up) shows what happened, not that a minute passed.
///     Idle minutes write nothing; each started job writes one line from CronService (per tick)
///     and one from ProcessWrapper.
/// </summary>
public class LogVolumeTests
{
    private static DateTime At(int hour, int minute, int second = 0)
    {
        return new DateTime(2026, 11, 26, hour, minute, second);
    }

    [Fact]
    public void IdleHour_WritesNothingAtDebugOrAbove()
    {
        var cronLog = new CountingLogger<CronService>();
        using var cron = new CronHarness(At(10, 0, 30), cronLog);
        cron.AddCrontab("0 3 * * * nightly.exe");
        cron.Start();
        cronLog.Reset(); // startup lines are wanted

        cron.RunUntil(At(11, 0, 30));

        Assert.Equal(60, cronLog.Count(Corney.Common.Logging.LogMessages.CronExecutionStarted)); // 60 ticks happened
        Assert.Equal(0, cronLog.CountAtLeast(LogLevel.Debug));
    }

    [Fact]
    public void TicksWithJobs_WriteOneLinePerTickAndOnePerJob()
    {
        var cronLog = new CountingLogger<CronService>();
        var processLog = new CountingLogger<ProcessWrapper>();
        using var cron = new CronHarness(At(10, 0, 30), cronLog, processLog);
        cron.AddCrontab("* * * * * a.exe", "* * * * * b.exe");
        cron.Start();
        cronLog.Reset();

        cron.RunUntil(At(10, 10, 30));

        Assert.Equal(10, cronLog.CountAtLeast(LogLevel.Debug)); // "Executing cron jobs ... with 2 items"
        Assert.Equal(20, processLog.CountAtLeast(LogLevel.Debug)); // "Detached process started ..."
    }

    [Fact]
    public void MissingProgram_WritesOneErrorLineWithoutStackTrace()
    {
        var cronLog = new CountingLogger<CronService>();
        using var cron = new CronHarness(At(10, 0, 30), cronLog);
        cron.FailingPrograms.Add("missing.exe");
        cron.AddCrontab("* * * * * missing.exe --x");
        cron.Start();
        cronLog.Reset();

        cron.RunUntil(At(10, 3, 30));

        Assert.Equal(3, cronLog.CountAtLeast(LogLevel.Error));
        Assert.Equal(0, cronLog.WithException);
    }

    [Theory]
    [InlineData(typeof(Win32Exception), true)]
    [InlineData(typeof(FileNotFoundException), true)]
    [InlineData(typeof(DirectoryNotFoundException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(NullReferenceException), false)]
    public void OnlyStartFailures_AreLoggedWithoutStackTrace(Type exceptionType, bool expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType);

        Assert.Equal(expected, CronService.IsStartFailure(exception));
    }

    [Theory]
    [InlineData(true, 10, LogLevel.Trace)]
    [InlineData(false, 10, LogLevel.Debug)]
    [InlineData(true, 6000, LogLevel.Debug)]
    public void Metrics_AreDebugOnlyWhenFailedOrSlow(bool success, int durationMs, LogLevel expected)
    {
        var metric = new PerformanceMetrics
        {
            OperationName = "cron.execution.batch",
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Success = success
        };

        Assert.Equal(expected, PerformanceMonitor.MetricLogLevel(metric));
    }
}
