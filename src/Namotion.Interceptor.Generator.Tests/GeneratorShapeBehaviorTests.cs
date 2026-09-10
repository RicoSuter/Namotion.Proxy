using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Generator.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Generator.Tests;

#region Case S: a non-public subject

[InterceptorSubject]
internal partial class InternalSubject
{
    public partial string Name { get; set; }
}

#endregion

#region Case W: internal and protected internal default members stay supported

public interface IAccessibleDefaults
{
    double Value { get; set; }

    internal string InternalStatus => "internal-" + Value;

    protected internal string ProtectedInternalStatus => "protected-internal-" + Value;
}

[InterceptorSubject]
public partial class AccessibleDefaultsSubject : IAccessibleDefaults
{
    public partial double Value { get; set; }
}

#endregion

#region Case P: a subject nested in a record

public partial record RecordContainer
{
    [InterceptorSubject]
    public partial class NestedSubject
    {
        public partial string Name { get; set; }
    }
}

#endregion

#region Case Y: a "ref readonly" parameter on a WithoutInterceptor method

[InterceptorSubject]
public partial class RefReadonlyMethodSubject
{
    public partial int Received { get; set; }

    public void SendWithoutInterceptor(ref readonly int value)
    {
        Received = value;
    }

    public int DoubleWithoutInterceptor(ref readonly int value)
    {
        return value * 2;
    }
}

#endregion

#region Case Y2: an "in" parameter on a WithoutInterceptor method

[InterceptorSubject]
public partial class InParameterMethodSubject
{
    public partial int Received { get; set; }

    public void SendWithoutInterceptor(in int value)
    {
        Received = value;
    }
}

#endregion

#region Case NP: a "new" partial property that hides a plain base member

public class NewPartialBase
{
    public string Label { get; set; } = "base";
}

[InterceptorSubject]
public partial class NewPartialSubject : NewPartialBase
{
    public new partial string Label { get; set; }
}

#endregion

#region Case SO: a "sealed override" partial property

[InterceptorSubject]
public partial class SealedOverrideBase
{
    public virtual partial string Label { get; set; }
}

[InterceptorSubject]
public partial class SealedOverrideSubject : SealedOverrideBase
{
    public sealed override partial string Label { get; set; }
}

#endregion

#region Case RM: a required partial property initialized by a [SetsRequiredMembers] constructor

[InterceptorSubject]
public partial class RequiredMemberSubject
{
    public required partial string Name { get; set; }

    [SetsRequiredMembers]
    public RequiredMemberSubject()
    {
        Name = "default";
    }
}

#endregion

#region Case RM3: a subject chain whose root constructor sets required members

[InterceptorSubject]
public partial class RequiredMemberBaseSubject
{
    public required partial string Id { get; set; }

    [SetsRequiredMembers]
    public RequiredMemberBaseSubject()
    {
        Id = "base-default";
    }
}

[InterceptorSubject]
public partial class RequiredMemberMiddleSubject : RequiredMemberBaseSubject
{
    public partial string Name { get; set; }
}

[InterceptorSubject]
public partial class RequiredMemberLeafSubject : RequiredMemberMiddleSubject
{
    public partial string Label { get; set; }
}

#endregion

public class GeneratorShapeBehaviorTests
{
    [Fact]
    public void WhenSubjectIsInternal_ThenPropertiesAreTracked()
    {
        // Arrange
        var readInterceptor = new RecordingReadInterceptor();
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService(() => readInterceptor)
            .WithService(() => writeInterceptor);

        var subject = new InternalSubject(context);
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        subject.Name = "value";
        var value = subject.Name;

        // Assert
        Assert.Equal("value", value);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Name" && Equals(write.Value, "value"));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "Name" && Equals(read.Value, "value"));
        Assert.Equal(["Name"], firedEvents);

        var registeredSubject = subject.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var nameProperty = registeredSubject.TryGetProperty("Name");
        Assert.NotNull(nameProperty);
        Assert.Equal("value", nameProperty.GetValue());
    }

    [Fact]
    public void WhenDefaultMemberIsInternalOrProtectedInternal_ThenItRemainsExposed()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var subject = new AccessibleDefaultsSubject(context) { Value = 3 };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;
        var registeredSubject = subject.TryGetRegisteredSubject();

        // Assert
        Assert.Equal("internal-3", properties["InternalStatus"].GetValue?.Invoke(subject));
        Assert.Equal("protected-internal-3", properties["ProtectedInternalStatus"].GetValue?.Invoke(subject));

        Assert.NotNull(registeredSubject);
        var internalStatusProperty = registeredSubject.TryGetProperty("InternalStatus");
        var protectedInternalStatusProperty = registeredSubject.TryGetProperty("ProtectedInternalStatus");
        Assert.NotNull(internalStatusProperty);
        Assert.NotNull(protectedInternalStatusProperty);
        Assert.Equal("internal-3", internalStatusProperty.GetValue());
        Assert.Equal("protected-internal-3", protectedInternalStatusProperty.GetValue());
    }

    [Fact]
    public void WhenPartialPropertyBesideAccessibleDefaultsIsWrittenAndRead_ThenInterceptorsObserveTheAccessAndPropertyChangedFires()
    {
        // Arrange: "Value" is the partial property that actually routes through the interceptor
        // chain, unlike the internal/protected internal interface defaults declared beside it,
        // which stay direct computed reads and are asserted separately above.
        var readInterceptor = new RecordingReadInterceptor();
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => readInterceptor)
            .WithService(() => writeInterceptor);

        var subject = new AccessibleDefaultsSubject(context);
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        subject.Value = 5;
        var value = subject.Value;

        // Assert
        Assert.Equal(5, value);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Value" && Equals(write.Value, 5.0));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "Value" && Equals(read.Value, 5.0));
        Assert.Equal(["Value"], firedEvents);
    }

    [Fact]
    public void WhenSubjectIsNestedInRecord_ThenPropertiesAreTracked()
    {
        // Arrange
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService(() => writeInterceptor);

        var subject = new RecordContainer.NestedSubject(context);
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        subject.Name = "value";

        // Assert
        Assert.Equal("value", subject.Name);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Name" && Equals(write.Value, "value"));
        Assert.Equal(["Name"], firedEvents);

        var registeredSubject = subject.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var nameProperty = registeredSubject.TryGetProperty("Name");
        Assert.NotNull(nameProperty);
        Assert.Equal("value", nameProperty.GetValue());
    }

    [Fact]
    public void WhenWithoutInterceptorMethodTakesRefReadonlyParameter_ThenTheWrapperRoutesThroughTheMethodInterceptor()
    {
        // Arrange: the wrapper boxes the argument and passes a readonly reference to the unboxed
        // temporary, so the value has to arrive unchanged in the wrapped method, and the call must
        // still be visible to the method interceptor chain rather than bypass it.
        var methodInterceptor = new RecordingMethodInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => methodInterceptor);

        var subject = new RefReadonlyMethodSubject(context);

        // Act
        subject.Send(42);
        var doubled = subject.Double(21);

        // Assert
        Assert.Equal(42, subject.Received);
        Assert.Equal(42, doubled);
        Assert.Contains(methodInterceptor.Invocations,
            invocation => invocation.MethodName == "Send" && invocation.Parameters.SequenceEqual(new object?[] { 42 }));
        Assert.Contains(methodInterceptor.Invocations,
            invocation => invocation.MethodName == "Double" && invocation.Parameters.SequenceEqual(new object?[] { 21 }));
    }

    [Fact]
    public void WhenWithoutInterceptorMethodTakesInParameter_ThenTheWrapperRoutesThroughTheMethodInterceptor()
    {
        // Arrange: the "in" argument is passed by value into the generated wrapper, which forwards
        // it to the wrapped method through the "in" parameter, and the call must be visible to the
        // method interceptor chain rather than bypass it.
        var methodInterceptor = new RecordingMethodInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => methodInterceptor);

        var subject = new InParameterMethodSubject(context);

        // Act
        subject.Send(42);

        // Assert
        Assert.Equal(42, subject.Received);
        Assert.Contains(methodInterceptor.Invocations,
            invocation => invocation.MethodName == "Send" && invocation.Parameters.SequenceEqual(new object?[] { 42 }));
    }

    [Fact]
    public void WhenBaseClassIsNamedOnADifferentPartialDeclaration_ThenBaseAndOwnPropertiesAreIntercepted()
    {
        // Arrange (Contractor): [InterceptorSubject] and the ": PersonBase" base list live on two
        // different partial declarations of the same class, which is exactly the shape the
        // base-class fix repaired. "Agency" is Contractor's own partial property, so it must
        // round-trip through the interceptor chain like any other. "FirstName" is inherited
        // (not redeclared) from PersonBase, a separately [InterceptorSubject]-attributed class.
        //
        // Both properties are asserted against the interceptors. "FirstName" used to be asserted
        // against value, PropertyChanged and the registry instead, because every subject in a
        // hierarchy emitted its own _executor and only the most derived one was ever populated, so
        // base-declared properties took the no-interception fast path. The interception
        // members now live once in the root, so a base-declared write is observable like any other.
        var readInterceptor = new RecordingReadInterceptor();
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService(() => readInterceptor)
            .WithService(() => writeInterceptor);

        var contractor = new Contractor(context);
        var firedEvents = new List<string>();
        contractor.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        contractor.FirstName = "Rico";
        contractor.Agency = "Acme";
        var firstName = contractor.FirstName;
        var agency = contractor.Agency;

        // Assert
        Assert.Equal("Rico", firstName);
        Assert.Equal("Acme", agency);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Agency" && Equals(write.Value, "Acme"));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "Agency" && Equals(read.Value, "Acme"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "FirstName" && Equals(write.Value, "Rico"));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "FirstName" && Equals(read.Value, "Rico"));
        Assert.Equal(["FirstName", "Agency"], firedEvents);

        Assert.True(Contractor.DefaultProperties.ContainsKey("FirstName"));
        Assert.True(Contractor.DefaultProperties.ContainsKey("Agency"));

        var registeredSubject = contractor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSubject);
        var firstNameProperty = registeredSubject.TryGetProperty("FirstName");
        var agencyProperty = registeredSubject.TryGetProperty("Agency");
        Assert.NotNull(firstNameProperty);
        Assert.NotNull(agencyProperty);
        Assert.Equal("Rico", firstNameProperty.GetValue());
        Assert.Equal("Acme", agencyProperty.GetValue());
    }

    [Fact]
    public void WhenNewPartialPropertyHidesBaseMember_ThenInterceptorsObserveTheAccessAndPropertyChangedFires()
    {
        // Arrange (case NP): "new" hides NewPartialBase.Label, a plain auto-property with no
        // interception of its own, so only the derived "new partial" half may legitimately route
        // through the interceptor chain.
        var readInterceptor = new RecordingReadInterceptor();
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => readInterceptor)
            .WithService(() => writeInterceptor);

        var subject = new NewPartialSubject(context);
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        subject.Label = "new-value";
        var value = subject.Label;

        // Assert
        Assert.Equal("new-value", value);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Label" && Equals(write.Value, "new-value"));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "Label" && Equals(read.Value, "new-value"));
        Assert.Equal(["Label"], firedEvents);
    }

    [Fact]
    public void WhenSealedOverridePartialPropertyIsWrittenAndRead_ThenInterceptorsObserveTheAccessAndPropertyChangedFires()
    {
        // Arrange (case SO): "sealed" is only legal paired with "override", so this proves the
        // sealed override half is what is actually wired into the interceptor chain, not merely
        // legal syntax accepted by the compiler.
        var readInterceptor = new RecordingReadInterceptor();
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => readInterceptor)
            .WithService(() => writeInterceptor);

        var subject = new SealedOverrideSubject(context);
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        subject.Label = "sealed-value";
        var value = subject.Label;

        // Assert
        Assert.Equal("sealed-value", value);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Label" && Equals(write.Value, "sealed-value"));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "Label" && Equals(read.Value, "sealed-value"));
        Assert.Equal(["Label"], firedEvents);
    }

    [Fact]
    public void WhenChainedConstructorSetsRequiredMembers_ThenTheContextConstructorNeedsNoInitializer()
    {
        // Arrange (case RM)
        var context = InterceptorSubjectContext.Create();

        // Act: the required member is satisfied by the chained constructor, so this call compiles
        // without an object initializer.
        var subject = new RequiredMemberSubject(context);

        // Assert
        Assert.Equal("default", subject.Name);
    }

    [Fact]
    public void WhenBaseConstructorSetsRequiredMembers_ThenTheDerivedConstructorsNeedNoInitializer()
    {
        // Arrange (case RM3): the generated constructors of the middle subject and of the leaf below
        // it chain implicitly up to the base constructor, so all four inherit its claim and none of
        // these calls needs an object initializer.
        var context = InterceptorSubjectContext.Create();

        // Act
        var middleSubject = new RequiredMemberMiddleSubject();
        var middleContextSubject = new RequiredMemberMiddleSubject(context);
        var leafSubject = new RequiredMemberLeafSubject();
        var leafContextSubject = new RequiredMemberLeafSubject(context);

        // Assert
        Assert.Equal("base-default", middleSubject.Id);
        Assert.Equal("base-default", middleContextSubject.Id);
        Assert.Equal("base-default", leafSubject.Id);
        Assert.Equal("base-default", leafContextSubject.Id);
    }
}
