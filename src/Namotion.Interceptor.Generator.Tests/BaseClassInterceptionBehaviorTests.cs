using System.Reactive.Concurrency;
using System.Reflection;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

[InterceptorSubject]
public partial class HierarchyChild
{
    public partial string ChildName { get; set; }
}

[InterceptorSubject]
public partial class HierarchyRoot
{
    public partial string RootProperty { get; set; }

    public partial HierarchyChild? Child { get; set; }

    public HierarchyRoot()
    {
        RootProperty = "";
    }

    // The generator wraps a method by its "WithoutInterceptor" postfix; there is no partial method
    // form. The emitted wrapper is the intercepted "Describe".
    public string DescribeWithoutInterceptor(string prefix) => prefix + RootProperty;
}

[InterceptorSubject]
public partial class HierarchyMiddle : HierarchyRoot
{
    public partial string MiddleProperty { get; set; }

    public HierarchyMiddle()
    {
        MiddleProperty = "";
    }
}

[InterceptorSubject]
public partial class HierarchyLeaf : HierarchyMiddle
{
    public partial string LeafProperty { get; set; }

    public HierarchyLeaf()
    {
        LeafProperty = "";
    }
}

/// <summary>
/// The only fixture with a hand-written ": base(context)" constructor. Everywhere else the
/// generator emits "Leaf(IInterceptorSubjectContext context) : this()", which attaches to the
/// context after the whole this() chain and therefore cannot intercept anything written during
/// construction.
/// </summary>
[InterceptorSubject]
public partial class HierarchyContextLeaf : HierarchyMiddle
{
    public const string WrittenInConstructor = "written-in-constructor";

    public partial string ContextLeafProperty { get; set; }

    public HierarchyContextLeaf(IInterceptorSubjectContext context) : base(context)
    {
        ContextLeafProperty = "";
        RootProperty = WrittenInConstructor;
    }
}

public class BaseClassInterceptionBehaviorTests
{
    [Fact]
    public void WhenPropertyIsDeclaredOnABaseSubject_ThenWritesAreObservedByTheInterceptor()
    {
        // Arrange: the value and PropertyChanged both work today while the bug is present, so the
        // assertion has to be interceptor observation.
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var leaf = new HierarchyLeaf(context);

        // Act
        leaf.RootProperty = "r";
        leaf.MiddleProperty = "m";
        leaf.LeafProperty = "l";

        // Assert
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "RootProperty" && Equals(w.Value, "r"));
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "MiddleProperty" && Equals(w.Value, "m"));
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "LeafProperty" && Equals(w.Value, "l"));
    }

    [Fact]
    public void WhenPropertiesFromEveryLevelAreWritten_ThenAChangeIsPublishedForEachOfThem()
    {
        // Arrange: a recording interceptor shows a write entered the chain, which is one step short
        // of what the bug cost. The OPC UA and MQTT connectors consume SubjectPropertyChange off the
        // change observable, so this subscribes there instead. ImmediateScheduler delivers on the
        // writing thread, which is why no synchronisation is needed below.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var leaf = new HierarchyLeaf(context);

        // Act
        leaf.RootProperty = "root-value";
        leaf.MiddleProperty = "middle-value";
        leaf.LeafProperty = "leaf-value";

        // Assert
        Assert.Contains(changes, change => change.Property.Name == "RootProperty" && change.GetNewValue<string>() == "root-value");
        Assert.Contains(changes, change => change.Property.Name == "MiddleProperty" && change.GetNewValue<string>() == "middle-value");
        Assert.Contains(changes, change => change.Property.Name == "LeafProperty" && change.GetNewValue<string>() == "leaf-value");
        Assert.All(changes, change => Assert.Same(leaf, change.Property.Subject));
    }

    [Fact]
    public void WhenPropertyIsDeclaredOnABaseSubject_ThenReadsAreObservedByTheInterceptor()
    {
        // Arrange
        var readInterceptor = new RecordingReadInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => readInterceptor);

        var leaf = new HierarchyLeaf(context);
        leaf.RootProperty = "r";

        // Act
        var value = leaf.RootProperty;

        // Assert
        Assert.Equal("r", value);
        Assert.Contains(readInterceptor.Reads, r => r.PropertyName == "RootProperty");
    }

    [Fact]
    public void WhenHierarchyIsThreeLevelsDeep_ThenPropertiesReportsEveryLevel()
    {
        // Arrange & Act
        var leaf = new HierarchyLeaf();
        var properties = ((IInterceptorSubject)leaf).Properties;

        // Assert: through the interface, which is what catches a regression that moves Properties
        // into the root, and on the statics, which catches a broken Concat chain.
        Assert.Contains("RootProperty", properties.Keys);
        Assert.Contains("MiddleProperty", properties.Keys);
        Assert.Contains("LeafProperty", properties.Keys);
        Assert.Equal(4, HierarchyLeaf.DefaultProperties.Count);
        Assert.Equal(3, HierarchyMiddle.DefaultProperties.Count);
        Assert.Equal(2, HierarchyRoot.DefaultProperties.Count);
    }

    [Fact]
    public void WhenHierarchyIsThreeLevelsDeep_ThenInterceptionMembersAreAllocatedOnce()
    {
        // Arrange & Act
        var leaf = new HierarchyLeaf();

        // Assert: this is the allocation claim. Every extra level used to cost one
        // ConcurrentDictionary and one object per instance. SyncRoot is asserted absent because
        // the terminal lock moved onto the executor, so subjects no longer allocate one at all.
        Assert.Equal(1, CountInstanceFields(leaf.GetType(), "_executor"));
        Assert.Equal(1, CountInstanceFields(leaf.GetType(), "_properties"));
        Assert.Equal(1, CountBackingFields(leaf.GetType(), "Data"));
        Assert.Equal(0, CountBackingFields(leaf.GetType(), "SyncRoot"));
    }

    [Fact]
    public void WhenMethodIsDeclaredOnABaseSubject_ThenInvocationsAreObservedByTheInterceptor()
    {
        // Arrange
        var methodInterceptor = new RecordingMethodInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => methodInterceptor);

        var leaf = new HierarchyLeaf(context);

        // Act
        var described = leaf.Describe("x:");

        // Assert
        Assert.Equal("x:", described);
        Assert.Contains(methodInterceptor.Invocations, i => i.MethodName == "Describe");
    }

    [Fact]
    public void WhenSubjectTypedPropertyIsDeclaredOnABaseSubject_ThenTheChildIsAttachedToTheRegistry()
    {
        // Arrange: this is where the user-visible damage lived. The registry never saw the
        // assignment, so the child subject was never attached, and neither a value assertion nor a
        // plain interceptor assertion covers it.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var leaf = new HierarchyLeaf(context);
        var child = new HierarchyChild();

        // Act
        leaf.Child = child;

        // Assert
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Contains(child, registry.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenAddPropertiesIsCalledOnADerivedSubject_ThenDefaultsFromEveryLevelSurvive()
    {
        // Arrange: AddProperties now lives in the root and merges from
        // ((IInterceptorSubject)this).Properties, so it must start from the leaf's defaults.
        var leaf = new HierarchyLeaf();
        var added = new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true);

        // Act
        ((IInterceptorSubject)leaf).AddProperties(added);
        var properties = ((IInterceptorSubject)leaf).Properties;

        // Assert
        Assert.Contains("Extra", properties.Keys);
        Assert.Contains("RootProperty", properties.Keys);
        Assert.Contains("MiddleProperty", properties.Keys);
        Assert.Contains("LeafProperty", properties.Keys);
    }

    [Fact]
    public void WhenConstructorChainsToTheBaseContextConstructor_ThenItsOwnBodyWritesAreIntercepted()
    {
        // Arrange: the constructor attach reads the subject's Executor through the interface, which
        // dispatches virtually, so a ": base(context)" constructor publishes the executor and the
        // attachment inside the BASE constructor. A base-declared write in the derived constructor
        // body afterwards is therefore intercepted now, where it took the uninterceptable fast path
        // before. This is the fix working, and it is pinned so it does not read as an accident later.
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var leaf = new HierarchyContextLeaf(context);

        // Assert
        Assert.Equal(HierarchyContextLeaf.WrittenInConstructor, leaf.RootProperty);
        Assert.Contains(
            writeInterceptor.Writes,
            w => w.PropertyName == "RootProperty" && Equals(w.Value, HierarchyContextLeaf.WrittenInConstructor));
    }

    private static int CountInstanceFields(Type type, string name)
    {
        var count = 0;
        for (var current = type; current is not null; current = current.BaseType)
        {
            count += current
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Count(field => field.Name == name);
        }

        return count;
    }

    private static int CountBackingFields(Type type, string memberName)
    {
        var count = 0;
        for (var current = type; current is not null; current = current.BaseType)
        {
            count += current
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Count(field => field.Name.Contains(memberName) && field.Name.Contains("BackingField"));
        }

        return count;
    }

}
