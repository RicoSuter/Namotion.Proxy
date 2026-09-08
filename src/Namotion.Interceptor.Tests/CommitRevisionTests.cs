using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class CommitRevisionTests
{
    [Fact]
    public void WhenPropertiesWrittenThroughChain_ThenContextCarriesDenseIncreasingRevisions()
    {
        // Arrange
        var revisions = new List<long>();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new RevisionCapturingInterceptor(revisions));
        var subject = new OriginProbeSubject(context);

        // Act: distinct values so every write actually commits.
        subject.Name = "first";
        subject.Name = "second";
        subject.Mode = ProbeMode.Running;

        // Assert
        Assert.Equal(new long[] { 1, 2, 3 }, revisions);
    }

    [Fact]
    public void WhenWrittenOnContextWithoutWriteInterceptors_ThenTerminalStillAssignsRevisions()
    {
        // Arrange: no registered write interceptor, so writes take the zero-interceptor terminal,
        // which no capturing interceptor can observe. The executor's counter is read instead.
        var context = InterceptorSubjectContext.Create();
        var written = new OriginProbeSubject(context);
        var untouched = new OriginProbeSubject(context);

        // Act
        written.Name = "first";
        written.Name = "second";
        written.Mode = ProbeMode.Running;

        // Assert: the terminal assigned one revision per committed write, while an unwritten subject
        // sharing the same context stayed at zero, so the counter is per subject.
        Assert.Equal(3, ((InterceptorExecutor)((IInterceptorSubject)written).Context).Revision);
        Assert.Equal(0, ((InterceptorExecutor)((IInterceptorSubject)untouched).Context).Revision);
    }

    /// <summary>
    /// The guard for the invariant everything else rests on. The terminal increments the subject's
    /// counter with a plain <c>++</c>, which is only exclusive because the enclosing lock is that
    /// subject's SyncRoot. Move that one statement outside the lock and concurrent writers share
    /// revisions, the flush merging then picks the wrong survivor, and connectors mirror stale
    /// values. Every other test in this repository still passes with the statement moved out, so
    /// without this one the branch's central claim has no regression coverage at all.
    /// </summary>
    [Fact]
    public void WhenOnePropertyIsWrittenConcurrently_ThenEveryCommitTakesADistinctRevision()
    {
        const int WriterCount = 8;
        const int WritesPerWriter = 4_000;
        const int Rounds = 6;

        for (var round = 0; round < Rounds; round++)
        {
            // Arrange: no equality-check interceptor is registered, so every write commits and every
            // commit must therefore consume a revision of its own.
            var captured = new ConcurrentBag<(long Revision, object? Value)>();
            var context = InterceptorSubjectContext
                .Create()
                .WithService(() => new ConcurrentRevisionCapturingInterceptor(captured));

            var subject = new OriginProbeSubject(context);

            using var start = new ManualResetEventSlim(false);
            var writers = Enumerable
                .Range(0, WriterCount)
                .Select(writer => new Thread(() =>
                {
                    start.Wait();
                    for (var i = 0; i < WritesPerWriter; i++)
                    {
                        subject.Name = $"w{writer}-{i}";
                    }
                }))
                .ToArray();

            foreach (var writer in writers)
            {
                writer.Start();
            }

            // Act
            start.Set();
            foreach (var writer in writers)
            {
                writer.Join();
            }

            // Assert: one revision per committed write, all distinct, none unstamped.
            var all = captured.ToArray();
            Assert.Equal(WriterCount * WritesPerWriter, all.Length);
            Assert.Equal(all.Length, all.Select(entry => entry.Revision).Distinct().Count());
            Assert.DoesNotContain(0L, all.Select(entry => entry.Revision));

            // The highest revision is the write that committed last, because the value store and the
            // increment happen together under the lock, so it has to carry the surviving value.
            var newest = all.MaxBy(entry => entry.Revision);
            Assert.Equal(subject.Name, newest.Value);
        }
    }

    private sealed class RevisionCapturingInterceptor(List<long> revisions) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            revisions.Add(context.Revision);
        }
    }

    private sealed class ConcurrentRevisionCapturingInterceptor(ConcurrentBag<(long Revision, object? Value)> captured) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            captured.Add((context.Revision, context.NewValue));
        }
    }
}
