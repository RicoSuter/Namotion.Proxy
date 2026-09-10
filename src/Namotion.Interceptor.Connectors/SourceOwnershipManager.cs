using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Manages source ownership for properties with automatic lifecycle cleanup.
/// </summary>
/// <remarks>
/// This class maintains a set of properties owned by a source and handles:
/// <list type="bullet">
/// <item>Setting/removing source ownership when properties are claimed/released</item>
/// <item>Automatic cleanup when subjects are detached from the object graph</item>
/// <item>Cleanup of all owned properties when disposed</item>
/// </list>
/// </remarks>
public class SourceOwnershipManager : IDisposable
{
    private readonly ISubjectSource _source;
    private readonly Action<PropertyReference>? _onReleasing;
    private readonly Action<IInterceptorSubject>? _onSubjectDetaching;
    private readonly HashSet<PropertyReference> _properties = [];
    private readonly Lock _lock = new();
    private readonly LifecycleInterceptor _lifecycle;

    private int _disposed;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceOwnershipManager"/> class.
    /// </summary>
    /// <param name="source">The source that owns the properties.</param>
    /// <param name="onReleasing">Optional callback invoked before a property is released.</param>
    /// <param name="onSubjectDetaching">Optional callback invoked before a subject's properties are released due to detachment.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source's context does not have a <see cref="LifecycleInterceptor"/> configured.
    /// Call <c>WithLifecycle()</c> on the context to enable lifecycle tracking.
    /// </exception>
    public SourceOwnershipManager(
        ISubjectSource source,
        Action<PropertyReference>? onReleasing = null,
        Action<IInterceptorSubject>? onSubjectDetaching = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _onReleasing = onReleasing;
        _onSubjectDetaching = onSubjectDetaching;

        _lifecycle = source.RootSubject.TryGetContext()?.TryGetLifecycleInterceptor()
            ?? throw new InvalidOperationException(
                $"LifecycleInterceptor not configured for {source.GetType().Name}. " +
                "Call WithLifecycle() on the context to enable automatic cleanup when subjects are detached.");

        _lifecycle.SubjectDetaching += OnSubjectDetaching;
    }

    /// <summary>
    /// Gets the properties currently owned by this source.
    /// </summary>
    public IReadOnlyCollection<PropertyReference> Properties
    {
        get
        {
            lock (_lock)
            {
                return _properties.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets how many properties this source currently owns.
    /// </summary>
    /// <remarks>
    /// Maintained rather than derived from <see cref="Properties"/>, which copies the whole set on
    /// every read. Recomputed at each mutation site, all of which already hold the lock.
    /// </remarks>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Claims source ownership for a property.
    /// </summary>
    /// <param name="property">The property to claim.</param>
    /// <returns>
    /// <c>true</c> if the property was successfully claimed or already owned by this source;
    /// <c>false</c> if the property is already owned by a different source.
    /// </returns>
    public bool ClaimSource(PropertyReference property)
    {
        lock (_lock)
        {
            if (!property.SetSource(_source))
            {
                return false;
            }

            _properties.Add(property);
            Volatile.Write(ref _count, _properties.Count);
            return true;
        }
    }

    /// <summary>
    /// Releases source ownership from a property.
    /// </summary>
    /// <param name="property">The property to release.</param>
    public void ReleaseSource(PropertyReference property)
    {
        lock (_lock)
        {
            if (_properties.Remove(property))
            {
                _onReleasing?.Invoke(property);
                property.RemoveSource(_source);
            }

            Volatile.Write(ref _count, _properties.Count);
        }
    }

    private void OnSubjectDetaching(SubjectLifecycleChange change)
    {
        _onSubjectDetaching?.Invoke(change.Subject);

        lock (_lock)
        {
            var toRelease = _properties
                .Where(p => p.Subject == change.Subject)
                .ToList();

            foreach (var property in toRelease)
            {
                _properties.Remove(property);
                _onReleasing?.Invoke(property);
                property.RemoveSource(_source);
            }

            Volatile.Write(ref _count, _properties.Count);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _lifecycle.SubjectDetaching -= OnSubjectDetaching;

        lock (_lock)
        {
            foreach (var property in _properties)
            {
                _onReleasing?.Invoke(property);
                property.RemoveSource(_source);
            }
            _properties.Clear();
            Volatile.Write(ref _count, _properties.Count);
        }
    }
}
