// Copyright © Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Diagnostics;
using System.Threading.Channels;

// ReSharper disable UnusedParameter.Global, ConvertIfStatementToConditionalTernaryExpression, MemberCanBePrivate.Global, UnusedMember.Global, VirtualMemberNeverOverridden.Global, ClassWithVirtualMembersNeverInherited.Global, SuspiciousTypeConversion.Global

namespace Serilog.Core.Sinks.Batching;

/// <summary>
/// Buffers log events into batches for background flushing.
/// </summary>
sealed class BatchingSink : ILogEventSink, IFlushableLogEventSink, IDisposable, ISetLoggingFailureListener
#if FEATURE_ASYNCDISPOSABLE
        , IAsyncDisposable
#endif
{
    // Buffers events from the write- to the read side. Flush requests are written to the queue alongside
    // events, so that they are observed after all of the events that were emitted before them.
    readonly Channel<object> _queue;

    // These fields are used by the write side to signal shutdown.
    // A mutex is required because the queue writer `Complete()` call is not idempotent and will throw if
    // called multiple times, e.g. via multiple `Dispose()` calls on this sink.
    readonly object _stateLock = new();
    // Needed because the read loop needs to observe shutdown even when the target batched (remote) sink is
    // unable to accept events (preventing the queue from being drained and completion being observed).
    readonly CancellationTokenSource _shutdownSignal = new();
    // The write side can wait on this to ensure shutdown has completed.
    readonly Task _runLoop;

    // Used only by the read side.
    readonly IBatchedLogEventSink _targetSink;
    readonly int _batchSizeLimit;
    readonly bool _eagerlyEmitFirstEvent;
    readonly FailureAwareBatchScheduler _batchScheduler;
    readonly Queue<LogEvent> _currentBatch = new();
    readonly Task _waitForShutdownSignal;
    Task<bool>? _cachedWaitToRead;
    ILoggingFailureListener _failureListener = SelfLog.FailureListener;

    /// <summary>
    /// Construct a <see cref="BatchingSink"/>.
    /// </summary>
    /// <param name="batchedSink">A <see cref="IBatchedLogEventSink"/> to send log event batches to. Batches and empty
    /// batch notifications will not be sent concurrently. When the <see cref="BatchingSink"/> is disposed,
    /// it will dispose this object if possible.</param>
    /// <param name="options">Options controlling behavior of the sink.</param>
    public BatchingSink(IBatchedLogEventSink batchedSink, BatchingOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.BatchSizeLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The batch size limit must be greater than zero.");
        if (options.BufferingTimeLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The buffering time limit must be greater than zero.");
        if (options.RetryTimeLimit < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The retry time limit must not be negative.");

        _targetSink = batchedSink ?? throw new ArgumentNullException(nameof(batchedSink));
        _batchSizeLimit = options.BatchSizeLimit;
        _queue = options.QueueLimit is { } limit
            ? Channel.CreateBounded<object>(new BoundedChannelOptions(limit) { SingleReader = true })
            : Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
        _batchScheduler = new FailureAwareBatchScheduler(options.BufferingTimeLimit, options.RetryTimeLimit);
        _eagerlyEmitFirstEvent = options.EagerlyEmitFirstEvent;
        _waitForShutdownSignal = Task.Delay(Timeout.InfiniteTimeSpan, _shutdownSignal.Token)
            .ContinueWith(e => e.Exception, TaskContinuationOptions.OnlyOnFaulted);

        // The conditional here is no longer required in .NET 8+ (dotnet/runtime#82912)
        using (ExecutionContext.IsFlowSuppressed() ? (IDisposable?)null : ExecutionContext.SuppressFlow())
        {
            _runLoop = Task.Run(LoopAsync);
        }
    }

    /// <summary>
    /// Emit the provided log event to the sink. If the sink is being disposed or
    /// the app domain unloaded, then the event is ignored.
    /// </summary>
    /// <param name="logEvent">Log event to emit.</param>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    /// <remarks>
    /// The sink implements the contract that any events whose Emit() method has
    /// completed at the time of sink disposal will be flushed (or attempted to,
    /// depending on app domain state).
    /// </remarks>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent == null) throw new ArgumentNullException(nameof(logEvent));

        if (_shutdownSignal.IsCancellationRequested)
            return;

        _queue.Writer.TryWrite(logEvent);
    }

    /// <summary>
    /// Write any buffered events to the target sink. The sink remains open, and logging can continue
    /// afterwards.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_shutdownSignal.IsCancellationRequested)
            return;

        var request = new FlushRequest();

        try
        {
            // The queue may be full; wait for space rather than dropping the request.
            await _queue.Writer.WriteAsync(request, _shutdownSignal.Token).ConfigureAwait(false);

            // If the run loop terminates before the request is observed (for example, during shutdown),
            // there is nothing left to wait for.
            await Task.WhenAny(request.Completion.Task, _runLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The sink was disposed while waiting for queue space; there is nothing left to flush.
        }
        catch (ChannelClosedException)
        {
            // The sink was disposed concurrently; there is nothing left to flush.
        }
    }

    async Task LoopAsync()
    {
        var isEagerBatch = _eagerlyEmitFirstEvent;
        do
        {
            // Code from here through to the `try` block is expected to be infallible. It's structured this way because
            // any failure modes within it haven't been accounted for in the rest of the sink design, and would need
            // consideration in order for the sink to function robustly (i.e. to avoid hot/infinite looping).

            var fillBatch = Task.Delay(_batchScheduler.NextInterval);
            FlushRequest? flushRequest = null;
            do
            {
                while (_currentBatch.Count < _batchSizeLimit &&
                       !_shutdownSignal.IsCancellationRequested &&
                       _queue.Reader.TryRead(out var next))
                {
                    if (next is FlushRequest request)
                    {
                        flushRequest = request;
                        break;
                    }

                    _currentBatch.Enqueue((LogEvent)next);
                }
            } while (flushRequest == null &&
                     (_currentBatch.Count < _batchSizeLimit && !isEagerBatch || _currentBatch.Count == 0) &&
                     !_shutdownSignal.IsCancellationRequested &&
                     await TryWaitToReadAsync(_queue.Reader, fillBatch, _shutdownSignal.Token).ConfigureAwait(false));

            if (flushRequest != null)
            {
                // Everything queued before the request has now been read into `_currentBatch`.
                if (await FlushCurrentBatchAsync(flushRequest).ConfigureAwait(false))
                    isEagerBatch = false;

                continue;
            }

            try
            {
                if (_currentBatch.Count == 0)
                {
                    await _targetSink.OnEmptyBatchAsync().ConfigureAwait(false);
                }
                else
                {
                    isEagerBatch = false;

                    await EmitCurrentBatchAsync().ConfigureAwait(false);

                    _currentBatch.Clear();
                    _batchScheduler.MarkSuccess();
                }
            }
            catch (Exception ex)
            {
                _failureListener.OnLoggingFailed(this, LoggingFailureKind.Temporary, "failed emitting a batch", _currentBatch, ex);
                _batchScheduler.MarkFailure(out var shouldDropBatch, out var shouldDropQueue);

                if (shouldDropBatch)
                {
                    _failureListener.OnLoggingFailed(this, LoggingFailureKind.Permanent, "dropping the current batch", _currentBatch, ex);
                    _currentBatch.Clear();
                }

                if (shouldDropQueue)
                {
                    DrainOnFailure(LoggingFailureKind.Permanent, "dropping all queued events", ex);
                }

                // Wait out the remainder of the batch fill time so that we don't overwhelm the server. With each
                // successive failure the interval will increase. Needs special handling so that we don't need to
                // make `fillBatch` cancellable (and thus fallible).
                await Task.WhenAny(fillBatch, _waitForShutdownSignal).ConfigureAwait(false);
            }
        }
        while (!_shutdownSignal.IsCancellationRequested);

        // At this point:
        //  - The sink is being disposed
        //  - The queue has been completed
        //  - The queue may or may not be empty
        //  - The waiting batch may or may not be empty
        //  - The target sink may or may not be accepting events

        // Try flushing the rest of the queue, but bail out on any failure. Shutdown time is unbounded, but it
        // doesn't make sense to pick an arbitrary limit - a future version might add a new option to control this.
        try
        {
            while (_queue.Reader.TryPeek(out _))
            {
                while (_currentBatch.Count < _batchSizeLimit &&
                       _queue.Reader.TryRead(out var next))
                {
                    // Any remaining flush requests are completed once shutdown is complete.
                    if (next is FlushRequest)
                        continue;

                    _currentBatch.Enqueue((LogEvent)next);
                }

                if (_currentBatch.Count != 0)
                {
                    await EmitCurrentBatchAsync().ConfigureAwait(false);
                    _currentBatch.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            _failureListener.OnLoggingFailed(this, LoggingFailureKind.Permanent, "dropping the current batch", _currentBatch, ex);
            DrainOnFailure(LoggingFailureKind.Final, "failed emitting a batch during shutdown; dropping remaining queued events", ex, ignoreShutdownSignal: true);
        }
    }

    // Emits whatever is buffered, then acknowledges the flush request. Failures are reported in the same
    // way as for regular batches, but do not fail the flush. Returns true if a batch was emitted.
    async Task<bool> FlushCurrentBatchAsync(FlushRequest request)
    {
        var emitted = false;

        if (_currentBatch.Count != 0)
        {
            emitted = true;

            try
            {
                await EmitCurrentBatchAsync().ConfigureAwait(false);

                _currentBatch.Clear();
                _batchScheduler.MarkSuccess();
            }
            catch (Exception ex)
            {
                _failureListener.OnLoggingFailed(this, LoggingFailureKind.Temporary, "failed emitting a batch during flush", _currentBatch, ex);
                _batchScheduler.MarkFailure(out var shouldDropBatch, out _);

                if (shouldDropBatch)
                {
                    _failureListener.OnLoggingFailed(this, LoggingFailureKind.Permanent, "dropping the current batch", _currentBatch, ex);
                    _currentBatch.Clear();
                }
            }
        }

        // The target sink may itself buffer the batches it receives.
        if (_targetSink is IFlushableLogEventSink flushableTarget)
        {
            try
            {
                await flushableTarget.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _failureListener.OnLoggingFailed(this, LoggingFailureKind.Temporary, "failed flushing the target sink", events: null, ex);
            }
        }

        request.Completion.TrySetResult(null);

        return emitted;
    }

    // Emits the current batch, timing the call so that the latency of the target (usually remote) sink can be
    // observed. Failed batches are timed, too: a sink that has begun timing out is the case most worth measuring.
    async Task EmitCurrentBatchAsync()
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            await _targetSink.EmitBatchAsync(_currentBatch).ConfigureAwait(false);
        }
        catch (Exception ex) when (RecordEmitBatchDuration(startTimestamp, ex))
        {
        }

        RecordEmitBatchDuration(startTimestamp, error: null);
    }

    // Always returns false, so that it can be used from an exception filter.
    bool RecordEmitBatchDuration(long startTimestamp, Exception? error)
    {
        var tags = new TagList { { SelfMetrics.TagNames.BatchedSinkType, _targetSink.GetType().FullName } };
        if (error != null)
        {
            tags.Add(SelfMetrics.TagNames.ErrorType, error.GetType().FullName);
        }

#if FEATURE_STOPWATCHGETELAPSEDTIME
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
#else
        var elapsedMilliseconds = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
#endif
        SelfMetrics.BatchingEmitBatchDuration.Record(elapsedMilliseconds, tags);

        return false;
    }

    void DrainOnFailure(LoggingFailureKind kind, string message, Exception? exception, bool ignoreShutdownSignal = false)
    {
        const int bufferLimit = 1024;
        var buffer = new List<LogEvent>();

        // Not ideal, uses some CPU capacity unnecessarily and doesn't complete in bounded time. The goal is
        // to reduce memory pressure on the client if the server is offline for extended periods. May be
        // worth reviewing and possibly abandoning this.
        while (_queue.Reader.TryRead(out var next) && (ignoreShutdownSignal || !_shutdownSignal.IsCancellationRequested))
        {
            if (next is FlushRequest request)
            {
                // The sink is being shut down, so the request cannot be honored.
                request.Completion.TrySetResult(null);
                continue;
            }

            buffer.Add((LogEvent)next);

            if (buffer.Count == bufferLimit)
            {
                _failureListener.OnLoggingFailed(this, kind, message, buffer, exception);
                buffer.Clear();
            }
        }

        _failureListener.OnLoggingFailed(this, kind, message, buffer, exception);
    }

    // Wait until `reader` has items to read. Returns `false` if the `timeout` task completes, or if the reader is cancelled.
    async Task<bool> TryWaitToReadAsync(ChannelReader<object> reader, Task timeout, CancellationToken cancellationToken)
    {
        var waitToRead = _cachedWaitToRead ?? reader.WaitToReadAsync(cancellationToken).AsTask();
        _cachedWaitToRead = null;

        var completed = await Task.WhenAny(timeout, waitToRead).ConfigureAwait(false);

        // Avoid unobserved task exceptions in the cancellation and failure cases. Note that we may not end up observing
        // read task cancellation exceptions during shutdown, may be some room to improve.
        if (completed is { Exception: not null, IsCanceled: false })
        {
            _failureListener.OnLoggingFailed(this, LoggingFailureKind.Temporary, "could not read from queue", events: null, completed.Exception);
        }

        if (completed == timeout)
        {
            // Dropping references to `waitToRead` will cause it and some supporting objects to leak; disposing it
            // will break the channel and cause future attempts to read to fail. So, we cache and reuse it next time
            // around the loop.

            _cachedWaitToRead = waitToRead;
            return false;
        }

        if (waitToRead.Status is not TaskStatus.RanToCompletion)
            return false;

        return await waitToRead;
    }

    /// <inheritdoc />
    public void SetFailureListener(ILoggingFailureListener failureListener)
    {
        Guard.AgainstNull(failureListener);
        _failureListener = failureListener;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        SignalShutdown();

        try
        {
            _runLoop.Wait();
        }
        catch (Exception ex)
        {
            // E.g. the task was canceled before ever being run, or internally failed and threw
            // an unexpected exception.
            _failureListener.OnLoggingFailed(this, LoggingFailureKind.Final, "caught exception during disposal", events: null, ex);
        }

        (_targetSink as IDisposable)?.Dispose();
    }

#if FEATURE_ASYNCDISPOSABLE
        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            SignalShutdown();

            try
            {
                await _runLoop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // E.g. the task was canceled before ever being run, or internally failed and threw
                // an unexpected exception.
                _failureListener.OnLoggingFailed(this, LoggingFailureKind.Final, "caught exception during async disposal", events: null, ex);
            }

            if (_targetSink is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                (_targetSink as IDisposable)?.Dispose();
        }
#endif

    void SignalShutdown()
    {
        lock (_stateLock)
        {
            if (!_shutdownSignal.IsCancellationRequested)
            {
                // Relies on synchronization via `_stateLock`: once the writer is completed, subsequent attempts to
                // complete it will throw.
                _queue.Writer.Complete();
                _shutdownSignal.Cancel();
            }
        }
    }

    // Queued alongside events so that the read side observes it in order, after any events emitted before it.
    sealed class FlushRequest
    {
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
