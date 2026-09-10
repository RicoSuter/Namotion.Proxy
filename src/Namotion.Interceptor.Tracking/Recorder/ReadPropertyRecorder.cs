using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// A utility class for property read recording and an interceptor that automatically
/// records property reads to all active scopes on the current async context.
/// Uses AsyncLocal to ensure proper isolation across concurrent async operations
/// (e.g., Blazor components in different browser tabs/sessions).
/// </summary>
public class ReadPropertyRecorder : IReadInterceptor,
    ISingletonContextService<ReadPropertyRecorder>
{
    private static readonly AsyncLocal<List<ReadPropertyRecorderScope>?> _activeScopes = new();

    /// <summary>
    /// Starts recording property reads to a new scope.
    /// Property reads are automatically recorded via the interceptor.
    /// Uses AsyncLocal for proper isolation across async contexts.
    /// </summary>
    /// <param name="properties">Optional preallocated properties dictionary.</param>
    /// <returns>The recording scope. Dispose to stop recording.</returns>
    public static ReadPropertyRecorderScope Start(ConcurrentDictionary<PropertyReference, bool>? properties = null)
    {
        var scope = new ReadPropertyRecorderScope(properties);

        // Get or create the scopes list for this async context
        var scopes = _activeScopes.Value;
        if (scopes == null)
        {
            scopes = new List<ReadPropertyRecorderScope>();
            _activeScopes.Value = scopes;
        }

        lock (scopes)
        {
            scopes.Add(scope);
        }

        return scope;
    }

    internal static void RemoveScope(ReadPropertyRecorderScope scope)
    {
        var scopes = _activeScopes.Value;
        if (scopes is not null)
        {
            lock (scopes)
            {
                scopes.Remove(scope);
            }
        }
    }

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
    {
        var result = next(ref context);

        // Record to all active scopes in this async context
        var scopes = _activeScopes.Value;
        if (scopes is not null)
        {
            lock (scopes)
            {
                foreach (var scope in scopes)
                {
                    scope.AddProperty(context.Property);
                }
            }
        }

        return result;
    }
}
