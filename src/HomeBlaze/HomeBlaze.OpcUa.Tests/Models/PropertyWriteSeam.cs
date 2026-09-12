using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

namespace HomeBlaze.OpcUa.Tests;

/// <summary>
/// Lets a test fail or hold a single property write. The client factory starts by publishing the tree
/// it is about to bind a source to, and that write is the only seam a test has into a factory the
/// wrapper keeps private.
/// </summary>
internal sealed class PropertyWriteSeam : IWriteInterceptor
{
    private Action<PropertyReference, object?>? _beforeWrite;
    private Action<PropertyReference, object?>? _afterWrite;

    /// <summary>
    /// Runs before the write reaches the rest of the chain, so throwing from here fails the write. The
    /// value is the one being written, which is what distinguishes the factory publishing a tree from
    /// the wrapper clearing one.
    /// </summary>
    public void ArmBeforeWrite(Action<PropertyReference, object?>? action) => Volatile.Write(ref _beforeWrite, action);

    /// <summary>
    /// Runs once the write has committed, so blocking here holds the caller with the new value visible.
    /// </summary>
    public void ArmAfterWrite(Action<PropertyReference, object?>? action) => Volatile.Write(ref _afterWrite, action);

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        Volatile.Read(ref _beforeWrite)?.Invoke(context.Property, context.NewValue);
        next(ref context);
        Volatile.Read(ref _afterWrite)?.Invoke(context.Property, context.NewValue);
    }
}
