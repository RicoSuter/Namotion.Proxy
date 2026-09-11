using HomeBlaze.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.OpcUa;

/// <summary>
/// The subject a <see cref="SingleAttachmentHost{TService}"/> maintains: the state it reports, the
/// configuration it starts from and the instance it builds.
/// </summary>
/// <remarks>
/// Implement explicitly whatever the wrapper does not publish anyway, so hosting a service adds nothing
/// to what the subject shows a user. <see cref="Status"/>, <see cref="StatusMessage"/> and
/// <see cref="IsEnabled"/> are already the generated partial properties.
/// </remarks>
internal interface IAttachmentOwner<TService> : IInterceptorSubject
    where TService : class, IHostedService
{
    ServiceStatus Status { get; set; }

    /// <summary>The text behind <see cref="ServiceStatus.Error"/>, and null at every other status.</summary>
    string? StatusMessage { get; set; }

    /// <summary>Whether a start is wanted, read by the run loop and after a configuration edit.</summary>
    bool IsEnabled { get; }

    /// <summary>What the wrapper calls itself in its own log messages.</summary>
    string LogName { get; }

    /// <summary>The endpoint or path those messages name, so a host running several is readable.</summary>
    string LogTarget { get; }

    /// <summary>
    /// The message to report when the configuration cannot produce an instance, or null when it can.
    /// Read before anything the start path waits for, so an unconfigured wrapper reports at once rather
    /// than sitting in a wait whose result it cannot use.
    /// </summary>
    string? GetConfigurationError();

    /// <summary>
    /// Waits for whatever has to be in place before the attach, and returns false when the start must
    /// abandon, in which case the implementation has reported that outcome itself. The start path is never reached through the subject's own StopAsync, so parking here
    /// cannot sit inside the handler's stop transition for that subject.
    /// </summary>
    ValueTask<bool> WaitUntilStartableAsync(CancellationToken cancellationToken) => new(true);

    /// <summary>
    /// Builds the instance. Invoked by the handler on every attach, so it reads the configuration each
    /// time rather than capturing a snapshot: a re-attach must produce a new instance, because the
    /// handler has already disposed the previous one.
    /// </summary>
    TService CreateInstance();

    /// <summary>Publishes the running instance's diagnostics.</summary>
    void ApplyDiagnostics(TService instance);

    /// <summary>Clears those diagnostics, which read null whenever nothing is running.</summary>
    void ResetDiagnostics();

    /// <summary>
    /// Drops what the instance published into the subject beyond its diagnostics, for a wrapper that
    /// publishes anything else. Called wherever the attachment stops holding an instance whose output
    /// can still be trusted, and deliberately never from the unwind, which runs while it is still live.
    /// </summary>
    void DropInstanceState()
    {
    }
}

/// <summary>
/// The one hosted service a wrapper subject owns: the attachment itself, the gate that serializes
/// everything touching it, the start and stop paths, the diagnostics poll and the unwind.
/// </summary>
internal sealed class SingleAttachmentHost<TService>
    where TService : class, IHostedService
{
    private const string NotAttachedMessage = "Not attached to a running host, so nothing was started";

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    private readonly IAttachmentOwner<TService> _owner;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    /// Serializes the start path, the stop path and the diagnostics poll against each other, and is
    /// what publishes <see cref="_attachment"/> between the threads that touch it. Held across the
    /// whole of each path, because the guard on the attachment spans the attach's own await. Never
    /// waited for from the subject's own StopAsync or from the unwind in <see cref="RunAsync"/>, so it
    /// can never park inside the handler's stop transition for that subject.
    /// </summary>
    private readonly SemaphoreSlim _attachmentGate = new(1, 1);

    /// <summary>
    /// The single attachment the wrapper owns, or null when nothing is attached. Read and written only
    /// under <see cref="_attachmentGate"/>. Each attach builds its own instance, so a second one would
    /// leave both live with the subject's state bound to one of them, while the other is unreachable
    /// from here and so can never be stopped from here.
    /// </summary>
    private IHostedServiceAttachment<TService>? _attachment;

    /// <remarks>
    /// <paramref name="pollInterval"/> is null for the default. It is injectable because the
    /// reconciliation is the only path into several of the states below, and a suite that runs in
    /// seconds cannot reach them on the production interval.
    /// </remarks>
    public SingleAttachmentHost(IAttachmentOwner<TService> owner, ILogger logger, TimeSpan? pollInterval = null)
    {
        _owner = owner;
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>
    /// The wrapper's own background loop: the start it wants on startup, the diagnostics poll, and the
    /// unwind that runs when the subject leaves the graph or the host shuts down.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_owner.IsEnabled)
        {
            await StartAsync(stoppingToken);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TryUpdateFromAttachment();
                await Task.Delay(_pollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Deliberately does NOT detach, and deliberately does not take the gate: this unwind runs inside
        // the handler's own stop transition for this subject, and either one would wait on that
        // transition. See docs/hosting.md#do-not-detach-from-your-own-stop-path. The handler owns the
        // detach on graph events; the explicit detach lives on the Stop operation and
        // ApplyConfigurationAsync, neither of which is reached through StopAsync.
        //
        // What the instance published is left alone for the same reason: it is still running here and is
        // stopped only after this unwind returns, so dropping its output would pull it out from under
        // something live. UpdateFromAttachment drops it instead, on the first poll or restart that sees
        // the attachment holding no instance.
        ReportStopped();
    }

    /// <summary>
    /// Applies a configuration edit by stopping what is attached and starting it again from the edited
    /// configuration.
    /// </summary>
    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);

        // Guarded here rather than left to the run loop's caller-side check: without it an edit that
        // disables the wrapper stops it and starts it again in the same call.
        if (_owner.IsEnabled)
        {
            await StartAsync(cancellationToken);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAttachmentGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            _owner.Status = ServiceStatus.Starting;
            _owner.StatusMessage = null;

            if (_owner.GetConfigurationError() is { } configurationError)
            {
                _owner.Status = ServiceStatus.Error;
                _owner.StatusMessage = configurationError;
                return;
            }

            if (!await _owner.WaitUntilStartableAsync(cancellationToken))
            {
                return;
            }

            // The attachment survives a context detach, so on re-attach the handler re-invokes the
            // factory itself. Without this guard a restarted run loop would attach a second instance
            // alongside the one the handler just re-created.
            if (_attachment is null)
            {
                // The awaited overload, and CancellationToken.None rather than the caller's token. The
                // returned handle is the only record of the attachment, and the transition runs to
                // completion whatever the token does, so a cancelled wait would strand a live
                // attachment with nothing pointing at it and let the next start attach a second
                // instance. The wait is bounded: the instance is a BackgroundService whose StartAsync
                // returns at its first await, and a start appended during shutdown returns without
                // creating anything.
                var attachment = await _owner.AttachHostedServiceAsync(_owner.CreateInstance, CancellationToken.None);
                if (attachment.Current is null)
                {
                    // The awaited overload appends nothing when the context has no handler, when the
                    // subject is not in the graph and when the host is draining, and it throws rather
                    // than returning when a start faulted, so no instance here means nothing was
                    // started and nothing will be before a context re-attach. Reported as an error and
                    // dropped rather than kept, which would report Starting forever.
                    _owner.DetachHostedService(attachment);

                    _owner.Status = ServiceStatus.Error;
                    _owner.StatusMessage = NotAttachedMessage;
                    _logger.LogWarning(
                        "{Service} for {Target} was not started: the subject is not attached to a running host.",
                        _owner.LogName, _owner.LogTarget);
                    return;
                }

                _attachment = attachment;
            }

            UpdateFromAttachment();
            _logger.LogInformation("{Service} started for {Target}", _owner.LogName, _owner.LogTarget);
        }
        catch (Exception exception)
        {
            // No OperationCanceledException filter: the only wait on the caller's token inside this try
            // is the startable wait, which reports its own cancellation and returns instead of throwing,
            // so the filter could only catch a genuine start failure that happens to surface as one and
            // would report it as a clean stop. A cancelled gate wait leaves through the gate helper.
            _owner.Status = ServiceStatus.Error;
            _owner.StatusMessage = exception.Message;
            _logger.LogError(exception, "Failed to start {Service} for {Target}", _owner.LogName, _owner.LogTarget);
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!await TryEnterAttachmentGateAsync(cancellationToken))
        {
            return;
        }

        try
        {
            if (_attachment is not { } attachment)
            {
                // A wrapper whose start failed sits at Error with no attachment, and a stop is the only
                // thing that can take it out of there: disabling it in the configuration stops it and
                // never starts it again, so a status left alone here is left alone forever. What such a
                // start published before it failed goes with it: nothing is attached, so dropping it
                // cannot pull anything out from under a live instance.
                _owner.DropInstanceState();
                ReportStopped();
                return;
            }

            // A wrapper that faulted with its attachment still held arrives here from Error. The message
            // is the text behind that status alone, and is cleared ahead of the status write so it never
            // stands under one that is not Error.
            _owner.StatusMessage = null;
            _owner.Status = ServiceStatus.Stopping;

            try
            {
                // CancellationToken.None, symmetrically with the attach: the detach removes the
                // attachment before it stops anything, so a cancelled wait would return while the
                // instance is still stopping with nothing left pointing at it, and the next start would
                // create a second one alongside it. The wait is bounded by the stop the handler runs.
                await _owner.DetachHostedServiceAsync(attachment, CancellationToken.None);
                _logger.LogInformation("{Service} stopped", _owner.LogName);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to stop {Service}", _owner.LogName);
            }

            // Cleared from the subject's own attachment set rather than from the detach having
            // returned: the field must never read null while an attachment is still live, or the guard
            // in the start path attaches a second instance over it. A detach that threw before removing
            // the attachment did not stop anything.
            if (!_owner.GetHostedServiceAttachments().Contains(attachment))
            {
                _attachment = null;
                _owner.DropInstanceState();
            }

            ReportStopped();
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// Reports a stop. <see cref="IAttachmentOwner{TService}.DropInstanceState"/> is deliberately not
    /// part of it: each caller decides that for itself.
    /// </summary>
    private void ReportStopped()
    {
        _owner.Status = ServiceStatus.Stopped;
        _owner.StatusMessage = null;
        _owner.ResetDiagnostics();
    }

    /// <summary>
    /// Polls the attachment from the run loop. Skips the round rather than waiting when a start or a
    /// stop holds the gate: that caller reconciles the state itself before it releases, and a poll that
    /// waited here would sit in the way of the shutdown that cancels it. Ungated, a poll preempted
    /// inside <see cref="UpdateFromAttachment"/> resumes after a completed stop and writes Running over
    /// Stopped, which no later poll corrects because the attachment is null by then.
    /// </summary>
    private void TryUpdateFromAttachment()
    {
        if (!_attachmentGate.Wait(0))
        {
            return;
        }

        try
        {
            UpdateFromAttachment();
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    /// <summary>
    /// Reconciles the reported status and the diagnostics with what the attachment actually holds. The
    /// handler creates, faults and disposes the instance on its own chain (a context re-attach re-invokes
    /// the factory without going through the wrapper), so polling the handle is the only way those
    /// outcomes reach the UI. Must be called with <see cref="_attachmentGate"/> held.
    /// </summary>
    private void UpdateFromAttachment()
    {
        if (_attachment is not { } attachment || _owner.Status is ServiceStatus.Stopping or ServiceStatus.Stopped)
        {
            return;
        }

        if (attachment.Fault is { } fault)
        {
            _owner.Status = ServiceStatus.Error;
            _owner.StatusMessage = fault.Message;
            _owner.ResetDiagnostics();
            _owner.DropInstanceState();
            return;
        }

        // Cleared here and not only on the start path: the handler clears a stale fault on the next
        // successful transition, so a recovered attachment would otherwise keep reporting Running beside
        // the error text of the transition that failed.
        _owner.StatusMessage = null;

        if (attachment.Current is not { } instance)
        {
            // Attached but not yet created: the handler's start transition has not run, or it has just
            // disposed the previous instance on a re-attach. Dropping what that instance published
            // belongs here rather than in the unwind, which runs while it is still live.
            _owner.DropInstanceState();
            _owner.Status = ServiceStatus.Starting;
            return;
        }

        _owner.Status = ServiceStatus.Running;
        _owner.ApplyDiagnostics(instance);
    }

    /// <summary>
    /// Waits for the attachment gate. Returns false when the wait was cancelled, in which case the caller
    /// holds nothing, must not release it and must report nothing: whoever holds the gate is maintaining
    /// the reported state. The caller's token is honoured so the wait can never stand in the way of the
    /// stop that cancels it, which is what keeps the run loop's own start off the handler's stop chain.
    /// </summary>
    private async Task<bool> TryEnterAttachmentGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _attachmentGate.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
