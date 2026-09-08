using Namotion.Interceptor.Registry;
using System.Collections;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Reconciliation reads the committed baseline, scans user values, and only then commits the new
/// baseline. Those scans run at callback depth zero, where a nested write of the same property is
/// legal, so the outer operation can commit its own baseline on top of the newer one the nested
/// write already committed.
/// </summary>
public class ReentrantStructuralWriteTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    private static object? GetCommittedBaseline(IInterceptorSubjectContext context, EnumerableChildrenHolder holder)
    {
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        return lifecycle.Graph.GetBaseline(
            new PropertyReference(holder, nameof(EnumerableChildrenHolder.Children)));
    }

    /// <summary>
    /// A user enumerable that re-enters the write protocol once, the first time it is scanned while
    /// <see cref="ShouldReenter"/> holds.
    ///
    /// The trigger is a condition rather than an enumeration ordinal on purpose. How many times the
    /// protocol scans a given value is an implementation detail that is expected to change, and an
    /// ordinal armed against today's count would silently stop firing when it does. The condition
    /// used by the test below instead names the phase it needs, so a change to the scan count either
    /// still lands in that phase or trips the guard.
    /// </summary>
    private sealed class ScanHookEnumerable(IEnumerable<Person> items) : IEnumerable<Person>
    {
        private bool _hasReentered;

        public int Enumerations { get; private set; }

        public bool HasReentered => _hasReentered;

        public Func<bool>? ShouldReenter { get; set; }

        public Action? OnReenter { get; set; }

        public IEnumerator<Person> GetEnumerator()
        {
            Enumerations++;
            if (!_hasReentered && ShouldReenter?.Invoke() == true)
            {
                _hasReentered = true;
                OnReenter?.Invoke();
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// The newly stored enumerable re-enters before its captured baseline commits. The terminal
    /// guard keeps discovery benign and proves the older continuation cannot replace a newer write.
    /// </summary>
    [Fact]
    public void WhenAUserEnumerableWritesTheSamePropertyWhileItIsScanned_ThenTheOuterWriteDoesNotOverwriteTheNewerBaseline()
    {
        // Arrange: discovery is benign; capturing the newly stored value runs the nested write
        // after the terminal commits it and before its baseline is installed.
        var context = CreateContext();
        var holder = new EnumerableChildrenHolder(context);
        var firstChild = new Person { FirstName = "first" };
        var outerChild = new Person { FirstName = "outer" };
        var nestedChild = new Person { FirstName = "nested" };

        var committedValue = new ScanHookEnumerable([firstChild]);
        holder.Children = committedValue;

        var outerValue = new ScanHookEnumerable([outerChild]);
        object? fieldAtReentry = null;
        object? baselineAtReentry = null;

        outerValue.ShouldReenter = () => ReferenceEquals(holder.Children, outerValue);
        outerValue.OnReenter = () =>
        {
            fieldAtReentry = holder.Children;
            baselineAtReentry = GetCommittedBaseline(context, holder);
            holder.Children = new List<Person> { nestedChild };
        };

        // Act
        holder.Children = outerValue;

        // Assert: the re-entry happened, and it happened in the phase this test is about. Either
        // guard failing means the instrument moved, not that the behaviour changed.
        Assert.True(outerValue.HasReentered,
            $"the reentrant write never ran; the new value was scanned {outerValue.Enumerations} times");
        Assert.Same(outerValue, fieldAtReentry);
        Assert.Same(committedValue, baselineAtReentry);

        // The nested write is the newer one and its value is what the property holds afterwards.
        Assert.Equal([nestedChild], holder.Children!);
        Assert.Same(context, ((IInterceptorSubject)nestedChild).TryGetContext());

        // The outer write committed its own baseline over the newer one, then published an edge
        // for a value the property no longer holds.
        Assert.True(((IInterceptorSubject)outerChild).TryGetContext() is null,
            "the outer write overwrote the newer baseline committed by the reentrant write and " +
            "published an ownership edge for a value the committed property no longer holds, so " +
            $"'{outerChild.FirstName}' is attached with {((IInterceptorSubject)outerChild).GetReferenceCount()} " +
            "incoming edge(s) while unreachable from the subject graph");
    }

    /// <summary>
    /// A root enumerable rewrites its own property after public context attachment becomes visible. The newer stored value must win seeding.
    /// </summary>
    [Fact]
    public void WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes()
    {
        // Arrange: an unattached root whose structural value runs user code when it is scanned.
        var context = CreateContext().WithRegistry();
        var seededChild = new Person { FirstName = "seeded" };
        var lateChild = new Person { FirstName = "late" };
        var holder = new EnumerableChildrenHolder();

        var initialValue = new ScanHookEnumerable([seededChild]);
        holder.Children = initialValue;

        var lateValue = new List<Person> { lateChild };
        var reenteredDuringAttach = false;

        initialValue.ShouldReenter = () =>
            ((IInterceptorSubject)holder).TryGetContext() is not null;

        initialValue.OnReenter = () =>
        {
            reenteredDuringAttach = true;
            holder.Children = lateValue;
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));

        // Assert
        Assert.Null(exception);
        Assert.True(reenteredDuringAttach,
            $"the reentrant write never ran during attach; the initial value was scanned {initialValue.Enumerations} times");
        Assert.Same(lateValue, holder.Children);
        SupportContractAssertions.Settled(context, [holder], holder, seededChild, lateChild);
        holder.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], holder, seededChild, lateChild);
    }
}
