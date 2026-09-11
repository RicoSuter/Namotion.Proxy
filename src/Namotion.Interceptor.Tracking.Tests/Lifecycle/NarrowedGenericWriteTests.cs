using System.Collections.Concurrent;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class NarrowedGenericWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAScalarGenericWriteReplacesAStructuralValue_ThenTheOldChildIsReleased(bool useValueType)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var subject = new ObjectSubject();
        subject.AttachToContext(context);
        var child = new ObjectSubject();
        subject.Value = child;

        // Act
        if (useValueType) subject.SetNarrowed(42);
        else subject.SetNarrowed("replacement");

        // Assert
        Assert.Equal(useValueType ? 42 : (object)"replacement", subject.Value);
        Assert.Null(child.TryGetContext());
        Assert.Empty(child.GetParents());
        Assert.Equal(0, child.GetReferenceCount());
        subject.DetachFromContext(context);
        Assert.Null(subject.TryGetContext());
    }

    [Fact]
    public void WhenAnotherThreadHoldsTheTopologyGate_ThenANarrowedWriteWaitsBeforeRunningInterceptors()
    {
        // Arrange
        using var interceptorEntered = new ManualResetEventSlim();
        using var writeCompleted = new ManualResetEventSlim();
        var context = InterceptorSubjectContext.Create().WithLifecycle()
            .WithService(() => new EntryObserver(interceptorEntered));
        var subject = new ObjectSubject();
        subject.AttachToContext(context);
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        Exception? writeFailure = null;
        var writer = new Thread(() =>
        {
            try { subject.SetNarrowed("replacement"); }
            catch (Exception exception) { writeFailure = exception; }
            finally { writeCompleted.Set(); }
        }) { IsBackground = true };

        // Act
        lifecycle.EnterStructuralWriteGate();
        bool reachedWait;
        bool enteredBeforeRelease;
        try
        {
            writer.Start();
            reachedWait = SpinWait.SpinUntil(
                () => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0 || writeCompleted.IsSet,
                WriteProtocolAcceptance.RendezvousTimeout);
            enteredBeforeRelease = interceptorEntered.IsSet;
        }
        finally
        {
            lifecycle.ExitStructuralWriteGate();
        }
        var completed = writer.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert
        Assert.True(reachedWait);
        Assert.True(completed);
        Assert.False(enteredBeforeRelease);
        Assert.True(interceptorEntered.IsSet);
        Assert.Null(writeFailure);
        Assert.Equal("replacement", subject.Value);
        subject.DetachFromContext(context);
    }

    [RunsBefore(typeof(LifecycleInterceptor))]
    private sealed class EntryObserver(ManualResetEventSlim entered) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            entered.Set();
            next(ref context);
        }
    }

    private sealed class ObjectSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private object? _value;
        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Value)] = new(nameof(Value), typeof(object), [],
                subject => ((ObjectSubject)subject)._value,
                (subject, value) => ((ObjectSubject)subject).Value = value,
                isIntercepted: true, isDynamic: false)
        };

        public object? Value
        {
            get => Executor.GetPropertyValue(nameof(Value), subject => ((ObjectSubject)subject)._value);
            set => Executor.SetPropertyValue(nameof(Value), value, _value,
                (subject, incoming) => ((ObjectSubject)subject)._value = incoming);
        }

        public void SetNarrowed<TProperty>(TProperty value) => Executor.SetPropertyValue(nameof(Value), value,
            _value is TProperty current ? current : default!,
            (subject, incoming) => ((ObjectSubject)subject)._value = incoming);

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
    }
}
