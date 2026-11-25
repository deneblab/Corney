using System;
using System.Linq;
using Corney.Tests.TestSupport;
using Xunit;

namespace Corney.Tests.Performance;

/// <summary>
///     CORNEY-014: job metrics count each job on its own, so a failing program is reported by name
///     (process.execution.&lt;program&gt;) and does not mark the other jobs of the minute as failed.
/// </summary>
public class JobMetricsTests
{
    private static DateTime At(int hour, int minute, int second = 0)
    {
        return new DateTime(2026, 11, 26, hour, minute, second);
    }

    [Fact]
    public void FailingJob_IsCountedPerJobAndPerProgram()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab("* * * * * missing.exe", "* * * * * job.exe");
        cron.FailingPrograms.Add("missing.exe");
        cron.Start();

        cron.RunUntil(At(10, 10, 30));

        var counters = cron.PerformanceMonitor.GenerateReport().Counters.ToDictionary(c => c.Name);

        Assert.DoesNotContain("cron.execution.batch_jobs", counters.Keys);

        var jobs = counters["cron.execution.job"];
        Assert.Equal(20, jobs.TotalExecutions);
        Assert.Equal(10, jobs.FailedExecutions); // was counted as success before

        Assert.Equal(10, counters["process.execution.missing"].FailedExecutions);
        Assert.Equal(0, counters["process.execution.job"].FailedExecutions);
        Assert.Equal(10, counters["process.execution.job"].SuccessfulExecutions);
    }
}
