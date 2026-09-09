using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

[RunsAfter(typeof(ContextInheritanceHandler))]
internal class HostedServiceHandler : IHostedService, ILifecycleHandler, IDisposable
{
    private ILogger? _logger;

    private Task? _executeTask;
    private CancellationTokenSource? _stoppingCts;

    private readonly Func<ILogger?> _loggerResolver;
    private readonly BufferBlock<(HostedServiceStartupScope? Scope, Task Ready, Func<CancellationToken, Task> Execute)> _actions = new();
    private readonly Dictionary<IHostedService, CancellationTokenSource> _deferredStarts = [];
    private bool _isStopping;
    private bool IsStopping => _isStopping || _stoppingCts?.IsCancellationRequested == true;
    private readonly HashSet<IHostedService> _hostedServices = [];
    private readonly AsyncLocal<HostedServiceStartupScope?> _startupScope = new();

    internal HostedServiceStartupScope DeferStartup() => new(_startupScope);

    public HostedServiceHandler(Func<ILogger?> loggerResolver)
    {
        _loggerResolver = loggerResolver;
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        _logger ??= _loggerResolver();

        if (change.IsContextAttach)
        {
            if (change.Subject is IHostedService hostedService)
            {
                AttachHostedService(hostedService, change.Subject.Context);
            }

            foreach (var hostedService2 in change.Subject.GetAttachedHostedServices())
            {
                AttachHostedService(hostedService2, change.Subject.Context);
            }
        }
        else if (change.IsContextDetach)
        {
            if (change.Subject is IHostedService hostedService)
            {
                DetachHostedService(hostedService);
            }

            foreach (var attachedHostedService in change.Subject.GetAttachedHostedServices())
            {
                // The extension, not this handler's own method: it also clears the subject's
                // attached-services data, which a plain stop would leave behind (pinned by
                // HostedServiceHandlerTests.WhenSubjectServiceIsDetached_ThenHostedServiceIsStopped).
                // It re-resolves the handler through the subject's context, so a subject whose own
                // context has already lost its fallback stops resolving one; that is pre-existing and
                // narrower than losing the data cleanup.
                change.Subject.DetachHostedService(attachedHostedService);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_executeTask is not null)
        {
            return _executeTask.IsCompleted ? _executeTask : Task.CompletedTask;
        }
        
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executeTask = ExecuteAsync(_stoppingCts.Token);
        return _executeTask.IsCompleted ? _executeTask : Task.CompletedTask;
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The long-lived action loop must not inherit the scope in which the host was started.
        _startupScope.Value = null;
        _logger ??= _loggerResolver();

        var pending = new List<(HostedServiceStartupScope? Scope, Task Ready, Func<CancellationToken, Task> Execute)>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var readyIndex = pending.FindIndex(action => action.Scope?.IsReady == true || action.Ready.IsCompleted);
                    if (readyIndex >= 0)
                    {
                        // A scope may release while the scan is in progress. Recheck earlier
                        // entries so starts released together retain attachment order.
                        if (readyIndex > 0)
                        {
                            readyIndex = pending.FindIndex(action => action.Scope?.IsReady == true || action.Ready.IsCompleted);
                        }
                        var action = pending[readyIndex];
                        pending.RemoveAt(readyIndex);
                        await action.Execute(stoppingToken);
                    }
                    else if (pending.Count == 0)
                    {
                        var action = await _actions.ReceiveAsync(stoppingToken);
                        if (action.Scope?.IsReady == true || action.Ready.IsCompleted)
                        {
                            await action.Execute(stoppingToken);
                        }
                        else
                        {
                            pending.Add(action);
                        }
                    }
                    else if (_actions.TryReceive(out var action))
                    {
                        pending.Add(action);
                    }
                    else
                    {
                        // Scope readiness must not occupy the consumer: unrelated starts and stops
                        // can be queued while configuration waits for those services.
                        using var wakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        await Task.WhenAny(pending.Select(action => action.Ready)
                            .Append(_actions.OutputAvailableAsync(wakeCancellation.Token)));
                        await wakeCancellation.CancelAsync();
                    }
                }
                catch (Exception exception)
                {
                    if (exception is not OperationCanceledException)
                    {
                        _logger?.LogError(exception, "Failed to execute hosted service action.");
                    }
                }
            }
        }
        finally
        {
            lock (_hostedServices)
            {
                _isStopping = true;
            }
            while (_actions.TryReceive(out var action))
            {
                pending.Add(action);
            }

            foreach (var action in pending)
            {
                // Cancelled for starts, which must not begin during shutdown. A stop reads the same
                // token as "shut down without waiting" and still runs, since the service it stops
                // has already left _hostedServices and StopAsync above therefore skipped it.
                await action.Execute(new CancellationToken(true));
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executeTask == null)
        {
            return;
        }

        lock (_hostedServices)
        {
            _isStopping = true;
        }

        try
        {
            if (_stoppingCts is not null)
            {
                await _stoppingCts.CancelAsync();
            }
            
            IHostedService[] started;
            lock (_hostedServices)
            {
                // Every attached service, including one whose deferred start never ran: shutdown is
                // the last owner of the stop, and skipping those would orphan a service that a
                // detach arriving during shutdown has already been refused
                // (WhenShutdownCancellationDetachesADeferredService_ThenShutdownStillStopsIt).
                // Telling the two apart needs a record of which starts actually ran, which this
                // handler does not keep.
                started = _hostedServices.ToArray();

                _hostedServices.Clear();
            }

            // Materialized outside the lock: an async lambda runs up to its first await inline, so
            // building the tasks under the lock would enter every StopAsync while holding it.
            await Task.WhenAll(started.Select(async hostedService =>
            {
                try
                {
                    _logger?.LogInformation("Stopping hosted service {Service}.", hostedService.ToString());
                    await hostedService.StopAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    if (exception is not OperationCanceledException)
                    {
                        _logger?.LogError(exception, "Failed to stop hosted service {Service}.", hostedService.ToString());
                    }
                }
            }));
        }
        finally
        {
            await _executeTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
    
    internal void AttachHostedService(IHostedService hostedService, IInterceptorSubjectContext context)
    {
        lock (_hostedServices)
        {
            if (!IsStopping && _hostedServices.Add(hostedService))
            {
                // Starting is queued, not inline, so the service is NOT running when this returns.
                // Anything that treats "the graph has finished starting" as a completion point would
                // otherwise reach it while this start is still on its way in - concretely, a source
                // attached here would not yet have registered with its SourceMonitor, and a
                // synchronization wait would complete against a tree that is not synchronized.
                // Holds are taken HERE, synchronously, rather than inside the queued action, so
                // there is no window between the attach and the hold in which completion can fire.
                // They are released once the start has actually run (see PostStartService).
                //
                // A nested attach composes: a service that attaches children during its own
                // StartAsync takes their holds before its own is released, so the count never
                // reaches zero in between.
                PostStartService(hostedService, null, TakeStartupHolds(context));
            }
        }
    }

    /// <summary>
    /// Takes a completion hold on every deferrer reachable from <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Empty for an application that configures no deferring subsystem (no source monitoring, for
    /// example), which is the common case and costs one empty-array check per attach.
    /// </remarks>
    private static IDisposable[] TakeStartupHolds(IInterceptorSubjectContext context)
    {
        var deferrers = context.GetServices<IStartupCompletionDeferrer>();
        if (deferrers.IsEmpty)
        {
            return [];
        }

        var holds = new IDisposable[deferrers.Length];
        for (var index = 0; index < deferrers.Length; index++)
        {
            holds[index] = deferrers[index].DeferCompletion();
        }

        return holds;
    }

    internal void DetachHostedService(IHostedService hostedService)
    {
        lock (_hostedServices)
        {
            // Shutdown owns the stop once cancellation begins, including during its callbacks.
            if (!IsStopping && _hostedServices.Remove(hostedService))
            {
                PostStopService(hostedService, null);
            }
        }
    }

    internal async Task AttachHostedServiceAsync(
        IHostedService hostedService, IInterceptorSubjectContext context, CancellationToken cancellationToken)
    {
        // Continuations run off the completing thread: the action loop completes this, and a caller
        // that resumes inline would otherwise run its own work on the loop's only thread.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_hostedServices)
        {
            if (IsStopping)
            {
                tcs.TrySetCanceled();
            }
            else if (_hostedServices.Add(hostedService))
            {
                // Holds here too, even though this overload's caller awaits the start: the caller
                // being blocked does not block the startup-completion gate, so without a hold
                // ApplicationStarted can fire, drop the count to zero and let a wait complete
                // vacuously while this start is still sitting in the queue.
                PostStartService(hostedService, tcs, TakeStartupHolds(context));
            }
            else
            {
                tcs.TrySetResult(); // Already attached
            }
        }

        await tcs.Task.WaitAsync(cancellationToken);
    }
    
    internal async Task DetachHostedServiceAsync(IHostedService hostedService, CancellationToken cancellationToken)
    {
        // See AttachHostedServiceAsync for why the continuations must not run inline.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_hostedServices)
        {
            if (IsStopping)
            {
                tcs.TrySetCanceled();
            }
            else if (_hostedServices.Remove(hostedService))
            {
                PostStopService(hostedService, tcs);
            }
            else
            {
                tcs.TrySetResult(); // Already removed
            }
        }

        await tcs.Task.WaitAsync(cancellationToken);
    }

    private void PostStartService(
        IHostedService hostedService, TaskCompletionSource? tcs, IDisposable[]? startupHolds = null)
    {
        // The action loop has a different execution flow from the code constructing the subject.
        var startupScope = _startupScope.Value;
        var cancellation = startupScope is null ? null : new CancellationTokenSource();
        if (cancellation is not null)
        {
            _deferredStarts.Add(hostedService, cancellation);
        }
        var readiness = startupScope?.WaitAsync(cancellation!.Token) ?? Task.CompletedTask;
        _actions.Post((startupScope, readiness, async token =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (cancellation is not null)
                {
                    await readiness.ConfigureAwait(false);
                    lock (_hostedServices)
                    {
                        token.ThrowIfCancellationRequested();
                        cancellation.Token.ThrowIfCancellationRequested();
                        RemoveDeferredStart(hostedService, cancellation);
                    }
                }

                _logger?.LogInformation("Starting attached hosted service {Service}.", hostedService.ToString());
                await hostedService.StartAsync(token);
                tcs?.TrySetResult();
            }
            catch (OperationCanceledException exception)
            {
                tcs?.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
            }
            finally
            {
                if (cancellation is not null)
                {
                    lock (_hostedServices)
                    {
                        // Under the lock, so a detach cannot be holding this source while it is
                        // cancelled and disposed here: it takes the entry out under the same lock,
                        // and whichever of the two removes it owns the rest of its lifetime.
                        RemoveDeferredStart(hostedService, cancellation);
                        cancellation.Cancel();
                        cancellation.Dispose();
                    }
                }
                // In a finally, so a start that throws or is cancelled releases its hold too.
                // Leaking a hold would block every synchronization wait on the tree forever - a
                // hang rather than a wrong answer, which is the safer direction, but still a hang.
                if (startupHolds is not null)
                {
                    foreach (var hold in startupHolds)
                    {
                        try
                        {
                            hold.Dispose();
                        }
                        catch (Exception holdException)
                        {
                            // One deferrer throwing must not strand the others: a leaked hold blocks
                            // every wait on that tree forever. Logged rather than swallowed, since
                            // this used to surface through the action loop.
                            _logger?.LogError(
                                holdException, "Releasing a startup completion hold threw and was ignored.");
                        }
                    }
                }
            }
        }));
    }

    /// <summary>
    /// Drops the deferred-start entry for <paramref name="hostedService"/>, if it is still the one
    /// <paramref name="cancellation"/> belongs to. Callers must hold the lock on
    /// <see cref="_hostedServices"/>.
    /// </summary>
    /// <remarks>
    /// A detach and re-attach in between installs a newer source for the same service, and dropping
    /// that one would leave the newer start with nothing left to cancel it.
    /// </remarks>
    private void RemoveDeferredStart(IHostedService hostedService, CancellationTokenSource cancellation)
    {
        if (_deferredStarts.TryGetValue(hostedService, out var current) && ReferenceEquals(current, cancellation))
        {
            _deferredStarts.Remove(hostedService);
        }
    }

    private void PostStopService(IHostedService hostedService, TaskCompletionSource? tcs)
    {
        if (_deferredStarts.Remove(hostedService, out var deferredStart))
        {
            deferredStart.Cancel();
        }

        if (IsStopping)
        {
            tcs?.TrySetCanceled();
            return;
        }

        _actions.Post((null, Task.CompletedTask, async token =>
        {
            try
            {
                // A grace period inherited from the start path, which no longer has one; nothing is
                // known to depend on it here. Suppressing the cancellation rather than throwing is
                // what lets shutdown's drain reach the stop below: the service has already left
                // _hostedServices, so nothing else stops it.
                await Task.Delay(50, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                _logger?.LogInformation("Stopping detached hosted service {Service}.", hostedService.ToString());
                await hostedService.StopAsync(token);
                tcs?.TrySetResult();
            }
            catch (OperationCanceledException exception)
            {
                tcs?.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
            }
        }));
    }

    public void Dispose()
    {
        lock (_hostedServices)
        {
            _isStopping = true;
        }
        _stoppingCts?.Cancel();
    }
}
