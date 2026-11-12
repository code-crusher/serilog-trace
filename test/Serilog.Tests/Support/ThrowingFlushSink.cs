namespace Serilog.Tests.Support;

class ThrowingFlushSink : ILogEventSink, IFlushableLogEventSink
{
    public void Emit(LogEvent logEvent)
    {
    }

    public Task FlushAsync()
    {
        throw new InvalidOperationException("Failed.");
    }
}
