using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class GeneratedWideWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenTheTerminalLockIsHeld_ThenGeneratedValueSnapshotsWaitWithoutReadInterception(bool captureHook)
    {
        // Arrange
        using var written = new ManualResetEventSlim();
        using var releaseInterceptor = new ManualResetEventSlim();
        var interceptor = new SnapshotObserver(captureHook ? written : null, releaseInterceptor);
        var context = InterceptorSubjectContext.Create();
        context.AddService(interceptor);
        var subject = new GeneratedWideSubject(context);
        var hookCalled = 0;
        subject.Changed = _ => Interlocked.Exchange(ref hookCalled, 1);
        Exception? failure = null;
        var writer = new Thread(() =>
        {
            try { subject.Value = new GeneratedWideValue(1); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        var terminalLock = ((InterceptorExecutor)((IInterceptorSubject)subject).Executor).SyncRoot;
        var snapshotRanWhileLocked = false;
        var blocked = false;

        // Act
        if (captureHook)
        {
            writer.Start();
            Assert.True(written.Wait(TimeSpan.FromSeconds(20)));
        }
        try
        {
            lock (terminalLock)
            {
                if (!captureHook) writer.Start();
                else releaseInterceptor.Set();
                if (captureHook)
                    Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref interceptor.AfterPause) != 0,
                        TimeSpan.FromSeconds(20)));
                blocked = SpinWait.SpinUntil(() => !writer.IsAlive ||
                    (writer.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(20));
                snapshotRanWhileLocked = captureHook
                    ? Volatile.Read(ref hookCalled) != 0
                    : Volatile.Read(ref interceptor.Captured) != 0;
            }
        }
        finally
        {
            releaseInterceptor.Set();
        }
        var completed = writer.Join(TimeSpan.FromSeconds(20));

        // Assert
        Assert.True(blocked);
        Assert.True(completed);
        Assert.Null(failure);
        Assert.False(snapshotRanWhileLocked);
        Assert.Equal(1, interceptor.Captured);
        Assert.Equal(1, hookCalled);
        Assert.Equal(0, interceptor.Reads);
    }

    [Fact]
    public void WhenARawSnapshotBoxesAWideValue_ThenItsCopyUsesTheTerminalLock()
    {
        // Arrange
        var subject = new GeneratedWideSubject(InterceptorSubjectContext.Create());
        var executor = (InterceptorExecutor)((IInterceptorSubject)subject).Executor;
        var copied = 0;
        object? observed = null;
        var reader = new Thread(() => observed = InterceptorExecutor.ReadBackingField<object>(subject, instance =>
        {
            Interlocked.Exchange(ref copied, 1);
            return ((GeneratedWideSubject)instance).ReadRaw();
        })) { IsBackground = true };
        bool blocked;
        int copiedWhileLocked;

        // Act
        lock (executor.SyncRoot)
        {
            reader.Start();
            blocked = SpinWait.SpinUntil(() => !reader.IsAlive ||
                (reader.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(20));
            copiedWhileLocked = Volatile.Read(ref copied);
        }
        var completed = reader.Join(TimeSpan.FromSeconds(20));

        // Assert
        Assert.True(blocked);
        Assert.True(completed);
        Assert.Equal(0, copiedWhileLocked);
        Assert.IsType<GeneratedWideValue>(observed);
    }

    [Fact]
    public void WhenAConstructorSetsAnUnattachedWideValue_ThenNoWriteStateIsPublished()
    {
        // Arrange & Act
        var subject = new ConstructedWideSubject();
        var property = ((IInterceptorSubject)subject).GetPropertyReference(nameof(ConstructedWideSubject.Value));

        // Assert
        Assert.Empty(((IInterceptorSubject)subject).Data);
        Assert.Null(property.TryGetWriteTimestamp());
        Assert.False(property.TryGetWriteState(true, out _, out _));
        Assert.True(subject.Value.IsConsistent);
    }

    [Fact]
    public void WhenGeneratedWideSettersRunConcurrently_ThenOldValuesAndHookArgumentsDoNotTear()
    {
        // Arrange
        var interceptor = new SnapshotObserver(null, null);
        var context = InterceptorSubjectContext.Create();
        context.AddService(interceptor);
        var subject = new GeneratedWideSubject(context);
        var tornHooks = 0;
        subject.Changed = value =>
        {
            if (!value.IsConsistent) Interlocked.Increment(ref tornHooks);
        };
        using var start = new ManualResetEventSlim();
        using var ready = new CountdownEvent(4);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var writers = Enumerable.Range(1, 4).Select(value => new Thread(() =>
        {
            try
            {
                var incoming = new GeneratedWideValue(value);
                ready.Signal();
                start.Wait();
                for (var index = 0; index < 25_000; index++) subject.Value = incoming;
            }
            catch (Exception exception) { failures.Enqueue(exception); }
        }) { IsBackground = true }).ToArray();

        // Act
        foreach (var writer in writers) writer.Start();
        var allReady = ready.Wait(TimeSpan.FromSeconds(20));
        start.Set();
        var completed = writers.Select(writer => writer.Join(TimeSpan.FromSeconds(20))).ToArray();

        // Assert
        Assert.True(allReady);
        Assert.All(completed, Assert.True);
        Assert.Empty(failures);
        Assert.Equal(0, interceptor.TornValues);
        Assert.Equal(0, tornHooks);
        Assert.Equal(0, interceptor.Reads);
    }

    private sealed class SnapshotObserver(ManualResetEventSlim? written, ManualResetEventSlim? releaseInterceptor)
        : IWriteInterceptor, IReadInterceptor
    {
        public int Captured;
        public int AfterPause;
        public int TornValues;
        public int Reads;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref Captured);
            if (context.CurrentValue is GeneratedWideValue value && !value.IsConsistent)
                Interlocked.Increment(ref TornValues);
            next(ref context);
            if (written is not null)
            {
                written.Set();
                Assert.True(releaseInterceptor!.Wait(TimeSpan.FromSeconds(20)));
                Volatile.Write(ref AfterPause, 1);
            }
        }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context,
            ReadInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref Reads);
            return next(ref context);
        }
    }
}

[InterceptorSubject]
public partial class GeneratedWideSubject
{
    public partial GeneratedWideValue Value { get; set; }
    public Action<GeneratedWideValue>? Changed;
    public GeneratedWideValue ReadRaw() => _Value;
    partial void OnValueChanged(GeneratedWideValue newValue) => Changed?.Invoke(newValue);
}

public struct GeneratedWideValue
{
    public long Field0;
    public long Field1;
    public long Field2;
    public long Field3;
    public long Field4;
    public long Field5;
    public long Field6;
    public long Field7;
    public long Field8;
    public long Field9;
    public long Field10;
    public long Field11;
    public long Field12;
    public long Field13;
    public long Field14;
    public long Field15;
    public long Field16;
    public long Field17;
    public long Field18;
    public long Field19;
    public long Field20;
    public long Field21;
    public long Field22;
    public long Field23;
    public long Field24;
    public long Field25;
    public long Field26;
    public long Field27;
    public long Field28;
    public long Field29;
    public long Field30;
    public long Field31;
    public GeneratedWideValue(long value)
    {
        Field0 = value;
        Field1 = value;
        Field2 = value;
        Field3 = value;
        Field4 = value;
        Field5 = value;
        Field6 = value;
        Field7 = value;
        Field8 = value;
        Field9 = value;
        Field10 = value;
        Field11 = value;
        Field12 = value;
        Field13 = value;
        Field14 = value;
        Field15 = value;
        Field16 = value;
        Field17 = value;
        Field18 = value;
        Field19 = value;
        Field20 = value;
        Field21 = value;
        Field22 = value;
        Field23 = value;
        Field24 = value;
        Field25 = value;
        Field26 = value;
        Field27 = value;
        Field28 = value;
        Field29 = value;
        Field30 = value;
        Field31 = value;
    }
    public bool IsConsistent => Field1 == Field0 && Field2 == Field0 && Field3 == Field0 && Field4 == Field0 && Field5 == Field0 && Field6 == Field0 && Field7 == Field0 && Field8 == Field0 && Field9 == Field0 && Field10 == Field0 && Field11 == Field0 && Field12 == Field0 && Field13 == Field0 && Field14 == Field0 && Field15 == Field0 && Field16 == Field0 && Field17 == Field0 && Field18 == Field0 && Field19 == Field0 && Field20 == Field0 && Field21 == Field0 && Field22 == Field0 && Field23 == Field0 && Field24 == Field0 && Field25 == Field0 && Field26 == Field0 && Field27 == Field0 && Field28 == Field0 && Field29 == Field0 && Field30 == Field0 && Field31 == Field0;
}

[InterceptorSubject]
public partial class ConstructedWideSubject
{
    public ConstructedWideSubject() => Value = new GeneratedWideValue(5);
    public partial GeneratedWideValue Value { get; set; }
}
