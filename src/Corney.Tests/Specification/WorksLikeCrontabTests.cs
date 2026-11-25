using System;
using System.Linq;
using Corney.Tests.TestSupport;
using Xunit;

namespace Corney.Tests.Specification;

/// <summary>
///     RULE 1: Corney works like crontab. Each job runs exactly at the minutes its cron expression
///     matches (local time), exactly once per matching minute, from all configured crontab files.
/// </summary>
public class WorksLikeCrontabTests
{
    private static DateTime At(int hour, int minute, int second = 0, int day = 26)
    {
        return new DateTime(2026, 11, day, hour, minute, second);
    }

    [Fact]
    public void EveryMinuteJob_RunsExactlyOncePerMinute()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab("* * * * * job.exe");
        cron.Start();

        cron.RunUntil(At(11, 0, 30));

        var expected = Enumerable.Range(1, 60).Select(m => At(10, 0).AddMinutes(m));
        Assert.Equal(expected, cron.StartTimes("job.exe"));
    }

    [Fact]
    public void StepExpression_RunsOnlyOnMatchingMinutes()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab("*/15 * * * * job.exe");
        cron.Start();

        cron.RunUntil(At(11, 0, 30));

        Assert.Equal(new[] { At(10, 15), At(10, 30), At(10, 45), At(11, 0) }, cron.StartTimes("job.exe"));
    }

    [Fact]
    public void FixedTime_RunsOnceADayAtThatLocalTime()
    {
        using var cron = new CronHarness(At(10, 0));
        cron.AddCrontab("30 2 * * * nightly.exe");
        cron.Start();

        cron.RunUntil(At(10, 0, day: 29));

        Assert.Equal(new[] { At(2, 30, day: 27), At(2, 30, day: 28), At(2, 30, day: 29) },
            cron.StartTimes("nightly.exe"));
    }

    [Fact]
    public void AllCrontabFiles_AreUsedTogether()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab("* * * * * a.exe");
        cron.AddCrontab("*/2 * * * * b.exe");
        cron.Start();

        cron.RunUntil(At(10, 10, 30));

        Assert.Equal(10, cron.StartTimes("a.exe").Count);
        Assert.Equal(5, cron.StartTimes("b.exe").Count);
    }

    [Fact]
    public void JobsDueInTheSameMinute_AllStartInThatMinute()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab("5 10 * * * a.exe", "5 10 * * * b.exe", "*/5 * * * * c.exe");
        cron.Start();

        cron.RunUntil(At(10, 5, 30));

        Assert.All(cron.Starts, s => Assert.Equal(At(10, 5), s.LocalTime));
        Assert.Equal(new[] { "a.exe", "b.exe", "c.exe" }, cron.Starts.Select(s => s.Program).OrderBy(p => p));
    }

    [Fact]
    public void CommentsAndBlankLines_AreIgnored()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab("# comment", "", "   ", "  # indented comment", "* * * * * job.exe");
        cron.Start();

        cron.RunUntil(At(10, 3, 30));

        Assert.Equal(3, cron.Starts.Count);
        Assert.All(cron.Starts, s => Assert.Equal("job.exe", s.Program));
    }

    [Fact]
    public void CommandAndArguments_ArePassedAsWritten()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        cron.AddCrontab(@"* * * * * ""C:\app dir\job.exe"" update -p a,b --name ""x  y""");
        cron.Start();

        cron.RunUntil(At(10, 1, 30));

        var start = Assert.Single(cron.Starts);
        Assert.Equal(@"C:\app dir\job.exe", start.Program);
        Assert.Equal(@"update -p a,b --name ""x  y""", start.Arguments);
    }

    [Fact]
    public void ChangedCrontab_TakesEffectFromTheNextMinute()
    {
        using var cron = new CronHarness(At(10, 0, 30));
        var file = cron.AddCrontab("* * * * * old.exe");
        cron.Start();
        cron.RunUntil(At(10, 5, 30));

        cron.RewriteCrontab(file, "* * * * * new.exe");
        cron.Reload();
        cron.RunUntil(At(10, 10, 30));

        Assert.Equal(At(10, 5), cron.StartTimes("old.exe").Last());
        Assert.Equal(At(10, 6), cron.StartTimes("new.exe").First());
        Assert.Equal(5, cron.StartTimes("new.exe").Count);
    }
}
