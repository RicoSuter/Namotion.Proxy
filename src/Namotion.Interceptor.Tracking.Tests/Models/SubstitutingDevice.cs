using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A hand-written subject whose terminal substitutes <see cref="Substitute"/> for the proposed
/// value when one is armed. <see cref="Substitute"/> is a plain auto-property, absent from the
/// metadata and therefore not intercepted, so arming it triggers no write of its own.
/// </summary>
internal sealed class SubstitutingDevice : IInterceptorSubject
{
    private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Child)] = new(
                nameof(Child),
                typeof(SubstitutingDevice),
                [],
                static subject => ((SubstitutingDevice)subject).ReadAuthoritativeValue(),
                static (subject, value) => ((SubstitutingDevice)subject).Child = (SubstitutingDevice?)value,
                isIntercepted: true,
                isDynamic: false)
        }.ToFrozenDictionary();

    private IInterceptorExecutor? _executor;
    private SubstitutingDevice? _child;

    public SubstitutingDevice? Substitute { get; set; }

    /// <summary>
    /// Runs inside the authoritative getter the lifecycle rereads after the terminal stored and
    /// before it reconciles, receiving the stored value. That reread is invoked by the lifecycle
    /// itself, between its own <c>next</c> and its reconcile, so no interceptor ordering can move
    /// it and the terminal lock is already released when it runs.
    /// </summary>
    public Action<SubstitutingDevice?>? OnAuthoritativeValueRead;

    /// <summary>
    /// The stored value read straight off the backing field, the way a device exposes state it also
    /// keeps in an intercepted property. Reading it records no dependency, because nothing
    /// intercepts it, which is what a design that infers a recalculation from the dependency set
    /// cannot see.
    /// </summary>
    public SubstitutingDevice? ChildWithoutInterception => _child;

    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");

    private SubstitutingDevice? ReadAuthoritativeValue()
    {
        OnAuthoritativeValueRead?.Invoke(_child);
        return _child;
    }

    public SubstitutingDevice? Child
    {
        get => Executor.GetPropertyValue(nameof(Child), static subject => ((SubstitutingDevice)subject)._child);
        set => Executor.SetPropertyValue(nameof(Child), value, _child,
            static (subject, newValue) =>
            {
                var device = (SubstitutingDevice)subject;
                device._child = device.Substitute ?? newValue;
            });
    }
}
