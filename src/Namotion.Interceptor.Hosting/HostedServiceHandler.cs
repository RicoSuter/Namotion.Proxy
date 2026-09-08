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
    private readonly BufferBlock<Func<CancellationToken, Task>> _actions = new();
    private readonly HashSet<IHostedService> _hostedServices = [];

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
        _logger ??= _loggerResolver();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var action = await _actions.ReceiveAsync(stoppingToken);
                await action(stoppingToken);
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executeTask == null)
        {
            return;
        }

        try
        {
            if (_stoppingCts is not null)
            {
                await _stoppingCts.CancelAsync();
            }
            
            Task[] tasks;
            lock (_hostedServices)
            {
                tasks = _hostedServices
                    .Select(async hostedService =>
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
                    })
                    .ToArray();
                
                _hostedServices.Clear();
            }
            
            await Task.WhenAll(tasks);
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
            if (_hostedServices.Add(hostedService))
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
            if (_hostedServices.Remove(hostedService))
            {
                PostStopService(hostedService, null);
            }
        }
    }

    internal async Task AttachHostedServiceAsync(
        IHostedService hostedService, IInterceptorSubjectContext context, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        lock (_hostedServices)
        {
            if (_hostedServices.Add(hostedService))
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
        var tcs = new TaskCompletionSource();
        lock (_hostedServices)
        {
            if (_hostedServices.Remove(hostedService))
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
        _actions.Post(async token =>
        {
            try
            {
                await Task.Delay(50, token); // TODO: Fix small delay to let sync property assignments/deserialization complete

                _logger?.LogInformation("Starting attached hosted service {Service}.", hostedService.ToString());
                await hostedService.StartAsync(token);
                tcs?.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
            }
            finally
            {
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
        });
    }

    private void PostStopService(IHostedService hostedService, TaskCompletionSource? tcs)
    {
        _actions.Post(async token =>
        {
            try
            {
                await Task.Delay(50, token); // TODO: Fix small delay to let sync property assignments/deserialization complete

                _logger?.LogInformation("Stopping detached hosted service {Service}.", hostedService.ToString());
                await hostedService.StopAsync(token);
                tcs?.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs?.TrySetException(ex);
            }
        });
    }

    public void Dispose()
    {
        _stoppingCts?.Cancel();
    }
}
