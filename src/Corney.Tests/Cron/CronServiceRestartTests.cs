using System;
using System.Linq;
using System.Reactive.Concurrency;
using Corney.Common.Logging;
using Corney.Common.Performance;
using Corney.Common.SimpleEnv;
using Corney.Features.App;
using Corney.Features.Cron;
using Corney.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Corney.Tests.Cron;

/// <summary>
///     CORNEY-007: a crontab reload (Restart) must not leave the previous schedule running.
///     Ticks are counted by the CronExecutionStarted log event ("Executing cron jobs ..."),
///     the same line that showed 180 ticks/h instead of 60 on SCRAPER-21.
/// </summary>
public class CronServiceRestartTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 15, 32, 30, TimeSpan.Zero);

    private readonly CountingLogger<CronService> _log = new();
    private readonly PerformanceMonitor _performanceMonitor = new(NullLogger<PerformanceMonitor>.Instance);
    private readonly HistoricalScheduler _scheduler = new(T0);
    private readonly CronService _service;

    public CronServiceRestartTests()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        _service = new CronService(_log, provider, CreateRegistry(), _performanceMonitor, _scheduler);
    }

    public void Dispose()
    {
        _service.Dispose();
        _performanceMonitor.Dispose();
    }

    private int Ticks => _log.Count(LogMessages.CronExecutionStarted);

    [Fact]
    public void Start_TicksOncePerMinute()
    {
        _service.Start([]);

        _scheduler.AdvanceBy(TimeSpan.FromMinutes(10));

        Assert.Equal(10, Ticks);
    }

    [Fact]
    public void Restart_DoesNotDuplicateSchedule()
    {
        _service.Start([]);
        _scheduler.AdvanceBy(TimeSpan.FromMinutes(1)); // 15:33:30

        // Same pattern as SCRAPER-21 on 2026-11-25: two reloads 17 s apart.
        _service.Restart([]);
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(17));
        _service.Restart([]);

        var before = Ticks;
        _scheduler.AdvanceBy(TimeSpan.FromMinutes(60));

        Assert.Equal(60, Ticks - before);
    }

    [Fact]
    public void ManyRestarts_KeepSingleSchedule()
    {
        _service.Start([]);
        for (var i = 0; i < 10; i++)
        {
            _scheduler.AdvanceBy(TimeSpan.FromSeconds(20));
            _service.Restart([]);
        }

        var before = Ticks;
        _scheduler.AdvanceBy(TimeSpan.FromMinutes(5));

        Assert.Equal(5, Ticks - before);
    }

    [Fact]
    public void Stop_StopsTicking()
    {
        _service.Start([]);
        _scheduler.AdvanceBy(TimeSpan.FromMinutes(2));

        _service.Stop();
        var before = Ticks;
        _scheduler.AdvanceBy(TimeSpan.FromMinutes(5));

        Assert.Equal(before, Ticks);
    }

    [Fact]
    public void CronService_IsSingletonInContainer()
    {
        // Was Transient: every CrontabFileIsChanged built a new CronHandlers with a new CronService,
        // whose Restart could not stop the schedule of the previous instance.
        var services = new ServiceCollection();
        services.AddCorneyCore(CreateRegistry());

        var descriptor = services.Single(d => d.ServiceType == typeof(ICronService));

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private static CorneyRegistry CreateRegistry()
    {
        return new CorneyRegistry(new SimpleEnvResult(), "config.json", new CorneyConfig());
    }
}
