using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Corney.Tests.TestSupport;

/// <summary>
///     Logger that counts log entries per EventId.
/// </summary>
internal sealed class CountingLogger<T> : ILogger<T>
{
    private readonly ConcurrentDictionary<int, int> _counts = new();

    public IDisposable BeginScope<TState>(TState state)
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        _counts.AddOrUpdate(eventId.Id, 1, (_, c) => c + 1);
    }

    public int Count(EventId eventId)
    {
        return _counts.TryGetValue(eventId.Id, out var c) ? c : 0;
    }
}
