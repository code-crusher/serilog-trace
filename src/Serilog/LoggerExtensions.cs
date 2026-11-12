namespace Serilog;

/// <summary>
/// Extends <see cref="ILogger" /> with additional methods.
/// </summary>
public static class LoggerExtensions
{
    /// <summary>
    /// Create a logger that enriches log events when the specified level is enabled.
    /// </summary>
    /// <typeparam name="TValue"> The type of the property value. </typeparam>
    /// <param name="logger">The logger.</param>
    /// <param name="level">The log event level used to determine if log is enriched with property.</param>
    /// <param name="propertyName">The name of the property. Must be non-empty.</param>
    /// <param name="value">The property value.</param>
    /// <param name="destructureObjects">If <see langword="true"/>, the value will be serialized as a structured
    /// object if possible; if <see langword="false"/>, the object will be recorded as a scalar or simple array.</param>
    /// <returns>A logger that will enrich log events as specified.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="logger"/> is <code>null</code></exception>
    public static ILogger ForContext<TValue>(
        this ILogger logger,
        LogEventLevel level,
        string propertyName,
        TValue value,
        bool destructureObjects = false)
    {
        Guard.AgainstNull(logger);

        return logger.IsEnabled(level)
            ? logger.ForContext(propertyName, value, destructureObjects)
            : logger;
    }

    /// <summary>
    /// Write any buffered log events to the sinks in the logger's pipeline, without closing or
    /// disposing the logger. Logging can continue normally afterwards.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <returns>A task that completes when the sinks that support flushing have written their buffered
    /// events, or when their attempts to do so have failed.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="logger"/> is <code>null</code></exception>
    /// <remarks>Flushing is best-effort, and is performed by sinks implementing
    /// <see cref="IFlushableLogEventSink"/>. Loggers and sinks that do not buffer events, or that cannot
    /// flush on demand, are unaffected.</remarks>
    public static Task FlushAsync(this ILogger logger)
    {
        Guard.AgainstNull(logger);

        return logger is IFlushableLogEventSink flushable
            ? flushable.FlushAsync()
            : Task.CompletedTask;
    }
}
