namespace Serilog.Tests.Support;

class FlushTrackingBatchedSink : IBatchedLogEventSink, IFlushableLogEventSink
{
    public int FlushCount { get; private set; }

    public bool IsFlushed => FlushCount != 0;

    public Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch) => Task.CompletedTask;

    public Task FlushAsync()
    {
        ++FlushCount;
        return Task.CompletedTask;
    }
}
