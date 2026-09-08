using System.Collections.Immutable;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ExceptionalWriteReconciliationTests
{
    [Fact]
    public void WhenADownstreamInterceptorThrowsAfterCommit_ThenOwnershipMatchesStorageBeforeTheExceptionUnwinds()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var oldChild = new Person();
        var newChild = new Person();
        var parent = new Person(context) { Father = oldChild };
        var failure = new InvalidOperationException("downstream failure");
        var observer = new UnwindObserver(newChild);
        context.WithService(() => observer);
        context.WithService(() => new ThrowingWriteInterceptor(failure));

        // Act
        var exception = Record.Exception(() => parent.Father = newChild);

        // Assert
        Assert.Same(failure, exception);
        Assert.Contains(nameof(ThrowingWriteInterceptor.WriteProperty), exception!.StackTrace);
        Assert.Same(newChild, parent.Father);
        Assert.Same(context, observer.ContextOnUnwind);
        Assert.True(observer.IsWrittenOnUnwind);
        Assert.Same(context, ((IInterceptorSubject)newChild).TryGetContext());
        Assert.Equal(new PropertyReference(parent, nameof(Person.Father)),
            Assert.Single(((IInterceptorSubject)newChild).GetParents()).Property);
        Assert.Null(((IInterceptorSubject)oldChild).TryGetContext());
        Assert.Empty(((IInterceptorSubject)oldChild).GetParents());
    }

    [Fact]
    public void WhenANestedWriteCommitsBeforeTheOuterWriteThrows_ThenOwnershipMatchesTheLatestStoredValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var oldChild = new Person();
        var intermediateChild = new Person();
        var latestChild = new Person();
        var parent = new Person(context) { Father = oldChild };
        var failure = new InvalidOperationException("outer write failure");
        context.WithService(() => new NestedWriteThenThrowInterceptor(parent, latestChild, failure));

        // Act
        var exception = Record.Exception(() => parent.Father = intermediateChild);

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(latestChild, parent.Father);
        Assert.Same(context, ((IInterceptorSubject)latestChild).TryGetContext());
        Assert.Equal(new PropertyReference(parent, nameof(Person.Father)),
            Assert.Single(((IInterceptorSubject)latestChild).GetParents()).Property);
        Assert.Null(((IInterceptorSubject)oldChild).TryGetContext());
        Assert.Empty(((IInterceptorSubject)oldChild).GetParents());
        Assert.Null(((IInterceptorSubject)intermediateChild).TryGetContext());
        Assert.Empty(((IInterceptorSubject)intermediateChild).GetParents());
    }

    [Fact]
    public void WhenAnImmutableArrayWriteThrowsAfterCommit_ThenEveryStoredOccurrenceIsOwned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var oldTire = new Tire();
        var newTire = new Tire();
        var garage = new Garage(context) { SpareTires = [oldTire] };
        ImmutableArray<Tire> newValue = [newTire, newTire];
        var failure = new InvalidOperationException("downstream failure");
        context.WithService(() => new ThrowingWriteInterceptor(failure));

        // Act
        var exception = Record.Exception(() => garage.SpareTires = newValue);

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(newValue, garage.SpareTires);
        Assert.Same(context, ((IInterceptorSubject)newTire).TryGetContext());
        Assert.Equal(2, ((IInterceptorSubject)newTire).GetParents().Length);
        Assert.Null(((IInterceptorSubject)oldTire).TryGetContext());
    }

    [Fact]
    public void WhenADownstreamInterceptorThrowsBeforeCommit_ThenTheProposedClaimIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var oldChild = new Person();
        var proposedChild = new Person();
        var parent = new Person(context) { Father = oldChild };
        var failure = new InvalidOperationException("downstream failure");
        context.WithService(() => new ThrowingWriteInterceptor(failure, beforeCommit: true));

        // Act
        var exception = Record.Exception(() => parent.Father = proposedChild);

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(oldChild, parent.Father);
        Assert.Same(context, ((IInterceptorSubject)oldChild).TryGetContext());
        Assert.Null(((IInterceptorSubject)proposedChild).TryGetContext());
        Assert.Empty(((IInterceptorSubject)proposedChild).GetParents());
    }

    [Fact]
    public void WhenAPropertyChangeObserverThrows_ThenTheReconciledOwnershipRemainsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var oldChild = new Person();
        var newChild = new Person();
        var parent = new Person(context) { Father = oldChild };
        var failure = new InvalidOperationException("observer failure");
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(_ => throw failure);

        // Act
        var exception = Record.Exception(() => parent.Father = newChild);

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(newChild, parent.Father);
        Assert.Same(context, ((IInterceptorSubject)newChild).TryGetContext());
        Assert.Single(((IInterceptorSubject)newChild).GetParents());
        Assert.Null(((IInterceptorSubject)oldChild).TryGetContext());
    }

    [Fact]
    public void WhenReconciliationAlsoThrows_ThenBothExceptionsArePreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var parent = new Person(context);
        var child = new Person();
        var writeFailure = new InvalidOperationException("downstream failure");
        var callbackFailure = new InvalidOperationException("callback contract violation");
        context.WithService(() => new ThrowingWriteInterceptor(writeFailure));
        context.WithService(() => new DelegateLifecycleHandler(change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                throw callbackFailure;
            }
        }));

        // Act
        var exception = Record.Exception(() => parent.Father = child);

        // Assert
        var aggregate = Assert.IsType<AggregateException>(exception);
        Assert.Collection(aggregate.InnerExceptions,
            failure => Assert.Same(writeFailure, failure),
            failure => Assert.Same(callbackFailure, failure));
        Assert.Contains(nameof(ThrowingWriteInterceptor.WriteProperty), writeFailure.StackTrace);
        Assert.NotNull(callbackFailure.StackTrace);
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class NestedWriteThenThrowInterceptor(Person parent, Person latestChild, Exception failure) : IWriteInterceptor
    {
        private bool _hasReentered;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (!_hasReentered && ReferenceEquals(context.Property.Subject, parent) &&
                context.Property.Name == nameof(Person.Father))
            {
                _hasReentered = true;
                parent.Father = latestChild;
                throw failure;
            }
        }
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class ThrowingWriteInterceptor(Exception failure, bool beforeCommit = false) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (!beforeCommit)
            {
                next(ref context);
            }

            throw failure;
        }
    }

    [RunsBefore(typeof(LifecycleInterceptor))]
    private sealed class UnwindObserver(IInterceptorSubject child) : IWriteInterceptor
    {
        public IInterceptorSubjectContext? ContextOnUnwind { get; private set; }

        public bool IsWrittenOnUnwind { get; private set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            try
            {
                next(ref context);
            }
            finally
            {
                ContextOnUnwind = child.TryGetContext();
                IsWrittenOnUnwind = context.IsWritten;
            }
        }
    }
}
