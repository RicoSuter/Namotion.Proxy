using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins how a dynamic property's boxed write enters the interceptor chain: the metadata setter
/// unboxes to the declared type and calls the typed write entry, so interceptors see the same
/// <c>TProperty</c> a generated property of that type would produce. A write whose value is not
/// representable in the declared type (a null into a non-nullable value type, say) must keep the
/// pre-typed behavior and arrive at the stored setter untouched through an object-typed chain.
/// </summary>
public class DynamicPropertyTypedChainTests
{
    private sealed class ChainTypeRecordingInterceptor : IWriteInterceptor
    {
        public readonly List<(Type PropertyType, object? NewValue, object? CurrentValue)> Writes = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Writes.Add((typeof(TProperty), context.NewValue, context.CurrentValue));
            next(ref context);
        }
    }

    [Fact]
    public void WhenDynamicPropertyDeclaredTypeIsScalar_ThenChainCarriesTheDeclaredTypeNotObject()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new ChainTypeRecordingInterceptor();
        context.AddService(probe);

        IInterceptorSubject person = new Person();
        person.AttachToContext(context);

        var registeredSubject = new RegisteredSubject(person);
        var storedValue = 0.0;
        registeredSubject.AddProperty(
            "Temperature",
            typeof(double),
            _ => storedValue,
            (_, value) => storedValue = (double)value!);

        // Act
        person.Properties["Temperature"].SetValue!(person, 42.0);

        // Assert: registration publishes the initial value first, as a null-to-value write that no
        // double chain can carry, so the assignment is the second write. That one runs the same
        // double chain a generated double property would, so TProperty is double, not object.
        Assert.Equal(2, probe.Writes.Count);
        var write = probe.Writes[1];
        Assert.Equal(typeof(double), write.PropertyType);
        Assert.Equal(42.0, write.NewValue);
        Assert.Equal(0.0, write.CurrentValue);
        Assert.Equal(42.0, storedValue);
    }

    [Fact]
    public void WhenNullIsWrittenIntoNonNullableValueTypeDynamicProperty_ThenSetterStillReceivesNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new ChainTypeRecordingInterceptor();
        context.AddService(probe);

        IInterceptorSubject person = new Person();
        person.AttachToContext(context);

        var registeredSubject = new RegisteredSubject(person);
        object? storedValue = 42.0;
        registeredSubject.AddProperty(
            "Temperature",
            typeof(double),
            _ => storedValue,
            (_, value) => storedValue = value);

        // Act
        person.Properties["Temperature"].SetValue!(person, null);

        // Assert: null cannot travel a double chain, so the write falls back to the object-typed
        // chain and the stored setter receives the null unchanged, as it always did. The first
        // write is the initial value registration publishes.
        Assert.Equal(2, probe.Writes.Count);
        var write = probe.Writes[1];
        Assert.Equal(typeof(object), write.PropertyType);
        Assert.Null(write.NewValue);
        Assert.Equal(42.0, write.CurrentValue);
        Assert.Null(storedValue);
    }
}
