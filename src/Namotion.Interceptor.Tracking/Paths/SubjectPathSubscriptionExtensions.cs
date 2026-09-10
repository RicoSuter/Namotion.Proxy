using System;
using System.Linq.Expressions;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>Entry points for subscribing to a decomposed path from a subject, for example <c>subject.SubscribeToPath(x => x.Child.Children[2].Name, callback, SubjectPathValidation.Full)</c>.</summary>
public static class SubjectPathSubscriptionExtensions
{
    /// <summary>
    /// Subscribes to the value of <paramref name="path"/> from <paramref name="subject"/>. The returned
    /// handle exposes the path's current value via <see cref="SubjectPathSubscription{TValue}.Current"/>
    /// and must be disposed to release the subscription. <paramref name="validation"/> chooses how much
    /// of the path a leaf write revalidates; see <see cref="SubjectPathValidation"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="subject"/>, <paramref name="path"/>, or <paramref name="callback"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a supported path expression (for example a cast, a field selector, a captured-object chain, a multi-argument indexer, the identity path, or a path ending in an index). A segment that is merely invalid at runtime (missing, or non-intercepted and non-derived) does not throw; the path resolves as unresolved instead.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="validation"/> is not a defined <see cref="SubjectPathValidation"/> value.</exception>
    /// <exception cref="InvalidOperationException">Called while an active, not-yet-committing transaction is on the current flow.</exception>
    public static SubjectPathSubscription<TValue> SubscribeToPath<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> path,
        SubjectPathChangeCallback<TValue> callback,
        SubjectPathValidation validation)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(callback);

        return Subscribe(subject, path, new DelegateObserver<TValue>(callback), validation);
    }

    /// <summary>Observer overload of <see cref="SubscribeToPath{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, SubjectPathChangeCallback{TValue}, SubjectPathValidation)"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="subject"/>, <paramref name="path"/>, or <paramref name="observer"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a supported path expression. A segment that is merely invalid at runtime does not throw; the path resolves as unresolved instead.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="validation"/> is not a defined <see cref="SubjectPathValidation"/> value.</exception>
    /// <exception cref="InvalidOperationException">Called while an active, not-yet-committing transaction is on the current flow.</exception>
    public static SubjectPathSubscription<TValue> SubscribeToPath<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> path,
        ISubjectPathChangeObserver<TValue> observer,
        SubjectPathValidation validation)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(observer);

        return Subscribe(subject, path, observer, validation);
    }

    private static SubjectPathSubscription<TValue> Subscribe<TSubject, TValue>(
        TSubject subject,
        Expression<Func<TSubject, TValue>> path,
        ISubjectPathChangeObserver<TValue> observer,
        SubjectPathValidation validation)
        where TSubject : IInterceptorSubject
    {
        if (validation is not (SubjectPathValidation.Full or SubjectPathValidation.LeafOnly))
        {
            throw new ArgumentOutOfRangeException(nameof(validation), validation,
                "Validation must be a defined SubjectPathValidation value (Full or LeafOnly).");
        }

        // Installing a chain from inside an active, not-yet-committing transaction would resolve its
        // intermediates against the transaction's speculative staged subjects and install listeners on them;
        // a rollback would then strand those listeners on subjects that never became part of the committed
        // graph. Refuse rather than leak: subscribe before the transaction, or after it commits.
        if (SubjectTransaction.Current is { IsCommitting: false })
        {
            throw new InvalidOperationException(
                "SubscribeToPath cannot be called inside an active transaction: the listener chain would bind to " +
                "the transaction's speculative staged subjects, which a rollback would strand. Subscribe before " +
                "starting the transaction or after it commits.");
        }

        var segments = PathExpressionDecomposer.Decompose(path);
        return new SubjectPathSubscription<TValue>(subject, segments, observer, validation);
    }

    /// <summary>Wraps a <see cref="SubjectPathChangeCallback{TValue}"/> so the callback and observer entry points share one code path.</summary>
    private sealed class DelegateObserver<TValue>(SubjectPathChangeCallback<TValue> callback) : ISubjectPathChangeObserver<TValue>
    {
        public void OnChange(in SubjectPathChange<TValue> change) => callback(in change);
    }
}
