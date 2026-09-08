using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Generator.Tests;

#region Case A: explicit implementation in a sub-interface

public enum CaseAGender { Male, Female }

public interface ICaseAHuman
{
    CaseAGender Gender { get; }
}

public interface ICaseAMale : ICaseAHuman
{
    CaseAGender ICaseAHuman.Gender => CaseAGender.Male;
}

[InterceptorSubject]
public partial class CaseAJohn : ICaseAMale
{
    // An ordinary intercepted property alongside the explicitly implemented one, so a test can
    // show the interceptor chain observing this read while "Gender" stays absent from it.
    public partial string Name { get; set; }
}

#endregion

#region Case I: a base class implementation beats an interface default

public interface ICaseIHuman
{
    string Origin => "interface-default";
}

public class CaseIBase : ICaseIHuman
{
    string ICaseIHuman.Origin => "base-class-explicit";
}

[InterceptorSubject]
public partial class CaseIDerived : CaseIBase
{
}

#endregion

#region Case AA: two explicit implementations of one generic interface, at different instantiations

// Deduplication (Task 4) keeps this from emitting duplicate dictionary keys once the name
// resolution below makes both entries resolve to "Kind". NI0008 reports the collision from
// Task 11; the suppression is placed now so that task does not break this file's build.
#pragma warning disable NI0008

public interface ICaseAAFoo<T>
{
    string Kind { get; }
}

[InterceptorSubject]
public partial class CaseAASubject : ICaseAAFoo<int>, ICaseAAFoo<string>
{
    string ICaseAAFoo<int>.Kind => "int";
    string ICaseAAFoo<string>.Kind => "string";
}

#pragma warning restore NI0008

#endregion

#region Case Z: a class that declares a property and explicitly implements the same member

public interface ICaseZKind
{
    string Kind { get; }
}

[InterceptorSubject]
public partial class CaseZSubject : ICaseZKind
{
    public partial string Kind { get; set; }

    string ICaseZKind.Kind => "explicit";
}

#endregion

#region Case AD: base implements, derived re-declares. Intentional, so NI0005 is suppressed.

#pragma warning disable NI0005

public interface ICaseADHuman
{
    string Origin => "interface-default";
}

public class CaseADBase : ICaseADHuman
{
}

[InterceptorSubject]
public partial class CaseADDerived : CaseADBase
{
    public partial string Origin { get; set; }
}

#pragma warning restore NI0005

#endregion

#region Inheritance regression: an override partial property must not duplicate the base key

[InterceptorSubject]
public partial class OverrideBase
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class OverrideDerived : OverrideBase
{
    public override partial string Name { get; set; }
}

#endregion

public class ExplicitInterfaceBehaviorTests
{
    [Fact]
    public void WhenSubInterfaceExplicitlyImplementsMember_ThenSubjectExposesItByMemberName()
    {
        // Arrange
        var john = new CaseAJohn();

        // Act
        var properties = ((IInterceptorSubject)john).Properties;

        // Assert
        Assert.True(properties.ContainsKey("Gender"));
        Assert.Equal(CaseAGender.Male, properties["Gender"].GetValue?.Invoke(john));
    }

    [Fact]
    public void WhenBaseClassImplementsAndInterfaceHasDefault_ThenBaseClassImplementationWins()
    {
        // Arrange
        var derived = new CaseIDerived();

        // Act
        var value = ((IInterceptorSubject)derived).Properties["Origin"].GetValue?.Invoke(derived);

        // Assert
        Assert.Equal("base-class-explicit", value);
    }

    [Fact]
    public void WhenTwoExplicitImplementationsCollideOnName_ThenOneEntryIsExposed()
    {
        // Arrange (case AA)
        var subject = new CaseAASubject();

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert: first declaration wins, and reading DefaultProperties does not throw
        Assert.Single(properties, p => p.Key == "Kind");
        Assert.Equal("int", properties["Kind"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenClassDeclaresAndExplicitlyImplementsSameProperty_ThenSinglePropertyIsExposed()
    {
        // Arrange
        var subject = new CaseZSubject { Kind = "tracked" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Single(properties, p => p.Key == "Kind");
        Assert.Equal("tracked", properties["Kind"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenDerivedRedeclaresBaseImplementedProperty_ThenSubjectAndInterfaceDiffer()
    {
        // Arrange (case AD): the divergence NI0005 warns about, pinned as behaviour
        var derived = new CaseADDerived { Origin = "derived" };

        // Act
        var throughInterface = ((ICaseADHuman)derived).Origin;

        // Assert
        Assert.Equal("derived", derived.Origin);
        Assert.Equal("interface-default", throughInterface);
    }

    [Fact]
    public void WhenDerivedOverridesPartialProperty_ThenSingleKeyIsExposed()
    {
        // Arrange
        var subject = new OverrideDerived { Name = "value" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Single(properties, p => p.Key == "Name");
        Assert.Equal("value", properties["Name"].GetValue?.Invoke(subject));
    }

    [Fact]
    public void WhenOverridePartialPropertyIsWrittenAndRead_ThenInterceptorsObserveTheAccessAndPropertyChangedFires()
    {
        // Arrange: "Name" is virtual on OverrideBase and overridden on OverrideDerived, so this
        // proves the override half is the one actually wired into the interceptor chain, rather
        // than a shadow that only satisfies the generated-text assertions elsewhere.
        var readInterceptor = new RecordingReadInterceptor();
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => readInterceptor)
            .WithService(() => writeInterceptor);

        var subject = new OverrideDerived(context);
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
    }

    [Fact]
    public void WhenDeclaredPropertyIsWrittenAndRead_ThenInterceptorsObserveTheAccessAndPropertyChangedFires()
    {
        // Arrange (case Z): "Kind" is the partial property, the one actually routed through the
        // interceptor chain, as opposed to its same-named explicit interface member below.
        var readInterceptor = new RecordingReadInterceptor();
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => readInterceptor)
            .WithService(() => writeInterceptor);

        var subject = new CaseZSubject(context);
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        subject.Kind = "tracked";
        var value = subject.Kind;

        // Assert
        Assert.Equal("tracked", value);
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Kind" && Equals(write.Value, "tracked"));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "Kind" && Equals(read.Value, "tracked"));
        Assert.Equal(["Kind"], firedEvents);
    }

    [Fact]
    public void WhenExplicitlyImplementedPropertyIsRead_ThenItIsNotInterceptedButRegistryReportsTheValue()
    {
        // Arrange (case A): ICaseAMale explicitly implements Gender with a fixed value, so there is
        // no generated wrapper for it, and reading it can never reach the interceptor chain.
        var readInterceptor = new RecordingReadInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => readInterceptor);

        var john = new CaseAJohn(context) { Name = "John" };

        // Act
        var genderThroughInterface = ((ICaseAHuman)john).Gender;
        var genderThroughMetadata = CaseAJohn.DefaultProperties["Gender"].GetValue?.Invoke(john);
        var nameThroughSubject = john.Name;
        var registeredSubject = john.TryGetRegisteredSubject();

        // Assert
        Assert.Equal(CaseAGender.Male, genderThroughInterface);
        Assert.Equal(CaseAGender.Male, genderThroughMetadata);
        Assert.Equal("John", nameThroughSubject);

        // "Name" proves the interceptor is wired and recording, which is what makes the absence of
        // "Gender" below meaningful rather than vacuously true of an inert interceptor.
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "Name");
        Assert.DoesNotContain(readInterceptor.Reads, read => read.PropertyName == "Gender");
        Assert.False(CaseAJohn.DefaultProperties["Gender"].IsIntercepted);
        Assert.True(CaseAJohn.DefaultProperties["Name"].IsIntercepted);

        Assert.NotNull(registeredSubject);
        var genderProperty = registeredSubject.TryGetProperty("Gender");
        Assert.NotNull(genderProperty);
        Assert.Equal(CaseAGender.Male, genderProperty.GetValue());
    }

    [Fact]
    public void WhenExplicitlyImplementedPropertyIsRegistered_ThenRegistryReportsTheValue()
    {
        // Arrange: Alice (defined in ReportedIssueTests.cs) reaches Rank through ISenior's explicit
        // implementation of IEmployee.Rank, the same shape as CaseAJohn's Gender above.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var alice = new Alice(context);

        // Act
        var registeredSubject = alice.TryGetRegisteredSubject();

        // Assert
        Assert.NotNull(registeredSubject);
        var rankProperty = registeredSubject.TryGetProperty("Rank");
        Assert.NotNull(rankProperty);
        Assert.Equal(Rank.Senior, rankProperty.GetValue());
    }

}
