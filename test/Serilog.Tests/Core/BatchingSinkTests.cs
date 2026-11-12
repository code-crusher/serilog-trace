using Serilog.Core.Sinks.Batching;

namespace Serilog.Core.Tests;

public class BatchingSinkTests
{
    static readonly TimeSpan TinyWait = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan MicroWait = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void WhenAnEventIsEnqueuedItIsWrittenToABatchOnDispose()
    {
        var bs = new InMemoryBatchedSink(TimeSpan.Zero);
        var pbs = new BatchingSink(bs, new() { BatchSizeLimit = 2, BufferingTimeLimit = TinyWait, EagerlyEmitFirstEvent = true });
        var evt = Some.InformationEvent();
        pbs.Emit(evt);
        pbs.Dispose();
        var batch = Assert.Single(bs.Batches);
        var actual = Assert.Single(batch);
        Assert.Same(evt, actual);
        Assert.True(bs.IsDisposed);
        Assert.False(bs.WasCalledAfterDisposal);
    }

#if FEATURE_ASYNCDISPOSABLE
        [Fact]
        public async Task WhenAnEventIsEnqueuedItIsWrittenToABatchOnDisposeAsync()
        {
            var bs = new InMemoryBatchedSink(TimeSpan.Zero);
            var pbs = new BatchingSink(
                bs, new()
                {
                    BatchSizeLimit = 2, BufferingTimeLimit = TinyWait, EagerlyEmitFirstEvent = true
                });
            var evt = Some.InformationEvent();
            pbs.Emit(evt);
            await pbs.DisposeAsync();
            var batch = Assert.Single(bs.Batches);
            var actual = Assert.Single(batch);
            Assert.Same(evt, actual);
            Assert.True(bs.IsDisposed);
            Assert.True(bs.IsDisposedAsync);
            Assert.False(bs.WasCalledAfterDisposal);
        }
#endif

    [Fact]
    public void WhenAnEventIsEnqueuedItIsWrittenToABatchOnTimer()
    {
        var bs = new InMemoryBatchedSink(TimeSpan.Zero);
        var pbs = new BatchingSink(
            bs,
            new()
            {
                BatchSizeLimit = 2, BufferingTimeLimit = TinyWait, EagerlyEmitFirstEvent = true
            });
        var evt = Some.InformationEvent();
        pbs.Emit(evt);
        Thread.Sleep(TinyWait + TinyWait);
        bs.Stop();
        pbs.Dispose();
        Assert.Single(bs.Batches);
        Assert.True(bs.IsDisposed);
        Assert.False(bs.WasCalledAfterDisposal);
    }

    [Fact]
    public void WhenAnEventIsEnqueuedItIsWrittenToABatchOnDisposeWhileRunning()
    {
        var bs = new InMemoryBatchedSink(TinyWait + TinyWait);
        var pbs = new BatchingSink(bs, new() { BatchSizeLimit = 2, BufferingTimeLimit = MicroWait, EagerlyEmitFirstEvent = true });
        var evt = Some.InformationEvent();
        pbs.Emit(evt);
        Thread.Sleep(TinyWait);
        pbs.Dispose();
        Assert.Single(bs.Batches);
        Assert.True(bs.IsDisposed);
        Assert.False(bs.WasCalledAfterDisposal);
    }

    [Fact]
    public void ExecutionContextDoesNotFlowToBatchedSink()
    {
        var local = new AsyncLocal<int>
        {
            Value = 5
        };

        var observed = 17;
        var bs = new CallbackBatchedSink(_ =>
        {
            observed = local.Value;
            return Task.CompletedTask;
        });

        var pbs = new BatchingSink(bs, new());
        var evt = Some.InformationEvent();
        pbs.Emit(evt);
        pbs.Dispose();

        Assert.Equal(default, observed);
    }

    [Fact]
    public async Task FlushingWritesBufferedEventsWithoutDisposingTheSink()
    {
        var bs = new InMemoryBatchedSink(TimeSpan.Zero);
        var pbs = new BatchingSink(bs, new()
        {
            BatchSizeLimit = 2, BufferingTimeLimit = TimeSpan.FromMinutes(1), EagerlyEmitFirstEvent = false
        });

        var evt = Some.InformationEvent();
        pbs.Emit(evt);

        await pbs.FlushAsync();

        var batch = Assert.Single(bs.Batches);
        Assert.Same(evt, Assert.Single(batch));
        Assert.False(bs.IsDisposed);
        Assert.False(bs.WasCalledAfterDisposal);
    }

    [Fact]
    public async Task FlushingIsANoOpWhenNoEventsAreBuffered()
    {
        var bs = new InMemoryBatchedSink(TimeSpan.Zero);
        var pbs = new BatchingSink(bs, new()
        {
            BatchSizeLimit = 2, BufferingTimeLimit = TimeSpan.FromMinutes(1), EagerlyEmitFirstEvent = false
        });

        await pbs.FlushAsync();

        Assert.Empty(bs.Batches);
        Assert.False(bs.IsDisposed);
    }

    [Fact]
    public async Task EventsEmittedAfterFlushAreStillWritten()
    {
        var bs = new InMemoryBatchedSink(TimeSpan.Zero);
        var pbs = new BatchingSink(bs, new()
        {
            BatchSizeLimit = 2, BufferingTimeLimit = TimeSpan.FromMinutes(1), EagerlyEmitFirstEvent = false
        });

        var first = Some.InformationEvent();
        pbs.Emit(first);
        await pbs.FlushAsync();

        var second = Some.InformationEvent();
        pbs.Emit(second);
        await pbs.FlushAsync();

        Assert.Equal(2, bs.Batches.Count);
        Assert.Same(first, Assert.Single(bs.Batches[0]));
        Assert.Same(second, Assert.Single(bs.Batches[1]));
        Assert.False(bs.IsDisposed);
    }

    [Fact]
    public async Task FlushingFlushesTheTargetSink()
    {
        var flushable = new FlushTrackingBatchedSink();
        var pbs = new BatchingSink(flushable, new() { BufferingTimeLimit = TimeSpan.FromMinutes(1) });

        await pbs.FlushAsync();

        Assert.True(flushable.IsFlushed);
    }

    [Fact]
    public async Task FlushingCompletesWhenTheBatchCannotBeWritten()
    {
        var bs = new CallbackBatchedSink(_ => Task.FromException(new InvalidOperationException("Failed.")));
        var listener = new CollectingFailureListener();
        var pbs = new BatchingSink(bs, new()
        {
            BatchSizeLimit = 2, BufferingTimeLimit = TimeSpan.FromMinutes(1), EagerlyEmitFirstEvent = false
        });
        pbs.SetFailureListener(listener);

        pbs.Emit(Some.InformationEvent());
        await pbs.FlushAsync();

        Assert.NotEmpty(listener.Failures);
    }

    [Fact]
    public async Task FlushingAfterDisposalCompletesImmediately()
    {
        var bs = new InMemoryBatchedSink(TimeSpan.Zero);
        var pbs = new BatchingSink(bs, new() { BufferingTimeLimit = TimeSpan.FromMinutes(1) });

        pbs.Dispose();
        await pbs.FlushAsync();

        Assert.True(bs.IsDisposed);
    }

    [Fact]
    public async Task ConcurrentFlushRequestsAreAllCompleted()
    {
        var bs = new InMemoryBatchedSink(TimeSpan.Zero);
        var pbs = new BatchingSink(bs, new()
        {
            BatchSizeLimit = 2, BufferingTimeLimit = TimeSpan.FromMinutes(1), EagerlyEmitFirstEvent = false
        });

        pbs.Emit(Some.InformationEvent());

        var flushes = Enumerable.Range(0, 10).Select(_ => pbs.FlushAsync()).ToArray();
        var all = Task.WhenAll(flushes);

        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10))));
        await all;

        Assert.All(flushes, t => Assert.True(t.IsCompletedSuccessfully));
        Assert.False(bs.IsDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EagerlyEmitFirstEventCausesQuickInitialBatch(bool eagerlyEmit)
    {
        long emitted = 0;
        var bs = new CallbackBatchedSink(_ =>
        {
            // ReSharper disable once AccessToModifiedClosure
            Interlocked.Increment(ref emitted);
            return Task.CompletedTask;
        });

        var options = new BatchingOptions
        {
            BufferingTimeLimit = TimeSpan.FromSeconds(3),
            EagerlyEmitFirstEvent = eagerlyEmit,
            BatchSizeLimit = 10,
            QueueLimit = 1000
        };

        var pbs = new BatchingSink(bs, options);

        var evt = Some.InformationEvent();
        pbs.Emit(evt);

        await Task.Delay(1900);
        Assert.Equal(eagerlyEmit, Interlocked.Read(ref emitted) == 1);

#if FEATURE_ASYNCDISPOSABLE
        await pbs.DisposeAsync();
#else
        pbs.Dispose();
#endif

        Assert.Equal(1, Interlocked.Read(ref emitted));
    }
}
