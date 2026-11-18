#pragma warning disable IDE0130
namespace Deneblab.SimpleLog;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


/// <summary>Flush level for controlling flush behavior.</summary>
public enum FlushLevel
{
    /// <summary>Complete current batch normally.</summary>
    Soft,

    /// <summary>Force immediate write of all queued items.</summary>
    Hard,

    /// <summary>Hard flush + wait for file system sync.</summary>
    Sync
}

// ───────────────────────────────────────────────────────────────────────────────
// Options
// ───────────────────────────────────────────────────────────────────────────────
public sealed class SingleFileLoggerOptions
{
    public string DirectoryPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public string FileName { get; set; } = "myapp"; // => myapp.yyyy-MM-dd.000.txt
    public string Extension { get; set; } = ".txt";

    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>Batch size and flush cadence (open → write batch → close).</summary>
    public int BatchSize { get; set; } = 256;

    public TimeSpan FlushPeriod { get; set; } = TimeSpan.FromSeconds(1);

    public bool AllowReaders { get; set; } = true;
    public bool AllowExternalDelete { get; set; } = true;

    public string ArchiveDirName { get; set; } = "archives";
    public int ArchiveRetentionDays { get; set; } = 10;

    public int RollingIndexDigits { get; set; } = 3;

    public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Use UTC time in timestamps (otherwise local).</summary>
    public bool UseUtcTimestamps { get; set; } = false;

    /// <summary>Roll when file exceeds this size (0 disables size rolling).</summary>
    public long MaxBytes { get; set; } = 50 * 1024 * 1024; // 50 MB

    public bool IncludeScopes { get; set; } = false;

    public bool CrossProcessWrites { get; set; } = false;
    public string CrossProcessMutexName { get; set; }
    public bool IncludeProcessId { get; set; } = true;

    /// <summary>
    ///     Custom formatter. Return one or more lines to write. If set, you control exception/scope rendering.
    /// </summary>
    public Func<LogEntry, IEnumerable<string>> Formatter { get; set; }

    /// <summary>
    ///     Validates the configuration options and throws exceptions for invalid settings.
    /// </summary>
    public void Validate()
    {
        if (BatchSize <= 0)
            throw new ArgumentException("BatchSize must be positive", nameof(BatchSize));
        if (MaxBytes < 0)
            throw new ArgumentException("MaxBytes cannot be negative", nameof(MaxBytes));
        if (string.IsNullOrWhiteSpace(DirectoryPath))
            throw new ArgumentException("DirectoryPath cannot be empty", nameof(DirectoryPath));
        if (RollingIndexDigits < 1 || RollingIndexDigits > 10)
            throw new ArgumentException("RollingIndexDigits must be between 1 and 10", nameof(RollingIndexDigits));
        if (ArchiveRetentionDays < 0)
            throw new ArgumentException("ArchiveRetentionDays cannot be negative", nameof(ArchiveRetentionDays));
    }
}

/// <summary>Data passed to the custom formatter when <see cref="SingleFileLoggerOptions.Formatter" /> is set.</summary>
public readonly record struct LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    Exception Exception,
    IReadOnlyList<string> Scopes,
    int ProcessId
);

// ───────────────────────────────────────────────────────────────────────────────
// Registration
// ───────────────────────────────────────────────────────────────────────────────
public static class SingleFileLoggerExtensions
{
    public static ILoggingBuilder AddSingleFile(this ILoggingBuilder b,
        Action<SingleFileLoggerOptions> configure = null)
    {
        if (configure is not null) b.Services.Configure(configure);
        b.Services.AddSingleton<SingleFileLoggerProvider>();
        b.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<SingleFileLoggerProvider>());
        return b;
    }
}

// ───────────────────────────────────────────────────────────────────────────────
// Provider (close-after-batch writer + manual flush)
// ───────────────────────────────────────────────────────────────────────────────
public sealed class SingleFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    // Performance optimization fields
    private static readonly ThreadLocal<StringBuilder> _cachedStringBuilder = new(() => new StringBuilder(256));
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable _onChange;

    private readonly BlockingCollection<LogItem> _queue = new(10_000);
    private readonly Task _worker;

    private Mutex _crossProcMutex;
    private DateTime _lastWrite = DateTime.MinValue;
    private SingleFileLoggerOptions _options;
    private LoggerExternalScopeProvider _scopes = new();
    private long _totalErrors;

    // Health monitoring fields
    private long _totalMessages;

    public SingleFileLoggerProvider(IOptionsMonitor<SingleFileLoggerOptions> options)
    {
        _options = options.CurrentValue;
        _options.Validate(); // Validate configuration on startup
        _onChange = options.OnChange(o =>
        {
            o.Validate();
            Volatile.Write(ref _options, o);
        });
        _worker = Task.Run(LoopAsync);
    }

    public ILogger CreateLogger(string category)
    {
        return new SingleFileLogger(this, category);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _cts.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            //
        }

        _onChange?.Dispose();
        _cts.Dispose();
        _cachedStringBuilder.Dispose(); // Clean up cached StringBuilder
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopes = scopeProvider as LoggerExternalScopeProvider ?? new LoggerExternalScopeProvider();
    }

    internal IDisposable BeginScope<TState>(TState state)
    {
        return _scopes.Push(state!);
    }

    internal bool IsEnabled(LogLevel level)
    {
        return level >= Volatile.Read(ref _options).MinimumLevel;
    }

    internal void Enqueue(LogItem item)
    {
        _queue.TryAdd(item);
        // best-effort drop if full
    }

    // ── Manual flush with levels
    public Task FlushAsync(FlushLevel level = FlushLevel.Soft, CancellationToken cancel = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushItem = LogItem.FlushMarker(tcs, level);

        while (!_queue.TryAdd(flushItem))
        {
            cancel.ThrowIfCancellationRequested();
            if (level == FlushLevel.Hard)
                // For hard flush, make space by taking an item if queue is full
                if (_queue.TryTake(out _))
                    continue;
            Thread.Yield();
        }

        return tcs.Task;
    }

    public void FlushNow(TimeSpan? timeout = null)
    {
        var task = FlushAsync();
        if (!task.Wait(timeout ?? TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Flush did not complete within the specified timeout.");
    }

    /// <summary>
    ///     Gets the current health status of the logger.
    /// </summary>
    /// <returns>Tuple containing queue length, last write time, total messages processed, and total errors.</returns>
    public (int QueueLength, DateTime LastWrite, long TotalMessages, long TotalErrors) GetHealth()
    {
        return (_queue.Count, _lastWrite, Interlocked.Read(ref _totalMessages), Interlocked.Read(ref _totalErrors));
    }

    /// <summary>
    ///     Executes a file operation with retry logic and exponential backoff.
    /// </summary>
    private static void RetryFileOperation<T>(Func<T> operation, int maxAttempts = 3)
    {
        Exception lastEx = null;
        for (var i = 0; i < maxAttempts; i++)
            try
            {
                operation();
                return;
            }
            catch (IOException ex) when (i < maxAttempts - 1)
            {
                lastEx = ex;
                Thread.Sleep(100 * (1 << i)); // Exponential backoff: 100ms, 200ms, 400ms
            }

        throw new IOException($"File operation failed after {maxAttempts} attempts", lastEx!);
    }

    private async Task LoopAsync()
    {
        var timer = new PeriodicTimer(Volatile.Read(ref _options)!.FlushPeriod);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var options = Volatile.Read(ref _options); // Cache options once per iteration
                if (options != null)
                {
                    var batch = new List<LogItem>(options.BatchSize);
                    TaskCompletionSource<bool> flushTcs = null;
                    var flushLevel = FlushLevel.Soft;

                    //  Collect some batch
                    while (batch.Count < options.BatchSize && _queue.TryTake(out var item))
                    {
                        if (item.FlushTcs is not null)
                        {
                            flushTcs = item.FlushTcs;
                            flushLevel = item.FlushLevel;
                            break;
                        }

                        batch.Add(item);
                    }

                    if (batch.Count == 0 && flushTcs is null)
                    {
                        if (!await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                            break;
                        continue;
                    }

                    // 🅱) Archive yesterday and manage current day files (best-effort)
                    try
                    {
                        if (options.ArchiveRetentionDays > 0)
                        {
                            var yesterday = DateOnly.FromDateTime(DateTime.Now.AddDays(-1));
                            ArchiveWholeDay(options, yesterday);
                            PruneArchives(Path.Combine(options.DirectoryPath, options.ArchiveDirName),
                                options.FileName, options.Extension, options.ArchiveRetentionDays);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Archive operations are best-effort, don't fail logging
                        Debug.WriteLine($"SingleFileLogger archive error: {ex}");
                    }

                    // 🅲) Write this batch with retry logic and health tracking
                    try
                    {
                        if (batch.Count > 0)
                            RetryFileOperation(() =>
                            {
                                var share = (options.AllowReaders ? FileShare.ReadWrite : FileShare.None) |
                                            (options.AllowExternalDelete ? FileShare.Delete : FileShare.None);

                                var today = DateOnly.FromDateTime(DateTime.Now);
                                var index = FindStartingIndex(options, today);
                                ArchiveCurrentDayOlderFiles(options, today, index);

                                var path = ComputePath(options, today, index);

                                using var writer = OpenWriter(path, share, out var currentSize);

                                foreach (var e in batch)
                                {
                                    var lines = PrepareLines(e, options);

                                    // Handle size-based rolling if needed
                                    if (options.MaxBytes > 0)
                                    {
                                        var entryBytes = GetBytesCount(lines);
                                        if (currentSize > 0 && currentSize + entryBytes > options.MaxBytes)
                                        {
                                            writer.Flush();
                                            writer.Dispose();

                                            TryMoveToArchive(path, options);

                                            index++;
                                            path = ComputePath(options, today, index);

                                            // Continue with remaining items in new file
                                            WriteRemaining(batch, batch.IndexOf(e), writer, ref path, ref index,
                                                ref currentSize, options, share, today);
                                            return true;
                                        }
                                    }

                                    WriteLines(writer, lines, ref currentSize);
                                }

                                writer.Flush();

                                // Handle sync flush level
                                if (flushLevel == FlushLevel.Sync) writer.BaseStream.Flush(); // Force OS flush

                                return true;
                            });

                        // Update health counters
                        Interlocked.Add(ref _totalMessages, batch.Count);
                        _lastWrite = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _totalErrors);
                        // Log error but continue processing
                        Debug.WriteLine($"SingleFileLogger error: {ex}");
                    }
                    finally
                    {
                        flushTcs?.TrySetResult(true);
                    }
                }

                if (options == null) continue;
                var now = options.FlushPeriod;
                if (timer.Period == now) continue;
                timer.Dispose();
                timer = new PeriodicTimer(now);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }


    // ── Building & writing lines
    private static List<string> PrepareLines(LogItem e, SingleFileLoggerOptions o)
    {
        var ts = o.UseUtcTimestamps ? e.Timestamp.ToUniversalTime() : e.Timestamp;

        if (o.Formatter is not null)
        {
            // Custom formatter fully controls output (including exception/scopes)
            var entry = new LogEntry(
                ts,
                e.Level,
                e.Category,
                e.Message,
                e.Exception,
                e.Scopes,
                Environment.ProcessId
            );
            var result = o.Formatter(entry)?.ToList() ?? new List<string>(0);
            if (result.Count == 0)
                // fall back to default single line if formatter gave nothing
                result.Add(DefaultMainLine(ts, o, e.Level, e.Category, e.Message, o.IncludeProcessId));
            return result;
        }

        // Default 1–3 lines (main + optional exception + optional scopes)
        var lines = new List<string>(3)
        {
            DefaultMainLine(ts, o, e.Level, e.Category, e.Message, o.IncludeProcessId && o.CrossProcessWrites)
        };
        if (e.Exception is not null) lines.Add(e.Exception.ToString());
        if (o.IncludeScopes && e.Scopes is { Count: > 0 })
            lines.Add("scopes|" + string.Join(" => ", e.Scopes));
        return lines;
    }

    private static string DefaultMainLine(DateTimeOffset ts, SingleFileLoggerOptions o, LogLevel level, string category,
        string message, bool includePid)
    {
        var sb = _cachedStringBuilder.Value!;
        sb.Clear();

        sb.Append(ts.ToString(o.TimestampFormat, CultureInfo.InvariantCulture)).Append('|')
            .Append(ShortLevel(level)).Append('|')
            .Append(category).Append('|');

        if (includePid)
            sb.Append("pid=").Append(Environment.ProcessId).Append('|');

        sb.Append(message);
        return sb.ToString();
    }

    private static long GetBytesCount(List<string> lines)
    {
        long sum = 0;
        foreach (var s in lines) sum += Encoding.UTF8.GetByteCount(s) + 1; // + newline
        return sum;
    }

    private static void WriteLines(StreamWriter writer, List<string> lines, ref long currentSize)
    {
        foreach (var line in lines)
        {
            writer.WriteLine(line);
            currentSize += Encoding.UTF8.GetByteCount(line) + 1;
        }
    }

    private static void WriteRemaining(
        List<LogItem> batch, int startIndex, StreamWriter writer, ref string path, ref int index,
        ref long currentSize, SingleFileLoggerOptions o, FileShare share, DateOnly today)
    {
        for (var i = startIndex; i < batch.Count; i++)
        {
            var e = batch[i];
            var lines = PrepareLines(e, o);

            if (o.MaxBytes > 0)
            {
                var entryBytes = GetBytesCount(lines);
                if (currentSize > 0 && currentSize + entryBytes > o.MaxBytes)
                {
                    writer.Flush();
                    writer.Dispose();
                    TryMoveToArchive(path, o);
                    index++;
                    path = ComputePath(o, today, index);

                    using var wNew = OpenWriter(path, share, out currentSize);
                    WriteLines(wNew, lines, ref currentSize);
                    wNew.Flush();

                    using var wCont = OpenWriter(path, share, out currentSize);
                    writer = wCont;
                    continue;
                }
            }

            WriteLines(writer, lines, ref currentSize);
        }

        writer.Flush();
    }

    private static StreamWriter OpenWriter(string p, FileShare share, out long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var fs = new FileStream(p, FileMode.Append, FileAccess.Write, share, 64 * 1024, false);
        size = fs.Length;
        return new StreamWriter(fs, new UTF8Encoding(false));
    }

    private static void TryMoveToArchive(string src, SingleFileLoggerOptions o)
    {
        try
        {
            var archiveRoot = Path.Combine(o.DirectoryPath, o.ArchiveDirName);
            Directory.CreateDirectory(archiveRoot);
            var dst = Path.Combine(archiveRoot, Path.GetFileName(src));
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }
        catch
        {
            // Ignore errors during move
        }
    }

    private static string ComputePath(SingleFileLoggerOptions o, DateOnly day, int index)
    {
        var idx = index.ToString($"D{o.RollingIndexDigits}");
        return Path.Combine(o.DirectoryPath, $"{o.FileName}.{day:yyyy-MM-dd}.{idx}{o.Extension}");
    }

    private static int? ParseIndexFromFileName(string path, int digits)
    {
        var name = Path.GetFileNameWithoutExtension(path); // base.yyyy-MM-dd.NNN
        var lastDot = name.LastIndexOf('.');
        if (lastDot < 0) return null;
        var idxStr = name[(lastDot + 1)..];
        return idxStr.Length == digits && int.TryParse(idxStr, out var i) ? i : null;
    }

    private static int FindStartingIndex(SingleFileLoggerOptions o, DateOnly day)
    {
        Directory.CreateDirectory(o.DirectoryPath);
        var pattern = $"{o.FileName}.{day:yyyy-MM-dd}.*{o.Extension}";
        var max = -1;
        foreach (var p in Directory.EnumerateFiles(o.DirectoryPath, pattern))
        {
            var idx = ParseIndexFromFileName(p, o.RollingIndexDigits);
            if (idx is { } i && i > max) max = i;
        }

        return Math.Max(0, max + 1);
    }

    private static void ArchiveCurrentDayOlderFiles(SingleFileLoggerOptions o, DateOnly day, int keepIndex)
    {
        var archiveRoot = Path.Combine(o.DirectoryPath, o.ArchiveDirName);
        Directory.CreateDirectory(archiveRoot);
        var pattern = $"{o.FileName}.{day:yyyy-MM-dd}.*{o.Extension}";

        foreach (var src in Directory.EnumerateFiles(o.DirectoryPath, pattern))
        {
            var idx = ParseIndexFromFileName(src, o.RollingIndexDigits);
            if (idx is not { } i || i >= keepIndex) continue;
            var dst = Path.Combine(archiveRoot, Path.GetFileName(src));
            try
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
            catch
            {
                // Ignore errors during move
            }
        }
    }

    private static void ArchiveWholeDay(SingleFileLoggerOptions o, DateOnly day)
    {
        var archiveRoot = Path.Combine(o.DirectoryPath, o.ArchiveDirName);
        Directory.CreateDirectory(archiveRoot);
        var pattern = $"{o.FileName}.{day:yyyy-MM-dd}.*{o.Extension}";
        foreach (var src in Directory.EnumerateFiles(o.DirectoryPath, pattern))
        {
            var dst = Path.Combine(archiveRoot, Path.GetFileName(src));
            try
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
            catch
            {
                // Ignore errors during move
            }
        }
    }

    private static void PruneArchives(string archiveRoot, string baseName, string ext, int keepDays)
    {
        if (keepDays <= 0) return;
        var cutoff = DateOnly.FromDateTime(DateTime.Now.Date.AddDays(-keepDays));
        foreach (var p in Directory.EnumerateFiles(archiveRoot, $"{baseName}.*{ext}"))
        {
            var name = Path.GetFileNameWithoutExtension(p);
            var parts = name.Split('.');
            if (parts.Length < 3) continue;
            if (!DateOnly.TryParseExact(parts[^2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var d)) continue;
            if (d >= cutoff) continue;
            try
            {
                File.Delete(p);
            }
            catch
            {
                // Ignore errors during delete
            }
        }
    }

    private static string ShortLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "FTL",
            _ => "UNK"
        };
    }

    private Mutex CrossMutex(SingleFileLoggerOptions o)
    {
        return _crossProcMutex ??= new Mutex(false, @"Global\" + (o.CrossProcessMutexName ?? "SFL_" + o.FileName));
    }

    // transport item; Flush marker includes a TCS
    internal readonly record struct LogItem(
        DateTimeOffset Timestamp,
        LogLevel Level,
        string Category,
        string Message,
        Exception Exception,
        List<string> Scopes,
        TaskCompletionSource<bool> FlushTcs,
        FlushLevel FlushLevel = FlushLevel.Soft
    )
    {
        public static LogItem FlushMarker(TaskCompletionSource<bool> tcs, FlushLevel level = FlushLevel.Soft)
        {
            return new LogItem(default, LogLevel.None, "__FLUSH__", "", null, null, tcs, level);
        }
    }

    // per-category logger
    private sealed class SingleFileLogger(SingleFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
        {
            return provider.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return provider.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var o = provider._options;
            Mutex m = null;

            try
            {
                if (o.CrossProcessWrites)
                {
                    m = provider.CrossMutex(o);
                    m.WaitOne();
                }

                var msg = SafeFormat(formatter, state, exception);

                List<string> scopes = null;
                if (o.IncludeScopes)
                {
                    scopes = new List<string>();
                    provider._scopes.ForEachScope((s, list) => list!.Add(s?.ToString() ?? string.Empty), scopes);
                }

                provider.Enqueue(new LogItem(
                    DateTimeOffset.Now,
                    logLevel,
                    category,
                    msg,
                    exception,
                    scopes,
                    null));
            }
            finally
            {
                if (m is not null)
                    try
                    {
                        m.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore if mutex release fails (e.g., if it was never acquired)
                    }
            }
        }

        private static string SafeFormat<TState>(Func<TState, Exception, string> formatter, TState state,
            Exception ex)
        {
            try
            {
                return formatter(state, ex);
            }
            catch
            {
                return state?.ToString() ?? string.Empty;
            }
        }
    }
}


// === InternalFileLoggerProvider ===
public sealed class InternalFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public InternalFileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    public ILogger CreateLogger(string categoryName) => new InternalFileLogger(_filePath, _lock, categoryName);

    public void Dispose() { }

    private class InternalFileLogger : ILogger
    {
        private readonly string _filePath;
        private readonly object _lock;
        private readonly string _category;

        public InternalFileLogger(string filePath, object lockObj, string category)
        {
            _filePath = filePath;
            _lock = lockObj;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) => default!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}|{logLevel}|{_category}|{formatter(state, exception)}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(_filePath, line);
            }
        }
    }
}

// === Extension for nice usage ===
public static class InternalFileLoggerExtensions
{
    public static ILoggingBuilder AddInternalFile(this ILoggingBuilder builder, string filePath)
    {
        builder.AddProvider(new InternalFileLoggerProvider(filePath));
        return builder;
    }
}

public sealed class InMemoryLoggerOptions
{
    /// <summary>Max number of log entries to keep in memory. Oldest entries are dropped when over capacity.</summary>
    public int Capacity { get; set; } = 5_000;

    /// <summary>Minimum level to store. Still respects IsEnabled checks on consumers when replaying.</summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Trace;

    /// <summary>Capture scope values into each entry (slight overhead).</summary>
    public bool CaptureScopes { get; set; } = true;
}

/// <summary>Immutable log entry captured in memory.</summary>
public sealed record InMemoryLogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<object?> Scopes);

public sealed class InMemoryLoggerProvider :
    ILoggerProvider, ISupportExternalScope
{
    private readonly InMemoryLoggerOptions _options;
    private readonly ConcurrentQueue<InMemoryLogEntry> _queue = new();
    private int _count;
    private IExternalScopeProvider? _scopeProvider;

    public InMemoryLoggerProvider() : this(new InMemoryLoggerOptions())
    {
    }

    public InMemoryLoggerProvider(InMemoryLoggerOptions options)
    {
        _options = options ?? new InMemoryLoggerOptions();
    }

    // --- Public API ---

    public int Count => Volatile.Read(ref _count);

    public ILogger CreateLogger(string categoryName)
    {
        return new InMemoryLogger(this, categoryName);
    }

    public void Dispose()
    {
    }

    void ISupportExternalScope.SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    /// <summary>Snapshot of current entries (copy).</summary>
    public ReadOnlyCollection<InMemoryLogEntry> Snapshot()
    {
        return new ReadOnlyCollection<InMemoryLogEntry>(_queue.ToArray());
    }

    /// <summary>Clears all buffered entries.</summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _count, 0);
    }

    /// <summary>Replay buffered entries to a target ILogger.</summary>
    public void ReplayTo(ILogger target, Func<InMemoryLogEntry, bool>? filter = null)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        foreach (var e in _queue)
        {
            if (filter is not null && !filter(e)) continue;
            if (!target.IsEnabled(e.Level)) continue;

            using var scope = BeginRecreatedScope(target, e.Scopes);
            target.Log(e.Level, e.EventId, e.Message, e.Exception, static (s, _) => s);
        }
    }

    /// <summary>Replay buffered entries to a category on an ILoggerFactory.</summary>
    public void ReplayTo(ILoggerFactory factory, string category = "Replay")
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        ReplayTo(factory.CreateLogger(category));
    }

    // --- Internals ---

    private void Enqueue(InMemoryLogEntry entry)
    {
        _queue.Enqueue(entry);
        var newCount = Interlocked.Increment(ref _count);

        // Trim if above capacity
        while (newCount > _options.Capacity && _queue.TryDequeue(out _))
            newCount = Interlocked.Decrement(ref _count);
    }

    private static IDisposable? BeginRecreatedScope(ILogger target, IReadOnlyList<object?> scopes)
    {
        // Recreate original scope stack in order.
        if (scopes.Count == 0) return null;

        var disposables = new Stack<IDisposable>(scopes.Count);
        try
        {
            foreach (var s in scopes)
                disposables.Push(target.BeginScope(s ?? "(null)"));

            // Return a composite disposable to unwind in reverse
            return new ScopeStack(disposables);
        }
        catch
        {
            // On any failure, dispose what we created and continue without scopes.
            while (disposables.Count > 0)
                disposables.Pop().Dispose();
            return null;
        }
    }

    private sealed class InMemoryLogger : ILogger
    {
        private readonly string _category;
        private readonly InMemoryLoggerProvider _provider;

        public InMemoryLogger(InMemoryLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return (_provider._scopeProvider ??= new LoggerExternalScopeProvider())
                .Push(state!);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= _provider._options.MinLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (formatter is null) throw new ArgumentNullException(nameof(formatter));

            var scopes = Array.Empty<object?>();
            if (_provider._options.CaptureScopes)
            {
                var list = new List<object?>();
                (_provider._scopeProvider ??= new LoggerExternalScopeProvider())
                    .ForEachScope((s, l) => l!.Add(s), list);
                scopes = list.ToArray();
            }

            var message = formatter(state, exception);
            var entry = new InMemoryLogEntry(
                DateTimeOffset.Now,
                logLevel,
                _category,
                eventId,
                message,
                exception,
                scopes);

            _provider.Enqueue(entry);
        }
    }

    private sealed class ScopeStack : IDisposable
    {
        private Stack<IDisposable>? _stack;

        public ScopeStack(Stack<IDisposable> stack)
        {
            _stack = stack;
        }

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _stack, null);
            if (s is null) return;
            while (s.Count > 0) s.Pop().Dispose();
        }
    }
}

// ---------- Nice extensions ----------
public static class InMemoryLoggerExtensions
{
    /// <summary>
    ///     Adds an in-memory logger and gives you the provider instance via 'out' so you can snapshot/replay later.
    /// </summary>
    public static ILoggingBuilder AddInMemory(this ILoggingBuilder builder,
        out InMemoryLoggerProvider provider,
        InMemoryLoggerOptions? options = null)
    {
        provider = new InMemoryLoggerProvider(options ?? new InMemoryLoggerOptions());
        builder.AddProvider(provider);
        return builder;
    }
}