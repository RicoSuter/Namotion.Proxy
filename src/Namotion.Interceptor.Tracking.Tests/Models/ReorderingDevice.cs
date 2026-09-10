using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A hand-written subject whose terminal drops one proposed subject and stores the rest,
/// reordered, in a different list instance. This is what a normalizing setter actually does,
/// and all of it must stay legal: every stored subject was proposed and therefore claimed
/// before the terminal ran, and the dropped one's claim is handed back by ReleaseUnusedClaims.
/// </summary>
internal sealed class ReorderingDevice : IInterceptorSubject
{
    private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Children)] = new(
                nameof(Children),
                typeof(IReadOnlyList<ReorderingDevice>),
                [],
                static subject => ((ReorderingDevice)subject)._children,
                static (subject, value) => ((ReorderingDevice)subject).Children = (IReadOnlyList<ReorderingDevice>?)value,
                isIntercepted: true,
                isDynamic: false)
        }.ToFrozenDictionary();

    private IInterceptorExecutor? _executor;
    private IReadOnlyList<ReorderingDevice>? _children;

    public string Name { get; init; } = string.Empty;

    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");

    public IReadOnlyList<ReorderingDevice>? Children
    {
        get => Executor.GetPropertyValue(nameof(Children), static subject => ((ReorderingDevice)subject)._children);
        set => Executor.SetPropertyValue(nameof(Children), value, _children,
            static (subject, newValue) => ((ReorderingDevice)subject)._children = newValue?
                .Where(child => child.Name != "dropped")
                .OrderBy(child => child.Name)
                .ToList());
    }
}
