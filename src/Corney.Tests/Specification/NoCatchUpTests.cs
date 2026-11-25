using System;
using Corney.Tests.TestSupport;
using Xunit;

namespace Corney.Tests.Specification;

/// <summary>
///     RULE 3: nothing is caught up. Corney schedules from a concrete moment on: the first full
///     minute after it starts (or reloads, or wakes up). Runs that fell before that moment,
///     during sleep or while stopped are skipped, never started late and never repeated.
/// </summary>
public class NoCatchUpTests
{
    private static DateTime At(int hour, int minute, int second = 0, int day = 26)
    {
        return new DateTime(2026, 11, day, hour, minute, second);
    }

    [Fact]
    public void AtStartup_TimesAlreadyPassedAreNotRun()
    {
        using var cron = new CronHarness(At(10, 7, 30));
        cron.AddCrontab("*/5 * * * * every5.exe", "0 10 * * * daily.exe");
        cron.Start();

        cron.RunUntil(At(10, 20, 30));

        Assert.Equal(new[] { At(10, 10), At(10, 15), At(10, 20) }, cron.StartTimes("every5.exe"));
        Assert.Empty(cron.StartTimes("daily.exe")); // 10:00 today was before the start
    }

    [Fact]
    public void AtStartup_TheCurrentMinuteIsNotRun_SchedulingStartsAtTheNextFullMinute()
    {
        using var cron = new CronHarness(At(10, 5, 20));
        cron.AddCrontab("5 10 * * * at1005.exe", "* * * * * every-minute.exe");
        cron.Start();

        cron.RunUntil(At(10, 8, 30));

        Assert.Empty(cron.StartTimes("at1005.exe"));
        Assert.Equal(new[] { At(10, 6), At(10, 7), At(10, 8) }, cron.StartTimes("every-minute.exe"));
    }

    [Fact]
    public void AfterSleep_MissedRunsAreSkipped_AndSchedulingContinuesFromWakeUp()
    {
        using var cron = new CronHarness(At(0, 0, 30));
        cron.AddCrontab("* * * * * every-minute.exe", "0 2 * * * at0200.exe");
        cron.Start();
        cron.RunUntil(At(0, 1, 30)); // 00:01 runs

        cron.SleepUntil(At(3, 32, 17)); // asleep 00:01:30 - 03:32:17
        cron.RunUntil(At(3, 35, 30));

        Assert.Equal(new[] { At(0, 1), At(3, 33), At(3, 34), At(3, 35) }, cron.StartTimes("every-minute.exe"));
        Assert.Empty(cron.StartTimes("at0200.exe")); // 02:00 fell into the sleep
    }

    [Fact]
    public void AfterStopAndStart_TheStoppedPeriodIsNotCaughtUp()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab("* * * * * job.exe");
        cron.Start();
        cron.RunUntil(At(10, 2, 30)); // 10:01, 10:02

        cron.Service.Stop();
        cron.RunUntil(At(10, 30, 10)); // stopped: nothing runs
        cron.Start();
        cron.RunUntil(At(10, 32, 30));

        Assert.Equal(new[] { At(10, 1), At(10, 2), At(10, 31), At(10, 32) }, cron.StartTimes("job.exe"));
    }

    [Fact]
    public void Reload_DoesNotRepeatTheCurrentMinute()
    {
        using var cron = new CronHarness(At(10, 4, 30));
        cron.AddCrontab("* * * * * job.exe");
        cron.Start();
        cron.RunUntil(At(10, 5, 30)); // 10:05 ran

        cron.Reload(); // crontab changed at 10:05:30
        cron.Reload(); // and again
        cron.RunUntil(At(10, 7, 30));

        Assert.Equal(new[] { At(10, 5), At(10, 6), At(10, 7) }, cron.StartTimes("job.exe"));
    }
}
