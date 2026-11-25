using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Threading;
using Corney.Common.Logging;
using Corney.Common.Performance;
using Corney.Common.SimpleEnv;
using Corney.Features.App;
using Corney.Features.Cron;
using Corney.Features.Processes;
using Corney.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Corney.Tests.Cron;

/// <summary>
///     CORNEY-011: after sleep (or a suspended VM / clock jump) the scheduler must not replay every
///     missed minute. Seen locally on 2026-11-26: 211 ticks in 0.3 s after waking up at 03:32.
/// </summary>
public sealed class CronServiceSleepTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 0, 0, 30, TimeSpan.Zero);

    private readonly string _crontab;
    private readonly CountingLogger<CronService> _log = new();
    private readonly PerformanceMonitor _performanceMonitor = new(NullLogger<PerformanceMonitor>.Instance);
    private readonly ServiceProvider _provider;
    private readonly HistoricalScheduler _scheduler = new(T0);
    private readonly CronService _service;
    private int _jobStarts;

    public CronServiceSleepTests()
    {
        _crontab = Path.Combine(Path.GetTempPath(), "corney-sleep-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(_crontab, "* * * * * job.exe --every-minute" + Environment.NewLine);

        // Jobs are counted instead of started.
        _provider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<CrontabFileParser>(sp =>
                new CrontabFileParser(NullLogger<CrontabFileParser>.Instance, sp))
            .AddTransient(_ => new ProcessWrapper(NullLogger<ProcessWrapper>.Instance, null, _ =>
            {
                Interlocked.Increment(ref _jobStarts);
                return null;
            }))
            .BuildServiceProvider();

        var registry = new CorneyRegistry(new SimpleEnvResult(), "config.json", new CorneyConfig());
        _service = new CronService(_log, _provider, registry, _performanceMonitor, _scheduler);
    }

    public void Dispose()
    {
        _service.Dispose();
        _performanceMonitor.Dispose();
        _provider.Dispose();
        File.Delete(_crontab);
    }

    private int Ticks => _log.Count(LogMessages.CronExecutionStarted);
    private int LateTicks => _log.Count(LogMessages.CronTickLate);

    [Fact]
    public void NormalRun_StartsJobEveryMinute_NoLateTicks()
    {
        _service.Start([_crontab]);

        _scheduler.AdvanceBy(TimeSpan.FromMinutes(60));

        Assert.Equal(60, Ticks);
        Assert.Equal(60, _jobStarts);
        Assert.Equal(0, LateTicks);
    }

    [Fact]
    public void AfterSleep_DoesNotReplayMissedMinutes()
    {
        _service.Start([_crontab]);
        _scheduler.AdvanceBy(TimeSpan.FromMinutes(2)); // 00:01, 00:02
        Assert.Equal(2, _jobStarts);

        // Machine sleeps 3.5 h: the clock moves on, no timer fires.
        _scheduler.Sleep(TimeSpan.FromMinutes(210));
        var ticksBefore = Ticks;
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(1)); // wake up at 03:32:31

        Assert.Equal(1, Ticks - ticksBefore); // only the overdue tick, no burst of 210
        Assert.Equal(1, LateTicks);
        Assert.Equal(2, _jobStarts); // the overdue job is skipped, like cron

        // Back to one tick and one job per minute from 03:33.
        _scheduler.AdvanceBy(TimeSpan.FromMinutes(10));

        Assert.Equal(11, Ticks - ticksBefore);
        Assert.Equal(12, _jobStarts);
        Assert.Equal(1, LateTicks);
    }

    [Fact]
    public void SlightlyLateTick_StillRunsItsJobs()
    {
        _service.Start([_crontab]);
        _scheduler.AdvanceBy(TimeSpan.FromMinutes(1)); // 00:01

        // Tick due at 00:02 fires 40 s late (e.g. a busy machine): below the limit, jobs still run.
        _scheduler.Sleep(TimeSpan.FromSeconds(70));
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(1));

        Assert.Equal(2, _jobStarts);
        Assert.Equal(0, LateTicks);
    }
}
