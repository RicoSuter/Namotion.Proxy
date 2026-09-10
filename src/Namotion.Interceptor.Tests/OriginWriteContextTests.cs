using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public enum ProbeMode
{
    Idle = 0,
    Running = 1,
    Fault = 2
}

/// <summary>Dedicated model with a reference-typed and a nullable-enum property for the survival tests.</summary>
[InterceptorSubject]
public partial class OriginProbeSubject
{
    public partial string? Name { get; set; }

    public partial ProbeMode? Mode { get; set; }
}

public class OriginWriteContextTests
{
    private sealed class OriginProbe : IWriteInterceptor
    {
        public ChangeOriginKind? KindBeforeWrite;
        public ChangeOriginKind? KindAfterWrite;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            KindBeforeWrite = context.Origin.Kind;
            next(ref context);
            KindAfterWrite = context.Origin.Kind;
        }
    }

    private sealed class ResolvingOriginProbe : IWriteInterceptor
    {
        public ChangeOriginKind? ResolvedKind { get; private set; }
        public ChangeOriginKind? KindAfterResolution { get; private set; }

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            ResolvedKind = context.GetFinalOrigin().Kind;
            KindAfterResolution = context.Origin.Kind;
            next(ref context);
        }
    }

    [Fact]
    public void WhenWriteIsSetFromSource_ThenOriginIsFromSourceBeforeAndAfterWrite()
    {
        // Arrange
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");
        var source = new object();

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(source), 42))
        {
            car.Speed = 42;
        }

        // Assert
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenStoredValueDiffersFromSentValue_ThenOriginIsFinalizedToLocal()
    {
        // Arrange: set the pending origin with sentValue 42 but write a different
        // value, as a transforming hook or rewriting interceptor would.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), 42))
        {
            car.Speed = 99;
        }

        // Assert: attempted origin visible mid-chain, Local after finalization.
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenFinalOriginIsResolvedBeforeWrite_ThenTheAttemptedOriginRemainsAvailableToTheTerminal()
    {
        // Arrange
        var probe = new ResolvingOriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), 42))
        {
            car.Speed = 99;
        }

        // Assert
        Assert.Equal(ChangeOriginKind.Local, probe.ResolvedKind);
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindAfterResolution);
        Assert.True(property.TryGetWriteState(true, out var anyRevision, out _));
        Assert.True(anyRevision > 0);
        Assert.True(property.TryGetWriteState(false, out var localRevision, out _));
        Assert.Equal(0, localRevision);
    }

    [Fact]
    public void WhenSentValueIsNullForValueTypeProperty_ThenOriginIsFinalizedToLocalWithoutThrowing()
    {
        // Arrange: a stamped write on a value-type property whose sent value is null (a malformed
        // inbound value). The survival check must demote to Local via the typed-pattern unbox instead
        // of throwing when it casts null down to the value type.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), sentValue: null))
        {
            car.Speed = 55;
        }

        // Assert: no throw; attempted origin visible mid-chain, demoted to Local after finalization.
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenSentValueTypeMismatchesValueTypeProperty_ThenOriginIsFinalizedToLocalWithoutThrowing()
    {
        // Arrange: the sent value is a boxed string but the property is an int, so the typed-pattern
        // unbox fails and the origin demotes to Local rather than throwing an InvalidCastException.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);
        var property = new PropertyReference(car, "Speed");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), sentValue: "55"))
        {
            car.Speed = 55;
        }

        // Assert
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenSentValueIsNullForReferenceTypeProperty_ThenOriginSurvivesAsFromSource()
    {
        // Arrange: a source clears a reference-typed property to null (old value non-null, so the write
        // proceeds and stores null faithfully). `null is TProperty` is always false in C#, so the
        // survival check must special-case null-matches-null to keep the origin FromSource; otherwise
        // the null would wrongly demote to Local and be echoed back to the source.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var subject = new OriginProbeSubject(context) { Name = "initial" };
        var property = new PropertyReference(subject, "Name");

        // Act
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), sentValue: null))
        {
            subject.Name = null;
        }

        // Assert: null was faithfully stored, so the origin survives as FromSource (not demoted).
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenSentValueIsNullableEnumUnderlyingInteger_ThenOriginSurvivesAsFromSource()
    {
        // Arrange: a source delivers a nullable enum as its boxed underlying integer (the OPC UA wire
        // shape). The write stores the enum faithfully, so the origin must survive as FromSource. Casting
        // the boxed integer to the nullable enum throws, so the survival check must unwrap Nullable<T> and
        // coerce the underlying integer to the enum before comparing; otherwise the value demotes to Local
        // and is echoed straight back to the source.
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var subject = new OriginProbeSubject(context);
        var property = new PropertyReference(subject, "Mode");

        // Act: sentValue is the boxed underlying integer while the stored value is the enum.
        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(new object()), sentValue: (int)ProbeMode.Fault))
        {
            subject.Mode = ProbeMode.Fault;
        }

        // Assert
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.FromSource, probe.KindAfterWrite);
    }

    [Fact]
    public void WhenWriteHasNoPendingOrigin_ThenOriginIsLocal()
    {
        // Arrange
        var probe = new OriginProbe();
        var context = InterceptorSubjectContext.Create();
        context.AddService(probe);
        var car = new Car(context);

        // Act
        car.Speed = 7;

        // Assert
        Assert.Equal(ChangeOriginKind.Local, probe.KindBeforeWrite);
        Assert.Equal(ChangeOriginKind.Local, probe.KindAfterWrite);
    }
}
