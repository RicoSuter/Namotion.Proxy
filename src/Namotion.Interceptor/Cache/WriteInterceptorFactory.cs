using System.Collections.Immutable;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class WriteInterceptorFactory<TProperty>
{
    public static WriteAction<TProperty> Create(ImmutableArray<IWriteInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return static (ref context, innerWriteValue) =>
            {
                // Hoisted: innerWriteValue is opaque to the JIT, so a second read of the property
                // reference could not be folded into the first (PropertyReference advises this).
                var property = context.Property;
                var subject = property.Subject;
                lock (context.Executor.SyncRoot)
                {
                    innerWriteValue(subject, context.NewValue);
                    context.IsWritten = true;
                    // Plain increment, no Interlocked: the enclosing lock and the revision counter live on
                    // the same executor, so the increment is exclusive. The lock protects the SUBJECT's
                    // backing fields, and the executor-belongs-to-subject half of that is only a
                    // construction-site convention, so it is asserted. A mismatched executor would lock
                    // another subject's terminal lock and increment another subject's counter, silently
                    // and undetectably.
                    Debug.Assert(ReferenceEquals(context.Executor.Subject, subject),
                        "The context's executor must own the subject being locked: the terminal lock and the plain increment rely on that pairing.");
                    context.Revision = ++context.Executor.Revision;
                    // Before FinalizeOrigin, which demotes a stamped origin to Local when a hook changed
                    // the value. That demotion is right for publishing and wrong here: the write still
                    // came from the source, and counting it as local would let it discard a local write
                    // that had already committed.
                    var isFromSource = context.Origin.Kind == ChangeOriginKind.FromSource;

                    context.FinalizeOrigin();
                    var raw = context.WriteTimestampRaw;
                    property.SetWriteState(raw > 0 ? raw : 0, context.Revision, isFromSource);
                }
            };
        }

        var chain = new WriteInterceptorChain<TProperty>(
            interceptors,
            static (ref context, innerWriteValue) =>
            {
                var property = context.Property;
                var subject = property.Subject;
                lock (context.Executor.SyncRoot)
                {
                    innerWriteValue(subject, context.NewValue);
                    context.IsWritten = true;
                    // See the zero-interceptor terminal above for why the property is hoisted, why the
                    // increment needs no Interlocked, and what the assert covers that the lock does not.
                    Debug.Assert(ReferenceEquals(context.Executor.Subject, subject),
                        "The context's executor must own the subject being locked: the terminal lock and the plain increment rely on that pairing.");
                    context.Revision = ++context.Executor.Revision;
                    // Before FinalizeOrigin, which demotes a stamped origin to Local when a hook changed
                    // the value. That demotion is right for publishing and wrong here: the write still
                    // came from the source, and counting it as local would let it discard a local write
                    // that had already committed.
                    var isFromSource = context.Origin.Kind == ChangeOriginKind.FromSource;

                    context.FinalizeOrigin();
                    var raw = context.WriteTimestampRaw;
                    property.SetWriteState(raw > 0 ? raw : 0, context.Revision, isFromSource);
                }
                return context.NewValue;
            }
        );
        return chain.Execute;
    }
}