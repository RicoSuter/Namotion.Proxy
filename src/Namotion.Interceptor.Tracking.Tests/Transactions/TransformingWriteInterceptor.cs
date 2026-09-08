using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

[RunsBefore(typeof(SubjectTransactionInterceptor))]
internal sealed class TransformingWriteInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(
        ref PropertyWriteContext<TProperty> context,
        WriteInterceptionDelegate<TProperty> next)
    {
        if (context.NewValue is string text)
        {
            context.NewValue = (TProperty)(object)text.ToUpperInvariant();
        }

        next(ref context);
    }
}
