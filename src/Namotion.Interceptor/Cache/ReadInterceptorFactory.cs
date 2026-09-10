using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return static (ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue) => innerReadValue(context.Property.Subject);
        }

        var chain = new ReadInterceptorChain<TProperty>(
            interceptors,
            static (ref context, innerReadValue) =>
            {
                // The executor threaded through the context belongs to the subject being read, so
                // its SyncRoot is the per-subject terminal lock; see the field's note on the
                // executor.
                lock (context.Executor.SyncRoot)
                {
                    return innerReadValue(context.Property.Subject);
                }
            });
        return chain.Execute;
    }
}
