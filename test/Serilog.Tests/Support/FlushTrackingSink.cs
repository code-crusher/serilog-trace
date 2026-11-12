namespace Serilog.Tests.Support;

class FlushTrackingSink : ILogEventSink, IFlushableLogEventSink
{
    public int FlushCount { get; private set; }

    public bool IsFlushed => FlushCount != 0;

    public void Emit(LogEvent logEvent)
    {
    }

    public Task FlushAsync()
    {
        ++FlushCount;
        return Task.CompletedTask;
    }
}
