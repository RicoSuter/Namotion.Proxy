using HomeBlaze.Services;
using Microsoft.AspNetCore.Components;
using Namotion.Interceptor;

namespace HomeBlaze.Host.Components;

/// <summary>
/// Base class for HomeBlaze Blazor components with access to the root subject and context.
/// Property tracking is now handled by <see cref="Namotion.Interceptor.Blazor.TrackingScope"/>.
/// </summary>
public abstract class HomeBlazorComponentBase : ComponentBase
{
    [Inject]
    protected RootManager RootManager { get; set; } = null!;

    /// <summary>
    /// The root subject. Available after OnInitialized.
    /// </summary>
    protected IInterceptorSubject? Root => RootManager.Root;

    /// <summary>
    /// The root subject's context. Available after OnInitialized.
    /// </summary>
    protected IInterceptorSubjectContext? RootContext => Root?.TryGetContext();
}
