using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Dynamic;

public class DynamicSubject : IInterceptorSubject
{
    private IInterceptorExecutor? _context;
    private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties;

    public DynamicSubject(IInterceptorSubjectContext context) : this()
    {
        ((IInterceptorSubject)this).Context.AddFallbackContext(context);
    }

    public DynamicSubject()
    {
        _properties = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;
    }
    
    protected DynamicSubject(IEnumerable<SubjectPropertyMetadata> properties)
    {
        _properties = properties.ToFrozenDictionary(p => p.Name, p => p);
    }
    
    [JsonIgnore] object IInterceptorSubject.SyncRoot { get; } = new();

    [JsonIgnore]
    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);

    [JsonIgnore] ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();

    [JsonIgnore] IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
    {
        lock (((IInterceptorSubject)this).SyncRoot)
        {
            _properties = _properties
                .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                .ToFrozenDictionary();
        }
    }
}