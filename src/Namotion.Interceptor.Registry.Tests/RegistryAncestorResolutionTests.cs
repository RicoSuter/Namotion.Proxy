using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Covers the second way a consumer meets the ordering: not from a lifecycle handler, but from a
/// derived attribute created by an <see cref="ISubjectPropertyInitializer"/>, whose getter
/// <c>DerivedPropertyChangeHandler</c> first evaluates while the property attaches. Reaching the
/// ancestors that late is a different route to them than <see cref="RegistryHandlerOrderTests"/>
/// takes, and the last of these also pins a consequence visibility alone does not: an ancestor the
/// getter never reached is never recorded as a dependency, so a later write to it does not correct
/// the value.
///
/// Only the composition that registers parents before the registry is covered, because it is the
/// only one whose resolved order the attribute changes. The assertions read what the getter computed
/// on its FIRST evaluation: reading the attribute afterwards re-resolves against the settled graph
/// and would report a correct chain either way.
/// </summary>
public class RegistryAncestorResolutionTests
{
    private const string EnabledPropertyName = nameof(FlagNode.Enabled);
    private const string ResolvedAncestorsAttribute = "ResolvedAncestors";
    private const string InheritedEnabledAttribute = "InheritedEnabled";

    private static (IInterceptorSubjectContext Context, InheritedFlagInitializer Initializer) CreateContext()
    {
        IInterceptorSubjectContext context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithParents()
            .WithRegistry();

        var initializer = new InheritedFlagInitializer();
        return (context.WithService(() => initializer), initializer);
    }

    /// <summary>
    /// Builds root -> top -> middle -> child with the subtree detached, and disables the flag on
    /// <c>top</c>: the ancestor a registry resolved behind the descent would be missing when the
    /// child's getter first runs.
    /// </summary>
    private static FlagNode BuildScenario(IInterceptorSubjectContext context, out FlagNode root, out FlagNode child)
    {
        root = new FlagNode(context) { Name = "root", Enabled = true };

        child = new FlagNode { Name = "child", Enabled = true };
        var middle = new FlagNode { Name = "middle", Enabled = true, Child = child };
        var top = new FlagNode { Name = "top", Enabled = false, Child = middle };

        return top;
    }

    [Fact]
    public void WhenSubtreeIsAttached_ThenADerivedGetterResolvesEveryAncestorOnItsFirstEvaluation()
    {
        // Arrange
        var (context, initializer) = CreateContext();
        var top = BuildScenario(context, out var root, out var child);

        // Act
        root.Child = top;

        // Assert
        Assert.Equal(3, initializer.FirstResolvedAncestors[child]);
    }

    [Fact]
    public void WhenAnAncestorIsDisabled_ThenTheDerivedGetterSeesItOnItsFirstEvaluation()
    {
        // Arrange
        var (context, initializer) = CreateContext();
        var top = BuildScenario(context, out var root, out var child);

        // Act
        root.Child = top;

        // Assert: an unresolvable ancestor is skipped by the getter, so a gap at "top" leaves this
        // true. The composed flag is the observable consequence of the ordering.
        Assert.False(initializer.FirstInheritedEnabled[child]);
    }

    [Fact]
    public void WhenAnAncestorFlagChangesAfterAttach_ThenTheDerivedAttributeRecalculates()
    {
        // Arrange
        var (context, _) = CreateContext();
        var top = BuildScenario(context, out var root, out var child);
        root.Child = top;

        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change =>
                ReferenceEquals(change.Property.Subject, child) &&
                change.Property.Name == $"{EnabledPropertyName}@{InheritedEnabledAttribute}")
            .Subscribe(changes.Add);

        // Act
        top.Enabled = true;

        // Assert: the getter read the ancestor's flag through the registry while attaching, so that
        // read was recorded as a dependency and this write cascades. An ancestor missed at attach
        // would never have been recorded and no recalculation would fire.
        Assert.Contains(changes, change => change.GetNewValue<bool>());
    }

    /// <summary>
    /// Adds two derived attributes to every <c>Enabled</c> property: one reporting how many ancestors
    /// resolve through the registry, one composing this subject's flag with theirs. Both record the
    /// value their first evaluation produced, which is the attach-time observation.
    /// </summary>
    private sealed class InheritedFlagInitializer : ISubjectPropertyInitializer
    {
        public ConcurrentDictionary<IInterceptorSubject, int> FirstResolvedAncestors { get; } = new();

        public ConcurrentDictionary<IInterceptorSubject, bool> FirstInheritedEnabled { get; } = new();

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (property.IsAttribute || property.Name != EnabledPropertyName)
            {
                return;
            }

            property.AddDerivedAttribute(
                ResolvedAncestorsAttribute, typeof(int),
                subject =>
                {
                    var count = CountResolvableAncestors(subject, []);
                    FirstResolvedAncestors.TryAdd(subject, count);
                    return count;
                },
                null);

            property.AddDerivedAttribute(
                InheritedEnabledAttribute, typeof(bool),
                subject =>
                {
                    var enabled = ComputeInheritedEnabled(subject);
                    FirstInheritedEnabled.TryAdd(subject, enabled);
                    return enabled;
                },
                null);
        }

        private static int CountResolvableAncestors(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
        {
            if (!visited.Add(subject))
            {
                return 0;
            }

            var count = 0;
            foreach (var parent in subject.GetParents())
            {
                var parentSubject = parent.Property.Subject;
                if (parentSubject.TryGetRegisteredSubject() is not null)
                {
                    count++;
                }

                count += CountResolvableAncestors(parentSubject, visited);
            }

            return count;
        }

        private static bool ComputeInheritedEnabled(IInterceptorSubject subject)
        {
            return ReadEnabled(subject) is not false && AllAncestorsEnabled(subject, []);
        }

        /// <summary>
        /// Reads every resolvable ancestor's flag rather than short-circuiting, so each one is
        /// recorded as a dependency of this attribute.
        /// </summary>
        private static bool AllAncestorsEnabled(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
        {
            if (!visited.Add(subject))
            {
                return true;
            }

            var result = true;
            foreach (var parent in subject.GetParents())
            {
                var parentSubject = parent.Property.Subject;
                if (parentSubject.TryGetRegisteredSubject() is not null && ReadEnabled(parentSubject) is false)
                {
                    result = false;
                }

                if (!AllAncestorsEnabled(parentSubject, visited))
                {
                    result = false;
                }
            }

            return result;
        }

        private static bool? ReadEnabled(IInterceptorSubject subject)
        {
            return subject.TryGetRegisteredSubject()?.TryGetProperty(EnabledPropertyName)?.GetValue() as bool?;
        }
    }
}

[InterceptorSubject]
public partial class FlagNode
{
    public partial string Name { get; set; }

    public partial bool Enabled { get; set; }

    public partial FlagNode? Child { get; set; }

    public FlagNode()
    {
        Name = string.Empty;
    }
}
