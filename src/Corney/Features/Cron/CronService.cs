using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using Corney.Common.Extensions;
using Corney.Common.Logging;
using Corney.Common.Performance;
using Corney.Features.App;
using Corney.Features.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Corney.Features.Cron;

/// <summary>
///     Core cron scheduling service that manages crontab files and executes scheduled tasks.
///     This service monitors configuration changes, parses cron expressions, and ensures
///     tasks are executed at their scheduled times with proper error handling and logging.
/// </summary>
/// <remarks>
///     The CronService uses a ReaderWriterLockSlim for thread-safe operations and integrates
///     with the performance monitoring system to track execution metrics and performance.
/// </remarks>
public class CronService : ICronService, IDisposable
{
    private readonly CorneyRegistry _corneyRegistry;

    private readonly Dictionary<string, List<CronDefinition>> _cronDefinitions = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<CronDefinition> _itemsToRunOnNextMinute = new();

    private readonly ILogger<CronService> _log;
    private readonly PerformanceMonitor _performanceMonitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly ReaderWriterLockSlim _stateLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly IScheduler _scheduler;

    // Guarded by _stateLock (write). Every Start/Restart/Stop bumps the generation; a schedule chain
    // started for an older generation stops at its next tick, so only one chain is ever active.
    private long _generation;
    private IDisposable _nextSchedule;

    public CronService(ILogger<CronService> log, IServiceProvider serviceProvider, CorneyRegistry corneyRegistry,
        PerformanceMonitor performanceMonitor)
        : this(log, serviceProvider, corneyRegistry, performanceMonitor, Scheduler.Default)
    {
    }

    internal CronService(ILogger<CronService> log, IServiceProvider serviceProvider, CorneyRegistry corneyRegistry,
        PerformanceMonitor performanceMonitor, IScheduler scheduler)
    {
        _log = log;
        _serviceProvider = serviceProvider;
        _corneyRegistry = corneyRegistry;
        _performanceMonitor = performanceMonitor;
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    /// <summary>
    ///     Starts the cron service with the specified crontab files.
    ///     This method initializes the service, parses all cron definitions, and begins scheduling.
    /// </summary>
    /// <param name="crontabFiles">Array of crontab file paths to monitor and execute</param>
    /// <exception cref="ArgumentNullException">Thrown when crontabFiles is null</exception>
    /// <exception cref="FileNotFoundException">Thrown when a crontab file cannot be found</exception>
    public void Start(string[] crontabFiles)
    {
        _log.LogInformation(LogMessages.CronServiceStarted, LogMessages.CronServiceStartedTemplate,
            _corneyRegistry.AppVersion.SemVer);
        InitWork(crontabFiles);
    }

    /// <summary>
    ///     Restarts the cron service with updated crontab files.
    ///     This method stops current scheduling, disposes resources, and reinitializes with new files.
    /// </summary>
    /// <param name="cronFiles">Array of updated crontab file paths to monitor</param>
    /// <remarks>
    ///     This method is typically called when configuration changes are detected,
    ///     allowing the service to pick up new or modified cron definitions without
    ///     requiring a full application restart.
    /// </remarks>
    public void Restart(string[] cronFiles)
    {
        _log.LogInformation(LogMessages.CronServiceRestarted, LogMessages.CronServiceRestartedTemplate,
            _corneyRegistry.AppVersion.SemVer);
        InitWork(cronFiles);
    }

    public void Stop()
    {
        _log.LogInformation(LogMessages.CronServiceStopped, LogMessages.CronServiceStoppedTemplate);
        if (!_stateLock.TryEnterWriteLock(TimeSpan.FromSeconds(30)))
        {
            _log.LogError(LogMessages.CronLockTimeout, LogMessages.CronLockTimeoutTemplate, "write", "Stop");
            return;
        }

        try
        {
            CancelSchedule();
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        _log.LogDebug("Disposing CronService");
        Stop();
        _stateLock?.Dispose();
    }

    /// <summary>
    ///     Invalidates the active schedule chain and returns the new generation. Caller holds the write lock.
    /// </summary>
    private long CancelSchedule()
    {
        _generation++;
        _nextSchedule?.Dispose();
        _nextSchedule = null;
        return _generation;
    }

    private void InitWork(string[] cronFiles)
    {
        if (!_stateLock.TryEnterWriteLock(TimeSpan.FromSeconds(30)))
        {
            _log.LogError(LogMessages.CronLockTimeout, LogMessages.CronLockTimeoutTemplate, "write", "InitWork");
            return;
        }

        try
        {
            var generation = CancelSchedule();
            var marker = Guid.NewGuid();
            CreateListDefinitions(cronFiles);
            var start = _scheduler.Now.UtcDateTime;
            var startDown = start.RoundDown(TimeSpan.FromSeconds(60));
            var next = startDown.AddMinutes(1);
            _log.LogInformation("Cron will be processing items from local time {NextExecutionTime}",
                next.ToLocalTime());
            WriteTasksToLog();
            _log.LogDebug("*** New round  ***");
            _log.LogDebug("InitWork started with marker {Marker} (generation {Generation})", marker, generation);
            GenerateNext(next, marker);
            ScheduleNext(next, marker, generation);
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }
    }


    private void WriteTasksToLog()
    {
        var list = new List<(string, DateTimeOffset)>();
        var from = DateTimeOffset.Now;
        var to = DateTimeOffset.Now.AddHours(24);
        const int showNumberJobs = 2;
        _log.LogDebug(
            "Displaying first {JobCount} tasks from each crontab file in range {FromTime} to {ToTime}",
            showNumberJobs, from, to);

        try
        {
            _log.LogDebug("Collecting definitions to list");

            var definitions = _cronDefinitions.Values.SelectMany(x => x).ToList();

            _log.LogDebug("Iterating {DefinitionCount} cron definitions", definitions.Count);

            foreach (var cronDefinition in definitions)
            {
                _log.LogDebug("Processing definition: {ExecutePart}", cronDefinition.ExecutePart);
                var occurrence = cronDefinition.Expression.GetOccurrences(
                    from,
                    to, TimeZoneInfo.Local).ToList();

                foreach (var dateTimeOffset in occurrence.OrderBy(x => x).Take(showNumberJobs))
                    list.Add((cronDefinition.ExecutePart, dateTimeOffset));
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Error occurred in WriteTasksToLog: {ErrorMessage}", e.Message);
        }

        foreach (var valueTuple in list.OrderBy(x => x.Item2))
            _log.LogDebug("Scheduled: {ScheduledTime} - {Command}", valueTuple.Item2, valueTuple.Item1);
    }


    private void Execute(DateTime date, Guid marker, long generation)
    {
        using var executionTimer = _performanceMonitor.StartTiming($"{MetricCategories.CronExecution}.batch");

        if (!_stateLock.TryEnterWriteLock(TimeSpan.FromSeconds(30)))
        {
            _log.LogError(LogMessages.CronLockTimeout, LogMessages.CronLockTimeoutTemplate, "write", "Execute");
            executionTimer.MarkFailure();
            return;
        }

        List<CronDefinition> itemsToRun;
        try
        {
            if (generation != _generation)
            {
                // Restart/Stop happened after this tick was scheduled: this chain is obsolete.
                _log.LogDebug("Stale cron schedule {Marker} (generation {Generation}, current {CurrentGeneration}) stopped",
                    marker, generation, _generation);
                executionTimer.MarkSuccess();
                return;
            }

            _log.LogDebug(LogMessages.CronExecutionStarted, LogMessages.CronExecutionStartedTemplate,
                marker, date.ToLocalTime(), _itemsToRunOnNextMinute.Count);

            itemsToRun = _itemsToRunOnNextMinute.ToList();
            _itemsToRunOnNextMinute.Clear();

            // Plan the next minute before starting jobs, so a slow job cannot delay or skip it.
            var next = date.AddMinutes(1);
            var nextMarker = Guid.NewGuid();
            _log.LogDebug("Starting next cron execution round");
            _log.LogDebug(LogMessages.CronExecutionCompleted, LogMessages.CronExecutionCompletedTemplate, marker,
                nextMarker);
            GenerateNext(next, nextMarker);
            ScheduleNext(next, nextMarker, generation);
        }
        finally
        {
            _stateLock.ExitWriteLock();
        }

        RunJobs(itemsToRun);
        executionTimer.MarkSuccess();
    }

    private void RunJobs(List<CronDefinition> itemsToRun)
    {
        if (itemsToRun.Count == 0)
            return;

        var executedJobs = 0;
        var failedJobs = 0;

        foreach (var cronDefinition in itemsToRun)
            try
            {
                using var jobTimer = _performanceMonitor.StartTiming($"{MetricCategories.CronExecution}.job");

                var processWrapper = _serviceProvider.GetRequiredService<ProcessWrapper>();
                var t1 = Pharse.Tokenize2(cronDefinition.ExecutePart);
                processWrapper.Start(t1);

                executedJobs++;
                jobTimer.MarkSuccess();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to execute cron job: {ExecutePart}", cronDefinition.ExecutePart);
                failedJobs++;
            }

        _performanceMonitor.RecordMetric(new PerformanceMetrics
        {
            OperationName = $"{MetricCategories.CronExecution}.batch_jobs",
            Duration = TimeSpan.Zero,
            Success = failedJobs == 0,
            AdditionalData = new Dictionary<string, object>
            {
                ["ExecutedJobs"] = executedJobs,
                ["FailedJobs"] = failedJobs,
                ["TotalJobs"] = itemsToRun.Count
            }
        });
    }

    /// <summary>
    ///     Schedules the tick at <paramref name="next" />. Caller holds the write lock.
    /// </summary>
    private void ScheduleNext(DateTime next, Guid marker, long generation)
    {
        if (generation != _generation)
            return;

        _log.LogDebug(LogMessages.CronNextScheduled, LogMessages.CronNextScheduledTemplate, marker, next.ToLocalTime());
        _nextSchedule?.Dispose();
        _nextSchedule = Observable
            .Timer(new DateTimeOffset(next), _scheduler)
            .Subscribe(_ => Execute(next, marker, generation));
    }

    private void GenerateNext(DateTime next, Guid marker)
    {
        // Dates in cron file are in "local time". We should convert next to local time. 
        _log.LogDebug("Starting GenerateNext operation");
        var nextLocal = next.ToLocalTime();
        var counter = 0;
        const int max = 10;

        while (true)
        {
            counter++;
            if (counter == max) break;
            _log.LogDebug("GenerateNext attempt {Attempt}", counter);
            try
            {
                // Read operation - use read lock for accessing _cronDefinitions
                if (!_stateLock.TryEnterReadLock(TimeSpan.FromSeconds(10)))
                {
                    _log.LogError(LogMessages.CronLockTimeout, LogMessages.CronLockTimeoutTemplate, "read",
                        "GenerateNext");
                    break;
                }

                List<CronDefinition> list;
                try
                {
                    list = _cronDefinitions
                        .SelectMany(x => x.Value)
                        .Where(x =>
                        {
                            // ReSharper disable once CommentTypo
                            // Read more: https://github.com/HangfireIO/Cronos#working-with-time-zones
                            var n1 = x.Expression.GetNextOccurrence(_scheduler.Now, TimeZoneInfo.Local);
                            var nextLocalTime = n1?.DateTime;
                            return nextLocalTime == nextLocal;
                        })
                        .Distinct()
                        .ToList();
                }
                finally
                {
                    _stateLock.ExitReadLock();
                }

                _itemsToRunOnNextMinute.Clear();
                _itemsToRunOnNextMinute.AddRange(list);
                break;
            }
            catch (Exception e)
            {
                _log.LogError(LogMessages.CronGenerateNextError, LogMessages.CronGenerateNextErrorTemplate, counter,
                    e.Message);
                _log.LogError(e.Message);
            }
        }

        _log.LogDebug(LogMessages.CronTasksScheduled, LogMessages.CronTasksScheduledTemplate,
            marker, nextLocal, _itemsToRunOnNextMinute.Count);
    }

    private void CreateListDefinitions(string[] cronFiles)
    {
        try
        {
            _log.LogDebug("CreateListDefinitions starting for files: {CronFiles}", string.Join(", ", cronFiles));

            if (!_stateLock.TryEnterWriteLock(TimeSpan.FromSeconds(30)))
            {
                _log.LogError(LogMessages.CronLockTimeout, LogMessages.CronLockTimeoutTemplate, "write",
                    "CreateListDefinitions");
                return;
            }

            try
            {
                _log.LogDebug(LogMessages.CronDefinitionsLoaded, LogMessages.CronDefinitionsLoadedTemplate,
                    string.Join(", ", cronFiles));
                _cronDefinitions.Clear();
                foreach (var crontabFile in cronFiles)
                {
                    var crontabFileParser = _serviceProvider.GetRequiredService<CrontabFileParser>();
                    var list = crontabFileParser.Read(crontabFile);
                    _cronDefinitions.Add(crontabFile, list);
                }
            }
            finally
            {
                _stateLock.ExitWriteLock();
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Error in CreateListDefinitions operation: {ErrorMessage}", e.Message);
        }
    }
}

public interface ICronService
{
    void Start(string[] crontabFiles);
    void Restart(string[] crontabFiles);
    void Stop();
}