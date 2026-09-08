using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class SubjectPropertyChangeTests
{
    private readonly PropertyReference _property;
    private readonly DateTimeOffset _changedTimestamp = DateTimeOffset.UtcNow;
    private readonly DateTimeOffset _receivedTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(-10);

    public SubjectPropertyChangeTests()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        _property = new PropertyReference(person, nameof(Person.FirstName));
    }

    [Theory]
    [InlineData("OldName", "NewName")]
    [InlineData("", "NewName")]
    [InlineData("Test", "")]
    public void WhenCreatedWithString_ThenStoresAndRetrievesCorrectly(string oldValue, string newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Fact]
    public void WhenCreatedWithNullString_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        string? oldValue = null;
        const string newValue = "NewName";

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Fact]
    public void WhenCreatedWithBothStringsNull_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        string? oldValue = null;
        string? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<string>());
        Assert.Null(change.GetNewValue<string>());
    }

    [Theory]
    [InlineData(42, 100)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(0, -1)]
    public void WhenCreatedWithInt_ThenStoresAndRetrievesCorrectly(int oldValue, int newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<int>());
        Assert.Equal(newValue, change.GetNewValue<int>());
    }

    [Theory]
    [InlineData(123456789012345L, 987654321098765L)]
    [InlineData(long.MinValue, long.MaxValue)]
    public void WhenCreatedWithLong_ThenStoresAndRetrievesCorrectly(long oldValue, long newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<long>());
        Assert.Equal(newValue, change.GetNewValue<long>());
    }

    [Theory]
    [InlineData(3.14159265358979, 2.71828182845904)]
    [InlineData(double.MinValue, double.MaxValue)]
    public void WhenCreatedWithDouble_ThenStoresAndRetrievesCorrectly(double oldValue, double newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<double>());
        Assert.Equal(newValue, change.GetNewValue<double>());
    }

    [Theory]
    [InlineData(3.14f, 2.71f)]
    [InlineData(float.MinValue, float.MaxValue)]
    public void WhenCreatedWithFloat_ThenStoresAndRetrievesCorrectly(float oldValue, float newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<float>());
        Assert.Equal(newValue, change.GetNewValue<float>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WhenCreatedWithBool_ThenStoresAndRetrievesCorrectly(bool oldValue, bool newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<bool>());
        Assert.Equal(newValue, change.GetNewValue<bool>());
    }

    [Theory]
    [InlineData((byte)0, (byte)255)]
    [InlineData((byte)128, (byte)64)]
    public void WhenCreatedWithByte_ThenStoresAndRetrievesCorrectly(byte oldValue, byte newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<byte>());
        Assert.Equal(newValue, change.GetNewValue<byte>());
    }

    [Theory]
    [InlineData('A', 'Z')]
    [InlineData('0', '9')]
    public void WhenCreatedWithChar_ThenStoresAndRetrievesCorrectly(char oldValue, char newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<char>());
        Assert.Equal(newValue, change.GetNewValue<char>());
    }

    public static IEnumerable<object[]> LargerValueTypeTestData()
    {
        yield return [123456789.123456789m, 987654321.987654321m];
        yield return [new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc)];
        yield return [new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5))];
        yield return [Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")];
        yield return [TimeSpan.FromHours(1.5), TimeSpan.FromDays(7)];
    }

    [Theory]
    [MemberData(nameof(LargerValueTypeTestData))]
    public void WhenCreatedWithLargerValueTypes_ThenStoresAndRetrievesCorrectly(object oldValue, object newValue)
    {
        // Arrange
        var method = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.Create))!
            .MakeGenericMethod(oldValue.GetType());

        // Act - reflection does not fill in the optional revision parameter, so it is passed explicitly
        var change = (SubjectPropertyChange)method.Invoke(null,
            [_property, null, _changedTimestamp, _receivedTimestamp, oldValue, newValue, 0L])!;

        // Assert
        var getOldMethod = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.GetOldValue))!
            .MakeGenericMethod(oldValue.GetType());
        var getNewMethod = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.GetNewValue))!
            .MakeGenericMethod(newValue.GetType());

        Assert.Equal(oldValue, getOldMethod.Invoke(change, null));
        Assert.Equal(newValue, getNewMethod.Invoke(change, null));
    }

    [Theory]
    [InlineData(42, 100)]
    [InlineData(0, int.MaxValue)]
    public void WhenCreatedWithNullableIntWithValue_ThenStoresAndRetrievesCorrectly(int oldVal, int newVal)
    {
        // Arrange
        int? oldValue = oldVal;
        int? newValue = newVal;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<int?>());
        Assert.Equal(newValue, change.GetNewValue<int?>());
    }

    [Fact]
    public void WhenCreatedWithNullableIntWithNull_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        int? oldValue = null;
        int? newValue = 42;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int?>());
        Assert.Equal(newValue, change.GetNewValue<int?>());
    }

    [Fact]
    public void WhenCreatedWithNullableIntBothNull_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        int? oldValue = null;
        int? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int?>());
        Assert.Null(change.GetNewValue<int?>());
    }

    [Fact]
    public void WhenCreatedWithNullableDecimal_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        decimal? oldValue = 123.456m;
        decimal? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<decimal?>());
        Assert.Null(change.GetNewValue<decimal?>());
    }

    private struct SmallCustomStruct
    {
        public int Value1;
        public int Value2;
    }

    private struct LargeCustomStruct
    {
        public long Value1;
        public long Value2;
    }

    private struct OversizedCustomStruct
    {
        public long Value1;
        public long Value2;
        public long Value3; // 24 bytes total - exceeds 16 byte inline storage
    }

    [Fact]
    public void WhenCreatedWithSmallCustomStruct_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new SmallCustomStruct { Value1 = 1, Value2 = 2 };
        var newValue = new SmallCustomStruct { Value1 = 10, Value2 = 20 };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<SmallCustomStruct>();
        var retrievedNew = change.GetNewValue<SmallCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
    }

    [Fact]
    public void WhenCreatedWithLargeCustomStruct_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new LargeCustomStruct { Value1 = 111111111111L, Value2 = 222222222222L };
        var newValue = new LargeCustomStruct { Value1 = 333333333333L, Value2 = 444444444444L };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<LargeCustomStruct>();
        var retrievedNew = change.GetNewValue<LargeCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
    }

    [Fact]
    public void WhenCreatedWithOversizedCustomStruct_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new OversizedCustomStruct { Value1 = 1L, Value2 = 2L, Value3 = 3L };
        var newValue = new OversizedCustomStruct { Value1 = 10L, Value2 = 20L, Value3 = 30L };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<OversizedCustomStruct>();
        var retrievedNew = change.GetNewValue<OversizedCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(oldValue.Value3, retrievedOld.Value3);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
        Assert.Equal(newValue.Value3, retrievedNew.Value3);
    }

    [Fact]
    public void WhenGettingOldValueOfCustomStructAsObject_ThenReturnsBoxedStruct()
    {
        // Arrange
        var oldValue = new SmallCustomStruct { Value1 = 42, Value2 = 84 };
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, new SmallCustomStruct());

        // Act
        var result = change.GetOldValue<object>();

        // Assert
        Assert.IsType<SmallCustomStruct>(result);
        var unboxed = (SmallCustomStruct)result;
        Assert.Equal(oldValue.Value1, unboxed.Value1);
        Assert.Equal(oldValue.Value2, unboxed.Value2);
    }

    [Fact]
    public void WhenGettingOldValueOfOversizedStructAsObject_ThenReturnsBoxedStruct()
    {
        // Arrange
        var oldValue = new OversizedCustomStruct { Value1 = 1L, Value2 = 2L, Value3 = 3L };
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, new OversizedCustomStruct());

        // Act
        var result = change.GetOldValue<object>();

        // Assert
        Assert.IsType<OversizedCustomStruct>(result);
        var unboxed = (OversizedCustomStruct)result;
        Assert.Equal(oldValue.Value1, unboxed.Value1);
        Assert.Equal(oldValue.Value2, unboxed.Value2);
        Assert.Equal(oldValue.Value3, unboxed.Value3);
    }

    private class CustomClass
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void WhenCreatedWithReferenceType_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new CustomClass { Id = 1, Name = "Old" };
        var newValue = new CustomClass { Id = 2, Name = "New" };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Same(oldValue, change.GetOldValue<CustomClass>());
        Assert.Same(newValue, change.GetNewValue<CustomClass>());
    }

    [Fact]
    public void WhenCreatedWithNullReferenceType_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        CustomClass? oldValue = null;
        var newValue = new CustomClass { Id = 1, Name = "New" };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<CustomClass>());
        Assert.Same(newValue, change.GetNewValue<CustomClass>());
    }

    [Fact]
    public void WhenCreatedWithIntArray_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        int[] oldValue = [1, 2, 3];
        int[] newValue = [4, 5, 6, 7];

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Same(oldValue, change.GetOldValue<int[]>());
        Assert.Same(newValue, change.GetNewValue<int[]>());
    }

    [Fact]
    public void WhenCreatedWithNullArray_ThenStoresAndRetrievesCorrectly()
    {
        // Arrange
        int[]? oldValue = null;
        int[] newValue = [1, 2, 3];

        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int[]>());
        Assert.Same(newValue, change.GetNewValue<int[]>());
    }

    [Theory]
    [InlineData(42)]
    [InlineData("test")]
    public void WhenGettingOldValueAsObject_ThenReturnsValue(object oldValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldValue, oldValue);

        // Assert
        var result = change.GetOldValue<object>();
        Assert.Equal(oldValue, result);
    }

    [Fact]
    public void WhenTryGettingOldValueWithWrongType_ThenReturnsFalse()
    {
        // Arrange
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            42, 100);

        // Act
        var success = change.TryGetOldValue<string>(out var result);

        // Assert
        Assert.False(success);
        Assert.Null(result);
    }

    [Fact]
    public void WhenTryGettingNewValueWithCorrectType_ThenReturnsTrue()
    {
        // Arrange
        var newValue = 42.5;
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            0.0, newValue);

        // Act
        var success = change.TryGetNewValue<double>(out var result);

        // Assert
        Assert.True(success);
        Assert.Equal(newValue, result);
    }

    [Fact]
    public void WhenCreated_ThenPreservesPropertyReference()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Equal(_property, change.Property);
    }

    [Fact]
    public void WhenCreated_ThenPreservesSource()
    {
        // Arrange
        var source = new object();

        // Act
        var change = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(source), _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Same(source, change.Origin.Source);
    }

    [Fact]
    public void WhenCreated_ThenPreservesTimestamps()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Equal(_changedTimestamp, change.ChangedTimestamp);
        Assert.Equal(_receivedTimestamp, change.ReceivedTimestamp);
    }

    [Fact]
    public void WhenCreatedWithNullReceivedTimestamp_ThenPreservesNull()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, receivedTimestamp: null,
            "old", "new");

        // Assert
        Assert.Null(change.ReceivedTimestamp);
    }

    [Fact]
    public void WhenMergingWithNewerWithInlineValues_ThenKeepsOldFromEarlierAndNewFromLater()
    {
        // Arrange
        var earlierSource = new object();
        var laterSource = new object();
        var earlierTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var laterTimestamp = DateTimeOffset.UtcNow;

        var earlier = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(earlierSource), earlierTimestamp, earlierTimestamp,
            10, 20);
        var later = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(laterSource), laterTimestamp, laterTimestamp,
            20, 30);

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert
        Assert.Equal(10, merged.GetOldValue<int>());
        Assert.Equal(30, merged.GetNewValue<int>());
        Assert.Same(laterSource, merged.Origin.Source);
        Assert.Equal(laterTimestamp, merged.ChangedTimestamp);
    }

    [Fact]
    public void WhenMergingWithNewerWithStrings_ThenKeepsOldFromEarlierAndNewFromLater()
    {
        // Arrange
        var earlier = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "original", "intermediate");
        var later = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "intermediate", "final");

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert
        Assert.Equal("original", merged.GetOldValue<string>());
        Assert.Equal("final", merged.GetNewValue<string>());
    }

    [Fact]
    public void WhenMergingWithNewerWithNullStringOldValue_ThenPreservesNull()
    {
        // Arrange
        var earlier = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            null, "intermediate");
        var later = SubjectPropertyChange.Create<string?>(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "intermediate", "final");

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert
        Assert.Null(merged.GetOldValue<string>());
        Assert.Equal("final", merged.GetNewValue<string>());
    }

    [Fact]
    public void WhenMergingWithNewerWithBoxedReferenceTypes_ThenKeepsOldFromEarlierAndNewFromLater()
    {
        // Arrange
        var oldObj = new CustomClass { Id = 1, Name = "Old" };
        var midObj = new CustomClass { Id = 2, Name = "Mid" };
        var newObj = new CustomClass { Id = 3, Name = "New" };

        var earlier = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldObj, midObj);
        var later = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            midObj, newObj);

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert
        Assert.Same(oldObj, merged.GetOldValue<CustomClass>());
        Assert.Same(newObj, merged.GetNewValue<CustomClass>());
    }

    [Fact]
    public void WhenMergingWithNewerWithFromSourceOrigins_ThenPreservesKindAndSource()
    {
        // Arrange
        var source = new object();
        var earlier = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(source), _changedTimestamp, _receivedTimestamp,
            "old", "intermediate");
        var later = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(source), _changedTimestamp, _receivedTimestamp,
            "intermediate", "new");

        // Act
        var merged = earlier.MergeWithNewer(later);

        // Assert - both the kind and the source survive the merge
        Assert.Equal(ChangeOriginKind.FromSource, merged.Origin.Kind);
        Assert.Same(source, merged.Origin.Source);
    }

    [Fact]
    public void WhenCreatedWithSmallStructContainingReference_ThenKeepsReferenceAliveForGc()
    {
        // Arrange: a small ref-carrying struct; the non-inlined helper leaves the retained change
        // as the value's only possible GC root.
        var (change, weakReference) = CreateChangeWithReferenceStruct(_property, _changedTimestamp, _receivedTimestamp);

        // Act
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        Assert.True(weakReference.IsAlive,
            "SubjectPropertyChange must keep references inside stored values alive for the GC.");
        Assert.Same(weakReference.Target, change.GetOldValue<SmallStructWithReference>().Reference);
        GC.KeepAlive(change);
    }

    [Fact]
    public void WhenCreatedWithImmutableArray_ThenKeepsBackingArrayAliveAcrossGc()
    {
        // Arrange: ImmutableArray<T> wraps a T[] reference; the change may be the backing array's only GC root.
        var (change, weakBackingArray) = CreateChangeWithImmutableArray(_property, _changedTimestamp, _receivedTimestamp);

        // Act
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert - the backing array survives and the value round-trips intact through the
        // boxed object read (the path collection diffing uses)
        Assert.True(weakBackingArray.IsAlive,
            "SubjectPropertyChange must keep the ImmutableArray's backing array alive for the GC.");
        var oldValue = Assert.IsType<ImmutableArray<string>>(change.GetOldValue<object?>());
        Assert.Equal(new[] { "a", "b" }, oldValue);
        GC.KeepAlive(change);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SubjectPropertyChange Change, WeakReference WeakReference) CreateChangeWithReferenceStruct(
        PropertyReference property, DateTimeOffset changedTimestamp, DateTimeOffset receivedTimestamp)
    {
        var referenced = new object();
        var change = SubjectPropertyChange.Create(
            property, origin: ChangeOrigin.Local, changedTimestamp, receivedTimestamp,
            new SmallStructWithReference(referenced), new SmallStructWithReference(null));
        return (change, new WeakReference(referenced));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SubjectPropertyChange Change, WeakReference WeakBackingArray) CreateChangeWithImmutableArray(
        PropertyReference property, DateTimeOffset changedTimestamp, DateTimeOffset receivedTimestamp)
    {
        var oldValue = ImmutableArray.Create("a", "b");
        var backingArray = ImmutableCollectionsMarshal.AsArray(oldValue)!;
        var change = SubjectPropertyChange.Create(
            property, origin: ChangeOrigin.Local, changedTimestamp, receivedTimestamp,
            oldValue, ImmutableArray.Create("a", "b", "c"));
        return (change, new WeakReference(backingArray));
    }

    private readonly record struct SmallStructWithReference(object? Reference);

    [Fact]
    public void WhenCreatedWithRevision_ThenRevisionIsExposedAndSurvivesMergeAndOrigin()
    {
        // Arrange
        var source = new object();

        // Act
        var earlier = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "a", "b", 5L);
        var later = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "b", "c", 6L);
        var merged = earlier.MergeWithNewer(later);
        var reoriginated = later.WithOrigin(ChangeOrigin.Confirmed(source));
        var withoutRevision = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "a", "b");

        // Assert
        Assert.Equal(5L, earlier.Revision);
        Assert.Equal(6L, later.Revision);
        Assert.Equal(6L, merged.Revision);
        Assert.Equal("a", merged.GetOldValue<string>());
        Assert.Equal("c", merged.GetNewValue<string>());
        Assert.Equal(6L, reoriginated.Revision);
        Assert.Equal(0L, withoutRevision.Revision);
    }

    [Fact]
    public void WhenCreatedWithRevisionOnEveryStoragePath_ThenRevisionIsPreserved()
    {
        // Arrange: each Create return path passes the revision separately, so all three are covered:
        // inline storage, the string fast path and the boxed holder.
        const long revision = 42L;

        // Act
        var inlineChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            1, 2, revision);
        var stringChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "old", "new", revision);
        var referenceChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            new CustomClass { Id = 1 }, new CustomClass { Id = 2 }, revision);
        var oversizedStructChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            new OversizedCustomStruct { Value1 = 1L }, new OversizedCustomStruct { Value1 = 2L }, revision);

        // Assert
        Assert.Equal(revision, inlineChange.Revision);
        Assert.Equal(1, inlineChange.GetOldValue<int>());
        Assert.Equal(revision, stringChange.Revision);
        Assert.Equal("old", stringChange.GetOldValue<string>());
        Assert.Equal(revision, referenceChange.Revision);
        Assert.Equal(1, referenceChange.GetOldValue<CustomClass>().Id);
        Assert.Equal(revision, oversizedStructChange.Revision);
        Assert.Equal(1L, oversizedStructChange.GetOldValue<OversizedCustomStruct>().Value1);
    }

    [Fact]
    public void WhenConvertedToRollbackChange_ThenPropertyOriginAndTimestampsArePreserved()
    {
        // Arrange
        var source = new object();
        var change = SubjectPropertyChange.Create(
            _property, ChangeOrigin.FromSource(source), _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Act
        var rollback = change.ToRollbackChange();

        // Assert
        Assert.Equal(_property, rollback.Property);
        Assert.Equal(ChangeOriginKind.FromSource, rollback.Origin.Kind);
        Assert.Same(source, rollback.Origin.Source);
        Assert.Equal(_changedTimestamp, rollback.ChangedTimestamp);
        Assert.Equal(_receivedTimestamp, rollback.ReceivedTimestamp);
    }

    [Fact]
    public void WhenConvertedToRollbackChange_ThenRevisionIsReset()
    {
        // Arrange: a rollback describes a write to perform, not a commit that happened, and Revision is
        // only meaningful for the latter. Applying it commits with a revision of its own.
        var change = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            "old", "new", 7L);

        // Act
        var rollback = change.ToRollbackChange();

        // Assert
        Assert.Equal(0L, rollback.Revision);
    }

    [Fact]
    public void WhenConvertedToRollbackChangeOnEveryStoragePath_ThenTypedValuesRoundTrip()
    {
        // Arrange: the swap moves the storage fields directly, so every storage path has to survive it
        // with its stored type intact rather than degrading to a boxed object.
        var oldReference = new CustomClass { Id = 1 };
        var newReference = new CustomClass { Id = 2 };

        var inlineChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, 1, 2);
        var nullableChange = SubjectPropertyChange.Create<int?>(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, 1, 2);
        var stringChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, "old", "new");
        var nullStringChange = SubjectPropertyChange.Create<string?>(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, null, "new");
        var referenceChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            oldReference, newReference);
        var oversizedChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            new OversizedCustomStruct { Value1 = 1L }, new OversizedCustomStruct { Value1 = 2L });

        // Act
        var inlineRollback = inlineChange.ToRollbackChange();
        var nullableRollback = nullableChange.ToRollbackChange();
        var stringRollback = stringChange.ToRollbackChange();
        var nullStringRollback = nullStringChange.ToRollbackChange();
        var referenceRollback = referenceChange.ToRollbackChange();
        var oversizedRollback = oversizedChange.ToRollbackChange();

        // Assert
        Assert.Equal(2, inlineRollback.GetOldValue<int>());
        Assert.Equal(1, inlineRollback.GetNewValue<int>());
        Assert.Equal(2, nullableRollback.GetOldValue<int?>());
        Assert.Equal(1, nullableRollback.GetNewValue<int?>());
        Assert.Equal("new", stringRollback.GetOldValue<string>());
        Assert.Equal("old", stringRollback.GetNewValue<string>());
        Assert.Equal("new", nullStringRollback.GetOldValue<string>());
        Assert.Null(nullStringRollback.GetNewValue<string>());
        Assert.Same(newReference, referenceRollback.GetOldValue<CustomClass>());
        Assert.Same(oldReference, referenceRollback.GetNewValue<CustomClass>());
        Assert.Equal(2L, oversizedRollback.GetOldValue<OversizedCustomStruct>().Value1);
        Assert.Equal(1L, oversizedRollback.GetNewValue<OversizedCustomStruct>().Value1);
    }

    [Fact]
    public void WhenConvertedToRollbackChangeAndReadAsObject_ThenValuesAreSwapped()
    {
        // Arrange: both revert paths read a rollback through object, so that is the read that has to
        // hold on every storage path.
        var inlineChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, 1, 2);
        var nullableChange = SubjectPropertyChange.Create<int?>(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, null, 2);
        var stringChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, "old", "new");
        var referenceChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            new CustomClass { Id = 1 }, new CustomClass { Id = 2 });

        // Act
        var inlineRollback = inlineChange.ToRollbackChange();
        var nullableRollback = nullableChange.ToRollbackChange();
        var stringRollback = stringChange.ToRollbackChange();
        var referenceRollback = referenceChange.ToRollbackChange();

        // Assert
        Assert.Equal(2, inlineRollback.GetOldValue<object?>());
        Assert.Equal(1, inlineRollback.GetNewValue<object?>());
        Assert.Equal(2, nullableRollback.GetOldValue<object?>());
        Assert.Null(nullableRollback.GetNewValue<object?>());
        Assert.Equal("new", stringRollback.GetOldValue<object?>());
        Assert.Equal("old", stringRollback.GetNewValue<object?>());
        Assert.Equal(2, Assert.IsType<CustomClass>(referenceRollback.GetOldValue<object?>()).Id);
        Assert.Equal(1, Assert.IsType<CustomClass>(referenceRollback.GetNewValue<object?>()).Id);
    }

    [Fact]
    public void WhenConvertedToRollbackChange_ThenValuesKeepTheirDeclaredStorageType()
    {
        // Arrange: moving the storage rather than re-boxing through object means a rollback refuses a
        // narrower type exactly as the change it inverts does. A nullable stores its own type inline,
        // so int? does not read back as int on either of them.
        var change = SubjectPropertyChange.Create<int?>(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, 1, 2);

        // Act
        var rollback = change.ToRollbackChange();

        // Assert
        Assert.False(rollback.TryGetOldValue<int>(out _));
        Assert.False(change.TryGetOldValue<int>(out _));
    }

    [Fact]
    public void WhenConvertedToRollbackChange_ThenNothingIsAllocatedOnAnyStoragePath()
    {
        // Arrange: the conversion moves the storage fields instead of round-tripping both values
        // through object. Rebuilding it through Create<object?> would cost two boxed holders per
        // reverted change plus a box per inline value, so every storage path has to measure zero:
        // measuring only the inline one would miss a partial revert on the two holder paths.
        var inlineChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, 1, 2);
        var stringChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp, "old", "new");
        var referenceChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            new CustomClass { Id = 1 }, new CustomClass { Id = 2 });
        var oversizedChange = SubjectPropertyChange.Create(
            _property, origin: ChangeOrigin.Local, _changedTimestamp, _receivedTimestamp,
            new OversizedCustomStruct { Value1 = 1L }, new OversizedCustomStruct { Value1 = 2L });

        // Act
        var inlineAllocated = MeasureRollbackAllocations(inlineChange, c => c.GetNewValue<int>());
        var stringAllocated = MeasureRollbackAllocations(stringChange, c => c.GetNewValue<string>().Length);
        var referenceAllocated = MeasureRollbackAllocations(referenceChange, c => c.GetNewValue<CustomClass>().Id);
        var oversizedAllocated = MeasureRollbackAllocations(
            oversizedChange, c => (int)c.GetNewValue<OversizedCustomStruct>().Value1);

        // Assert
        Assert.Equal(0L, inlineAllocated);
        Assert.Equal(0L, stringAllocated);
        Assert.Equal(0L, referenceAllocated);
        Assert.Equal(0L, oversizedAllocated);
    }

    /// <summary>
    /// Converts <paramref name="change"/> in a tight loop and returns the bytes allocated. The read is
    /// passed in as a non-capturing lambda, so its delegate is cached before the measured window and the
    /// result is consumed, which keeps the conversion from being optimized away.
    /// </summary>
    private static long MeasureRollbackAllocations(
        SubjectPropertyChange change, Func<SubjectPropertyChange, int> read)
    {
        // Warm up so JIT compilation does not land inside the measured window.
        var accumulator = 0L;
        for (var i = 0; i < 100; i++)
        {
            accumulator += read(change.ToRollbackChange());
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            accumulator += read(change.ToRollbackChange());
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEqual(0L, accumulator);
        return allocated;
    }

    [Fact]
    public void WhenMeasuringSubjectPropertyChange_ThenSizeStaysWithinTheAcceptedBudget()
    {
        // The struct is copied on every publish, so growth is a hot-path cost that has to be a decision
        // rather than a side effect. The commit revision took it up by one alignment slot, to the 144
        // bytes measured here, and that growth is accepted. The bound is the exact measurement: slack
        // would let the next field through unnoticed.
        var size = System.Runtime.CompilerServices.Unsafe.SizeOf<SubjectPropertyChange>();
        Assert.True(size <= 144, $"SubjectPropertyChange grew to {size} bytes");
    }
}
