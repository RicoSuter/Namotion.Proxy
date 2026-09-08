using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[RunsAfter(typeof(DerivedPropertyChangeHandler))]
internal sealed class VetoingWriteInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(
        ref PropertyWriteContext<TProperty> context,
        WriteInterceptionDelegate<TProperty> next)
    {
    }
}
