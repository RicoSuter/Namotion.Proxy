using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class IncomingOccurrenceRemovalTests
{
    [Fact]
    public void WhenRemovingAKeyedDuplicate_ThenUserEqualityCannotInterruptOwnershipCleanup()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var child = new Person();
        var first = new ThrowingKey(1);
        var second = new ThrowingKey(2);
        var root = new CallbackSpikeNode(context)
        {
            Payload = new Dictionary<ThrowingKey, Person> { [first] = child, [second] = child }
        };
        var replacement = new Dictionary<ThrowingKey, Person> { [first] = child };
        first.ThrowOnComparison = true;
        second.ThrowOnComparison = true;

        // Act
        root.Payload = replacement;

        // Assert
        Assert.Same(replacement, root.Payload);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Same(first, Assert.Single(child.GetParents()).Index);
        root.Payload = null;
        Assert.Null(child.TryGetContext());
        Assert.Empty(child.GetParents());
    }

    [Fact]
    public void WhenIncomingIndexIsStale_ThenRemovalPreservesTheLeadingOccurrenceWithoutCallingUserEquality()
    {
        // Arrange
        var parent = new Person();
        var property = new PropertyReference(parent, nameof(Person.Children));
        var first = new ThrowingKey(1) { ThrowOnComparison = true };
        var second = new ThrowingKey(2) { ThrowOnComparison = true };
        var ownership = new SubjectOwnership();
        ownership.AddIncoming(property, first);
        ownership.AddIncoming(property, second);

        // Act
        var removed = ownership.RemoveIncoming(property, new ThrowingKey(3));

        // Assert
        Assert.True(removed);
        Assert.Equal(1, ownership.IncomingCount);
        Assert.Same(first, Assert.Single(ownership.ActivateParents()).Index);
    }

    [Fact]
    public void WhenRemovingAnInterleavedPropertyOccurrence_ThenOtherPropertiesAndLeadingDuplicatesRemain()
    {
        // Arrange
        var parent = new Person();
        var property = new PropertyReference(parent, nameof(Person.Children));
        var otherProperty = new PropertyReference(parent, nameof(Person.Father));
        var first = new ThrowingKey(1) { ThrowOnComparison = true };
        var second = new ThrowingKey(2) { ThrowOnComparison = true };
        var ownership = new SubjectOwnership();
        ownership.AddIncoming(otherProperty, null);
        ownership.AddIncoming(property, first);
        ownership.AddIncoming(otherProperty, null);
        ownership.AddIncoming(property, second);

        // Act
        var removed = ownership.RemoveIncoming(property, second);

        // Assert
        Assert.True(removed);
        Assert.Equal(3, ownership.IncomingCount);
        var parents = ownership.ActivateParents();
        Assert.Equal(2, parents.Count(item => item.Property.Equals(otherProperty)));
        Assert.Same(first, Assert.Single(parents, item => item.Property.Equals(property)).Index);
    }

    private sealed class ThrowingKey(int value)
    {
        public bool ThrowOnComparison { get; set; }
        public override int GetHashCode() => value;
        public override bool Equals(object? other)
        {
            if (ThrowOnComparison) throw new InvalidOperationException("Ownership mutation invoked a user key comparison.");
            return other is ThrowingKey key && key.GetHashCode() == value;
        }
    }
}
