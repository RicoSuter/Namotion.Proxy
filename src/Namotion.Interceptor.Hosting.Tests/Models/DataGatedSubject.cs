using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A hand written subject with no properties whose <see cref="Data"/> accessor runs one queued action
/// and then forgets it.
/// </summary>
/// <remarks>
/// The seam a test needs for the attach paths sits between the handler lookup and the add, and those
/// two statements are adjacent in production. The add reaches the subject through this accessor and
/// nothing ahead of it does, so gating it here holds that window open from the test project instead of
/// putting a seam on a public extension method.
/// <para>
/// Hand written rather than generated because the gate has to be the implementation the extension
/// method reaches: the generator emits <see cref="IInterceptorSubject.Data"/> explicitly, so a derived
/// class cannot override it. No properties, which is all the lifecycle interceptor needs to attach a
/// subject to a context and dispatch the event this seam is here to interleave with.
/// </para>
/// </remarks>
internal sealed class DataGatedSubject : IInterceptorSubject
{
    private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> NoProperties =
        new Dictionary<string, SubjectPropertyMetadata>();

    private readonly ConcurrentDictionary<(string? property, string key), object?> _data = new();

    private IInterceptorExecutor? _context;
    private Action? _gate;

    /// <summary>
    /// Arms <paramref name="gate"/> to run on the next read of <see cref="Data"/>, once.
    /// </summary>
    public void GateNextDataRead(Action gate) => Volatile.Write(ref _gate, gate);

    public object SyncRoot { get; } = new();

    public IInterceptorSubjectContext Context => InterceptorExecutor.GetOrCreate(ref _context, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data
    {
        get
        {
            // Taken before it runs, so the reads the gate itself makes through this accessor do not
            // re-enter it.
            Interlocked.Exchange(ref _gate, null)?.Invoke();
            return _data;
        }
    }

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => NoProperties;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
        => throw new NotSupportedException();
}
