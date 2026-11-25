using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Corney.Common.Logging;
using Corney.Common.Performance;
using Corney.Core.Features.Cron.Models;
using Microsoft.Extensions.Logging;

namespace Corney.Features.Processes;

/// <summary>
///     Starts crontab commands the way cron does: fire-and-forget. The process is started and
///     Corney moves on; it never waits for the process, never kills it and ignores its exit code.
/// </summary>
public class ProcessWrapper
{
    // Started directly (CreateProcess). Anything else with an extension (.lnk, .ps1, .url, documents)
    // is started through the Windows shell, which resolves shortcuts and file associations.
    private static readonly HashSet<string> DirectExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".com", ".bat", ".cmd" };

    private readonly ILogger<ProcessWrapper> _log;
    private readonly PerformanceMonitor _performanceMonitor;
    private readonly Func<ProcessStartInfo, Process> _startProcess;

    public ProcessWrapper(ILogger<ProcessWrapper> log, PerformanceMonitor performanceMonitor = null)
        : this(log, performanceMonitor, Process.Start)
    {
    }

    internal ProcessWrapper(ILogger<ProcessWrapper> log, PerformanceMonitor performanceMonitor,
        Func<ProcessStartInfo, Process> startProcess)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _performanceMonitor = performanceMonitor;
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
    }

    /// <summary>
    ///     Starts the program and returns immediately.
    /// </summary>
    /// <returns>The process ID, or null when the shell did not start a new process.</returns>
    /// <exception cref="Exception">The process could not be started (e.g. file not found).</exception>
    public int? Start(ExecuteItem item)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        if (string.IsNullOrWhiteSpace(item.Program))
            throw new ArgumentException("Program cannot be null or empty", nameof(item));

        var startInfo = CreateStartInfo(item);

        _log.LogDebug("Executing: {Program}", startInfo.FileName);
        _log.LogDebug("Parameters: {Arguments}", startInfo.Arguments);
        _log.LogDebug("In directory: {WorkingDirectory}", startInfo.WorkingDirectory);
        if (startInfo.UseShellExecute)
            _log.LogDebug("Starting through the Windows shell: {Program}", startInfo.FileName);

        var operationName =
            $"{MetricCategories.ProcessExecution}.{Path.GetFileNameWithoutExtension(item.Program)}";
        using var timer = _performanceMonitor?.StartTiming(operationName);

        try
        {
            using var process = _startProcess(startInfo);
            var processId = process?.Id;

            _log.LogDebug(LogMessages.ProcessStarted, "Detached process started: {ProcessId} {Program}",
                processId?.ToString() ?? "(shell)", startInfo.FileName);

            timer?.MarkSuccess();
            return processId;
        }
        catch
        {
            // The caller (CronService) logs the exception together with the crontab line.
            timer?.MarkFailure();
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ExecuteItem item)
    {
        var program = item.Program.Trim();
        var useShell = UsesShellExecute(program);

        var startInfo = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = useShell,
            CreateNoWindow = !useShell,
            WorkingDirectory = ResolveWorkingDirectory(item.WorkingDirectory, program)
        };

        if (item.ArgumentsArray?.Length > 0)
            foreach (var argument in item.ArgumentsArray)
                startInfo.ArgumentList.Add(argument);
        else
            // Raw text from the crontab line, passed on as written (keeps the user's quoting).
            startInfo.Arguments = item.Arguments ?? string.Empty;

        return startInfo;
    }

    internal static bool UsesShellExecute(string program)
    {
        var extension = Path.GetExtension(program);
        return !string.IsNullOrEmpty(extension) && !DirectExtensions.Contains(extension);
    }

    private static string ResolveWorkingDirectory(string workingDirectory, string program)
    {
        if (!string.IsNullOrEmpty(workingDirectory))
            return workingDirectory;

        if (!Path.IsPathRooted(program))
            return string.Empty;

        return Path.GetDirectoryName(Path.GetFullPath(program)) ?? string.Empty;
    }
}
