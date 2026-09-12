using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// A hosted service bound to a subject. The handler creates the instance when the subject enters the
/// graph and disposes it when the subject leaves, so the same attachment yields a fresh instance on
/// every re-attach.
/// </summary>
public interface IHostedServiceAttachment
{
    /// <summary>The running instance, or null when nothing is running.</summary>
    IHostedService? Current { get; }

    /// <summary>The exception from the last failed transition, or null.</summary>
    Exception? Fault { get; }
}

/// <inheritdoc />
public interface IHostedServiceAttachment<out T> : IHostedServiceAttachment
    where T : class, IHostedService
{
    /// <inheritdoc cref="IHostedServiceAttachment.Current" />
    new T? Current { get; }
}

/// <summary>
/// Lets the handler reach the target from a non generic attachment. An abstract base class cannot
/// serve here: the generic and non generic <c>Current</c> differ only by return type, so declaring
/// both on one class is CS0102. The non generic one is implemented explicitly instead.
/// </summary>
internal interface IHostedServiceAttachmentTarget
{
    HostedServiceTarget Target { get; }
}

internal sealed class HostedServiceAttachment<T> : IHostedServiceAttachment<T>, IHostedServiceAttachmentTarget
    where T : class, IHostedService
{
    public HostedServiceAttachment(HostedServiceTarget target)
    {
        Target = target;
    }

    public HostedServiceTarget Target { get; }

    public T? Current => (T?)Target.Current;

    public Exception? Fault => Target.Fault;

    IHostedService? IHostedServiceAttachment.Current => Target.Current;
}
