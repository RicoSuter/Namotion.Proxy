using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Dynamic;

public class DynamicSubject : IInterceptorSubject
{
    private IInterceptorExecutor? _executor;
    private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties;

    public DynamicSubject(IInterceptorSubjectContext context) : this()
    {
        // A provisional root anchor, matching the generated context-taking constructor; see
        // SubjectAttachmentAnchorKind for why constructors do not create explicit roots.
        this.AttachToContext(context, SubjectAttachmentAnchorKind.Provisional);
    }

    public DynamicSubject()
    {
        _properties = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;
    }
    
    protected DynamicSubject(IEnumerable<SubjectPropertyMetadata> properties)
    {
        _properties = properties.ToFrozenDictionary(p => p.Name, p => p);
    }
    
    // Explicit implementation, like Data below: DynamicSubjectFactory reflects
    // over GetProperties(Instance | Public | NonPublic) and turns every unknown property into an
    // intercepted subject property, so a public or protected Executor would become a phantom
    // property on every Castle-proxied subject.
    [JsonIgnore]
    IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    [JsonIgnore] ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();

    [JsonIgnore] IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
    {
        // Routed through the executor so an attached subject's lifecycle can admit metadata,
        // ownership edges and property callbacks as one atomic publication.
        var subject = (IInterceptorSubject)this;
        subject.Executor.AddProperties(new SubjectPropertyRegistration(
            subject, properties, published => _properties = published));
    }
}