using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenDistinctSubjectsCompareEqual_ThenTheirWireIdsAndValuesRemainDistinct(bool partial)
    {
        // Arrange
        var first = new ValueEqualWireSubject { EqualityKey = "child", Value = 1 };
        var second = new ValueEqualWireSubject { EqualityKey = "child", Value = 2 };
        var source = new ValueEqualWireSubject(InterceptorSubjectContext.Create().WithRegistry())
        {
            EqualityKey = "root",
            Children = [first, second]
        };
        var target = new ValueEqualWireSubject(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = [new ValueEqualWireSubject(), new ValueEqualWireSubject()]
        };
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create(new PropertyReference(first, nameof(ValueEqualWireSubject.Value)), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 0, 1),
            SubjectPropertyChange.Create(new PropertyReference(second, nameof(ValueEqualWireSubject.Value)), ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 0, 2)
        ];

        // Act
        var update = partial
            ? SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, [])
            : SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var items = update.Subjects[update.Root][nameof(ValueEqualWireSubject.Children)].Items!;
        Assert.Equal(2, items.Count);
        Assert.NotEqual(items[0].Id, items[1].Id);
        Assert.Equal(1, target.Children[0].Value);
        Assert.Equal(2, target.Children[1].Value);
    }
}

[InterceptorSubject]
public partial class ValueEqualWireSubject
{
    public ValueEqualWireSubject() => Children = [];

    public partial string? EqualityKey { get; set; }

    public partial int Value { get; set; }

    public partial ValueEqualWireSubject[] Children { get; set; }

    public override bool Equals(object? obj) => obj is ValueEqualWireSubject other && other.EqualityKey == EqualityKey;

    public override int GetHashCode() => EqualityKey?.GetHashCode() ?? 0;
}
