using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// An explicit attach reads every structural getter twice: discovery claims the component the first
/// read opens up, and seeding commits the second read as the baseline. A getter that answers
/// differently across those two reads breaks the stability the protocol requires, and the value only
/// discovery saw must not be left claimed by the context: it carries no edge, so no release can
/// reach it, and a subject the context owns can neither join another graph nor be detached.
/// </summary>
public class UnstableStructuralGetterTests
{
    /// <summary>
    /// A hand-written subject whose structural getter answers with a different child on its first
    /// call than on every later one. Hand-written metadata is the shape that can do this: a
    /// generated partial property cannot, because the generator writes the getter.
    /// </summary>
    private sealed class UnstableChildDevice : IInterceptorSubject
    {
        private const string ChildPropertyName = "Child";

        private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [ChildPropertyName] = new(
                    ChildPropertyName,
                    typeof(Person),
                    [],
                    static subject => ((UnstableChildDevice)subject).ReadChild(),
                    static (_, _) => throw new NotSupportedException("The unstable child is read-only."),
                    isIntercepted: true,
                    isDynamic: false)
            }.ToFrozenDictionary();

        private IInterceptorExecutor? _executor;
        private int _reads;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException("The unstable device declares all its properties statically.");

        /// <summary>The child only discovery ever sees.</summary>
        public Person DiscoveredChild { get; } = new() { FirstName = "disc" };

        /// <summary>The child seeding commits, and the answer to every read after the first.</summary>
        public Person SeededChild { get; } = new() { FirstName = "seed" };

        /// <summary>How often the structural getter was read, which positions the two passes.</summary>
        public int Reads => Volatile.Read(ref _reads);

        private Person ReadChild()
        {
            return Interlocked.Increment(ref _reads) == 1 ? DiscoveredChild : SeededChild;
        }
    }

    /// <summary>
    /// Pins that the value discovery claimed and seeding discarded is handed back rather than left
    /// stranded. Each consequence is asserted separately, the way a residue rollback is, so a
    /// partial cleanup reports which part leaked: nothing references the discarded child, the
    /// registry never saw it, and it has no anchor to detach, which together mean the context has
    /// no way to release it. So the context must not still own it, and the last assertion is the
    /// observable consequence of that: it can join another graph.
    /// </summary>
    [Fact]
    public void WhenAStructuralGetterAnswersDifferentlyAcrossTheAttachReads_ThenTheDiscardedValueIsNotStranded()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();
        var otherContext = InterceptorSubjectContext
            .Create()
            .WithRegistry();
        var device = new UnstableChildDevice();

        // Act
        ((IInterceptorSubject)device).AttachToContext(context);

        // Assert: the attach ran both reads and committed the second one, which is what makes the
        // first value a discarded claim rather than the child the graph is built from.
        Assert.True(device.Reads >= 2, $"the attach read the structural getter {device.Reads} times, not twice");
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Same(context, ((IInterceptorSubject)device.SeededChild).TryGetContext());
        Assert.Contains(device.SeededChild, registry.KnownSubjects.Keys);

        // Nothing in the graph holds the discarded child, on either side of the fix.
        var discarded = (IInterceptorSubject)device.DiscoveredChild;
        Assert.Equal(0, discarded.GetReferenceCount());
        Assert.Empty(discarded.GetParents());
        Assert.DoesNotContain(device.DiscoveredChild, registry.KnownSubjects.Keys);
        Assert.Throws<InvalidOperationException>(() => discarded.DetachFromContext(context));

        // So the context must not still own it: an owned subject that nothing references and no
        // anchor holds is one nothing can ever release.
        Assert.Null(discarded.TryGetContext());
        Assert.Null(Record.Exception(() => discarded.AttachToContext(otherContext)));
    }
}
