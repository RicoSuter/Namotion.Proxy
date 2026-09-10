using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Extension methods for attaching and detaching hosted services to/from interceptor subjects.
/// </summary>
public static class InterceptorHostingExtensions
{
    // TODO: Refactor to reuse code, lots of duplication here
    
    private const string AttachedHostedServicesKey = "Namotion.Hosting.AttachedHostedServices";

    /// <summary>
    /// Gets all hosted services currently attached to the subject.
    /// Returns an immutable snapshot that is thread-safe and allocation-free to enumerate.
    /// </summary>
    /// <param name="subject">The subject to get attached hosted services from.</param>
    /// <returns>An immutable array of attached hosted services, or an empty array if none are attached.</returns>
    public static ImmutableArray<IHostedService> GetAttachedHostedServices(this IInterceptorSubject subject)
    {
        var value = subject.Data.GetOrAdd((null, AttachedHostedServicesKey), _ => null);
        if (value is ImmutableArray<IHostedService> array)
        {
            return array;
        }
        return [];
    }

    /// <summary>
    /// Attaches a hosted service to the subject. The service will be started when the subject's
    /// lifecycle handler processes the attachment. This method does not wait for the service to start.
    /// </summary>
    /// <param name="subject">The subject to attach the hosted service to.</param>
    /// <param name="hostedService">The hosted service to attach.</param>
    public static bool AttachHostedService(this IInterceptorSubject subject, IHostedService hostedService)
    {
        bool wasAdded = false;
        subject.Data.AddOrUpdate((null, AttachedHostedServicesKey),
            _ =>
            {
                wasAdded = true;
                return ImmutableArray.Create(hostedService);
            },
            (_, value) =>
            {
                var array = value is ImmutableArray<IHostedService> arr ? arr : [];
                if (!array.Contains(hostedService))
                {
                    wasAdded = true;
                    return array.Add(hostedService);
                }
                return array;
            });

        if (wasAdded)
        {
            // Context passed so the start is held open until it has actually run: this overload
            // queues the start and returns without waiting for it, so anything treating "the graph
            // has finished starting" as a completion point would otherwise pass it too early.
            var context = subject.TryGetContext();
            var hostedServiceHandler = context?.TryGetService<HostedServiceHandler>();
            hostedServiceHandler?.AttachHostedService(hostedService, context!);
        }

        return wasAdded;
    }

    /// <summary>
    /// Detaches a hosted service from the subject. The service will be stopped when the subject's
    /// lifecycle handler processes the detachment. This method does not wait for the service to stop.
    /// </summary>
    /// <param name="subject">The subject to detach the hosted service from.</param>
    /// <param name="hostedService">The hosted service to detach.</param>
    public static bool DetachHostedService(this IInterceptorSubject subject, IHostedService hostedService)
    {
        bool wasRemoved = false;
        subject.Data.AddOrUpdate((null, AttachedHostedServicesKey),
            _ => null,
            (_, value) =>
            {
                if (value is ImmutableArray<IHostedService> array && array.Contains(hostedService))
                {
                    wasRemoved = true;
                    var newArray = array.Remove(hostedService);
                    return newArray.Length > 0 ? newArray : null;
                }
                return value;
            });

        if (wasRemoved)
        {
            var hostedServiceHandler = subject.TryGetContext()?.TryGetService<HostedServiceHandler>();
            hostedServiceHandler?.DetachHostedService(hostedService);
        }

        return wasRemoved;
    }

    /// <summary>
    /// Attaches a hosted service to the subject and waits for it to start.
    /// </summary>
    public static async Task<bool> AttachHostedServiceAsync(
        this IInterceptorSubject subject,
        IHostedService hostedService,
        CancellationToken cancellationToken)
    {
        bool wasAdded = false;
        subject.Data.AddOrUpdate((null, AttachedHostedServicesKey),
            _ =>
            {
                wasAdded = true;
                return ImmutableArray.Create(hostedService);
            },
            (_, value) =>
            {
                var array = value is ImmutableArray<IHostedService> arr ? arr : [];
                if (!array.Contains(hostedService))
                {
                    wasAdded = true;
                    return array.Add(hostedService);
                }
                return array;
            });

        if (wasAdded)
        {
            var context = subject.TryGetContext();
            if (context?.TryGetService<HostedServiceHandler>() is { } hostedServiceHandler)
            {
                await hostedServiceHandler.AttachHostedServiceAsync(hostedService, context, cancellationToken);
            }
        }

        return wasAdded;
    }

    /// <summary>
    /// Detaches a hosted service from the subject and waits for it to stop.
    /// </summary>
    public static async Task<bool> DetachHostedServiceAsync(
        this IInterceptorSubject subject,
        IHostedService hostedService,
        CancellationToken cancellationToken)
    {
        var wasRemoved = false;
        subject.Data.AddOrUpdate((null, AttachedHostedServicesKey),
            _ => null,
            (_, value) =>
            {
                if (value is ImmutableArray<IHostedService> array && array.Contains(hostedService))
                {
                    wasRemoved = true;
                    var newArray = array.Remove(hostedService);
                    return newArray.Length > 0 ? newArray : null;
                }
                return value;
            });

        if (wasRemoved)
        {
            var hostedServiceHandler = subject.TryGetContext()?.TryGetService<HostedServiceHandler>();
            if (hostedServiceHandler != null)
            {
                await hostedServiceHandler.DetachHostedServiceAsync(hostedService, cancellationToken);
            }
        }

        return wasRemoved;
    }
}
