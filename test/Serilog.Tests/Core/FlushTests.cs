namespace Serilog.Tests.Core;

[Collection("Log.Logger")]
public class FlushTests
{
    [Fact]
    public async Task FlushingALoggerFlushesItsSinksWithoutDisposingThem()
    {
        var flushable = new FlushTrackingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(flushable)
            .CreateLogger();

        await logger.FlushAsync();

        Assert.True(flushable.IsFlushed);

        // The pipeline is still open.
        logger.Information("Hello");
    }

    [Fact]
    public async Task FlushingALoggerLeavesItUsable()
    {
        var collecting = new CollectingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(collecting)
            .CreateLogger();

        logger.Information("Before");
        await logger.FlushAsync();
        logger.Information("After");

        Assert.Equal(2, collecting.Events.Count);
    }

    [Fact]
    public async Task FlushingIsANoOpWhenNoSinkSupportsIt()
    {
        var collecting = new CollectingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(collecting)
            .CreateLogger();

        logger.Information("Hello");
        await logger.FlushAsync();

        Assert.Single(collecting.Events);
    }

    [Fact]
    public async Task FlushingABatchedSinkWritesBufferedEventsToItsTarget()
    {
        var batched = new InMemoryBatchedSink(TimeSpan.Zero);
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(batched, new()
            {
                BatchSizeLimit = 2, BufferingTimeLimit = TimeSpan.FromMinutes(1), EagerlyEmitFirstEvent = false
            })
            .CreateLogger();

        var evt = Some.InformationEvent();
        logger.Write(evt);

        await logger.FlushAsync();

        var batch = Assert.Single(batched.Batches);
        Assert.Same(evt, Assert.Single(batch));
        Assert.False(batched.IsDisposed);

        // Logging continues after the flush.
        logger.Write(Some.InformationEvent());
        await logger.FlushAsync();

        Assert.Equal(2, batched.Batches.Count);
        Assert.False(batched.IsDisposed);
    }

    [Fact]
    public async Task FlushingReachesSinksWrappedByLevelRestriction()
    {
        var flushable = new FlushTrackingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(flushable, restrictedToMinimumLevel: Warning)
            .CreateLogger();

        await logger.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingReachesSinksWrappedByALevelSwitch()
    {
        var flushable = new FlushTrackingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(flushable, levelSwitch: new(Information))
            .CreateLogger();

        await logger.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingReachesSinksWrappedByACondition()
    {
        var flushable = new FlushTrackingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Conditional(_ => false, wt => wt.Sink(flushable))
            .CreateLogger();

        await logger.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingReachesSinksWrappedByAFallbackChain()
    {
        var flushable = new FlushTrackingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.FallbackChain(
                wt => wt.Sink(flushable),
                wt => wt.Sink(new CollectingSink()))
            .CreateLogger();

        await logger.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingReachesSubLoggers()
    {
        var flushable = new FlushTrackingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Logger(lc => lc.WriteTo.Sink(flushable))
            .CreateLogger();

        await logger.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingReachesAuditSinks()
    {
        var flushable = new FlushTrackingSink();
        var logger = new LoggerConfiguration()
            .AuditTo.Sink(flushable)
            .CreateLogger();

        await logger.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingAChildLoggerFlushesTheSharedPipeline()
    {
        var flushable = new FlushTrackingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(flushable)
            .CreateLogger();

        await logger.ForContext<FlushTests>().FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingAWrapperFlushesASinkThatCannotFlushItself()
    {
        var flushable = new FlushTrackingSink();
        var wrapper = LoggerSinkConfiguration.Wrap(_ => new DelegatingSink(_ => { }), wt => wt.Sink(flushable));

        var flushableWrapper = Assert.IsAssignableFrom<IFlushableLogEventSink>(wrapper);
        await flushableWrapper.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingASafeAggregateSinkContinuesPastFailures()
    {
        var flushable = new FlushTrackingSink();
        var sink = new SafeAggregateSink([new ThrowingFlushSink(), flushable]);

        await sink.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingASafeAggregateSinkDoesNotThrow()
    {
        var sink = new SafeAggregateSink([new ThrowingFlushSink()]);

        await sink.FlushAsync();
    }

    [Fact]
    public async Task FlushingAnAggregateSinkPropagatesFailures()
    {
        var sink = new AggregateSink([new ThrowingFlushSink()]);

        await Assert.ThrowsAsync<AggregateException>(sink.FlushAsync);
    }

    [Fact]
    public async Task FlushingASilentLoggerIsANoOp()
    {
        await Log.Logger.FlushAsync();
    }

    [Fact]
    public async Task LogFlushAsyncFlushesTheCurrentLogger()
    {
        var flushable = new FlushTrackingSink();
        Log.CloseAndFlush();
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Sink(flushable)
            .CreateLogger();

        try
        {
            await Log.FlushAsync();
        }
        finally
        {
            Log.CloseAndFlush();
        }

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task LogFlushAsyncDoesNotDisposeTheCurrentLogger()
    {
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(new FlushTrackingSink())
            .CreateLogger();

        Log.CloseAndFlush();
        Log.Logger = logger;

        try
        {
            await Log.FlushAsync();
            Assert.Same(logger, Log.Logger);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    [Fact]
    public async Task FlushingThroughExtensionMethodIsANoOpForNonFlushableLoggers()
    {
        ILogger logger = new SilentLogger();
        await logger.FlushAsync();
    }

    [Fact]
    public async Task LogFlushAsyncDoesNotThrow()
    {
        Log.CloseAndFlush();
        Log.Logger = new LoggerConfiguration()
            .AuditTo.Sink(new ThrowingFlushSink())
            .CreateLogger();

        try
        {
            await Log.FlushAsync();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    [Fact]
    public async Task FlushingThroughExtensionMethodThrowsForNullLoggers()
    {
        ILogger logger = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(logger.FlushAsync);
    }
}
