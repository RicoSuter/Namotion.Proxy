using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class WideValueReadTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void WhenAnAttachedWideValueWriteIsInProgress_ThenTheReadCannotTear(bool addReadInterceptor, int readShape)
    {
        // Arrange
        using var partialWrite = new ManualResetEventSlim();
        using var finishWrite = new ManualResetEventSlim();
        using var readCompleted = new ManualResetEventSlim();
        var context = InterceptorSubjectContext.Create();
        if (addReadInterceptor)
        {
            context.AddService(new ForwardingReadInterceptor());
        }
        var subject = new WideSubject();
        subject.AttachToContext(context);
        var expected = new WideValue { First = 1, Second = 2 };
        WideValue observed = default;
        Exception? writeFailure = null;
        Exception? readFailure = null;
        var writer = new Thread(() =>
        {
            try { subject.WriteInParts(expected, partialWrite, finishWrite); }
            catch (Exception exception) { writeFailure = exception; }
        }) { IsBackground = true };
        var reader = new Thread(() =>
        {
            try
            {
                observed = readShape switch
                {
                    1 => (WideValue)subject.BoxedValue,
                    2 => (WideValue)subject.BoxedValueType,
                    3 => (WideValue)subject.BoxedInterface,
                    _ => subject.Value
                };
            }
            catch (Exception exception) { readFailure = exception; }
            finally { readCompleted.Set(); }
        }) { IsBackground = true };

        // Act
        writer.Start();
        bool reachedPartialWrite;
        bool reachedRead;
        try
        {
            reachedPartialWrite = partialWrite.Wait(TimeSpan.FromSeconds(20));
            reader.Start();
            reachedRead = SpinWait.SpinUntil(
                () => readCompleted.IsSet || (reader.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(20));
        }
        finally
        {
            finishWrite.Set();
        }
        var writerCompleted = writer.Join(TimeSpan.FromSeconds(20));
        var readerCompleted = reader.Join(TimeSpan.FromSeconds(20));

        // Assert
        Assert.True(reachedPartialWrite);
        Assert.True(reachedRead);
        Assert.True(writerCompleted);
        Assert.True(readerCompleted);
        Assert.Null(writeFailure);
        Assert.Null(readFailure);
        Assert.Equal(expected.First, observed.First);
        Assert.Equal(expected.Second, observed.Second);
    }

    [Fact]
    public void WhenAtomicValuesAreReadWithoutInterceptors_ThenTheTerminalWriteLockIsNotRequired()
    {
        VerifyReadDoesNotWait(42);
        VerifyReadDoesNotWait("value");
        VerifyReadDoesNotWait(new object());
        if (IntPtr.Size == 8)
        {
            VerifyReadDoesNotWait(42L);
            VerifyReadDoesNotWait(42.5);
        }
    }

    private static void VerifyReadDoesNotWait<TValue>(TValue expected)
    {
        // Arrange
        using var valueStored = new ManualResetEventSlim();
        using var finishWrite = new ManualResetEventSlim();
        using var readCompleted = new ManualResetEventSlim();
        var subject = new AtomicSubject<TValue>();
        subject.AttachToContext(InterceptorSubjectContext.Create());
        TValue? observed = default;
        Exception? writeFailure = null;
        Exception? readFailure = null;
        var writer = new Thread(() =>
        {
            try { subject.WriteAndPause(expected, valueStored, finishWrite); }
            catch (Exception exception) { writeFailure = exception; }
        }) { IsBackground = true };
        var reader = new Thread(() =>
        {
            try { observed = subject.Value; }
            catch (Exception exception) { readFailure = exception; }
            finally { readCompleted.Set(); }
        }) { IsBackground = true };

        // Act
        writer.Start();
        bool reachedWrite;
        bool reachedRead;
        bool readBeforeWriteReleased;
        try
        {
            reachedWrite = valueStored.Wait(TimeSpan.FromSeconds(20));
            reader.Start();
            reachedRead = SpinWait.SpinUntil(
                () => readCompleted.IsSet || (reader.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(20));
            readBeforeWriteReleased = readCompleted.IsSet;
        }
        finally
        {
            finishWrite.Set();
        }
        var writerCompleted = writer.Join(TimeSpan.FromSeconds(20));
        var readerCompleted = reader.Join(TimeSpan.FromSeconds(20));

        // Assert
        Assert.True(reachedWrite);
        Assert.True(reachedRead);
        Assert.True(writerCompleted);
        Assert.True(readerCompleted);
        Assert.Null(writeFailure);
        Assert.Null(readFailure);
        Assert.True(readBeforeWriteReleased);
        Assert.Equal(expected, observed);
    }

    private sealed class AtomicSubject<TValue> : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private TValue _value = default!;
        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Value)] = new(nameof(Value), typeof(TValue), [],
                subject => ((AtomicSubject<TValue>)subject)._value, null, isIntercepted: true, isDynamic: false)
        };
        public TValue Value => Executor.GetPropertyValue(nameof(Value), subject => ((AtomicSubject<TValue>)subject)._value);
        public void WriteAndPause(TValue value, ManualResetEventSlim valueStored, ManualResetEventSlim finishWrite) =>
            Executor.SetPropertyValue(nameof(Value), value, _value, (subject, incoming) =>
            {
                ((AtomicSubject<TValue>)subject)._value = incoming;
                valueStored.Set();
                Assert.True(finishWrite.Wait(TimeSpan.FromSeconds(20)));
            });
        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
    }

    private sealed class ForwardingReadInterceptor : IReadInterceptor
    {
        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next) => next(ref context);
    }

    private interface IWideValue;

    private struct WideValue : IWideValue
    {
        public long First;
        public long Second;
    }

    private sealed class WideSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private WideValue _value;
        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Value)] = new(nameof(Value), typeof(WideValue), [],
                subject => ((WideSubject)subject)._value, null, isIntercepted: true, isDynamic: false)
        };

        public ValueType BoxedValueType => Executor.GetPropertyValue<ValueType>(nameof(Value), subject => ((WideSubject)subject)._value);
        public IWideValue BoxedInterface => Executor.GetPropertyValue<IWideValue>(nameof(Value), subject => ((WideSubject)subject)._value);
        public object BoxedValue => Executor.GetPropertyValue<object>(nameof(Value), subject => ((WideSubject)subject)._value);

        public WideValue Value => Executor.GetPropertyValue(nameof(Value), subject => ((WideSubject)subject)._value);

        public void WriteInParts(WideValue value, ManualResetEventSlim partialWrite, ManualResetEventSlim finishWrite) =>
            Executor.SetPropertyValue(nameof(Value), value, _value, (subject, incoming) =>
            {
                var target = (WideSubject)subject;
                target._value.First = incoming.First;
                partialWrite.Set();
                Assert.True(finishWrite.Wait(TimeSpan.FromSeconds(20)));
                target._value.Second = incoming.Second;
            });

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
    }
}
