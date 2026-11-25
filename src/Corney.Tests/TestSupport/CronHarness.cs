using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using Corney.Common.Performance;
using Corney.Common.SimpleEnv;
using Corney.Features.App;
using Corney.Features.Cron;
using Corney.Features.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Corney.Tests.TestSupport;

/// <summary>
///     Runs a real CronService on virtual local time (HistoricalScheduler) with real crontab files.
///     Processes are not started: every start is recorded with its virtual local time.
/// </summary>
internal sealed class CronHarness : IDisposable
{
    private readonly List<string> _crontabFiles = new();
    private readonly PerformanceMonitor _performanceMonitor = new(NullLogger<PerformanceMonitor>.Instance);
    private readonly ServiceProvider _provider;
    private readonly List<JobStart> _starts = new();
    private readonly string _tempDir;

    public CronHarness(DateTime localStart, ILogger<CronService> cronLog = null,
        ILogger<ProcessWrapper> processLog = null)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "corney-spec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        Scheduler = new HistoricalScheduler(ToLocalOffset(localStart));

        _provider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(sp => new CrontabFileParser(NullLogger<CrontabFileParser>.Instance, sp))
            .AddTransient(_ => new ProcessWrapper(processLog ?? NullLogger<ProcessWrapper>.Instance, null, RecordStart))
            .BuildServiceProvider();

        var registry = new CorneyRegistry(new SimpleEnvResult(), "config.json", new CorneyConfig());
        Service = new CronService(cronLog ?? NullLogger<CronService>.Instance, _provider, registry, _performanceMonitor,
            Scheduler);
    }

    public HistoricalScheduler Scheduler { get; }
    public CronService Service { get; }

    /// <summary>Programs for which starting throws (like a missing executable).</summary>
    public HashSet<string> FailingPrograms { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime LocalNow => Scheduler.Now.ToLocalTime().DateTime;

    public IReadOnlyList<JobStart> Starts
    {
        get
        {
            lock (_starts)
            {
                return _starts.ToList();
            }
        }
    }

    public void Dispose()
    {
        Service.Dispose();
        _performanceMonitor.Dispose();
        _provider.Dispose();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Creates a crontab file (one more file each call) and returns its path.</summary>
    public string AddCrontab(params string[] lines)
    {
        var path = Path.Combine(_tempDir, $"crontab{_crontabFiles.Count + 1}.txt");
        File.WriteAllLines(path, lines);
        _crontabFiles.Add(path);
        return path;
    }

    public void RewriteCrontab(string path, params string[] lines)
    {
        File.WriteAllLines(path, lines);
    }

    public void Start()
    {
        Service.Start(_crontabFiles.ToArray());
    }

    /// <summary>What Corney does when a crontab file changes.</summary>
    public void Reload()
    {
        Service.Restart(_crontabFiles.ToArray());
    }

    /// <summary>Lets time pass normally until the given local time; timers fire on time.</summary>
    public void RunUntil(DateTime local)
    {
        Scheduler.AdvanceTo(ToLocalOffset(local));
    }

    /// <summary>The machine sleeps: the clock moves to the given local time, no timer fires.</summary>
    public void SleepUntil(DateTime local)
    {
        Scheduler.Sleep(ToLocalOffset(local) - Scheduler.Now);
    }

    public IReadOnlyList<DateTime> StartTimes(string program)
    {
        return Starts.Where(s => string.Equals(s.Program, program, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.LocalTime)
            .ToList();
    }

    private Process RecordStart(ProcessStartInfo startInfo)
    {
        if (FailingPrograms.Contains(startInfo.FileName))
            throw new FileNotFoundException("Program not found", startInfo.FileName);

        lock (_starts)
        {
            _starts.Add(new JobStart(LocalNow, startInfo.FileName, startInfo.Arguments));
        }

        return null;
    }

    private static DateTimeOffset ToLocalOffset(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    internal sealed record JobStart(DateTime LocalTime, string Program, string Arguments);
}
