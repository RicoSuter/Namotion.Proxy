using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Records every intercepted read, write and method call, which is what separates "the value looks
/// right" from "the executor really saw it". A generated shape is only proven by running it, so these
/// are the evidence most behaviour tests in this project assert on. They live here rather than as a
/// private class per test file because every copy was byte identical and three of them had already
/// drifted apart into separate declarations of the same thing.
/// </summary>
internal sealed class RecordingWriteInterceptor : IWriteInterceptor
{
    public List<(string PropertyName, object? Value)> Writes { get; } = [];

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        Writes.Add((context.Property.Name, context.NewValue));
        next(ref context);
    }
}

/// <inheritdoc cref="RecordingWriteInterceptor"/>
internal sealed class RecordingReadInterceptor : IReadInterceptor
{
    public List<(string PropertyName, object? Value)> Reads { get; } = [];

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
    {
        var value = next(ref context);
        Reads.Add((context.Property.Name, value));
        return value;
    }
}

/// <inheritdoc cref="RecordingWriteInterceptor"/>
internal sealed class RecordingMethodInterceptor : IMethodInterceptor
{
    public List<(string MethodName, object?[] Parameters)> Invocations { get; } = [];

    public object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next)
    {
        Invocations.Add((context.MethodName, context.Parameters));
        return next(ref context);
    }
}
