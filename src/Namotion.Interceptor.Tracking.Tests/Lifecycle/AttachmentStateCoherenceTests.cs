using System.Diagnostics;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The attached context and the anchor are one state: a non-None anchor means the subject is
/// anchored to the context it is attached to, so a non-None anchor without a context is a state
/// that never existed. Both are read lock-free, so publishing them as separate stores lets a
/// reader land between them and observe exactly that.
/// </summary>
public class AttachmentStateCoherenceTests
{
    private static readonly TimeSpan HammerDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reproduces the finding that the attachment context and the anchor are not one coherent
    /// state. Reproduces naturally: nothing holds a window open, the reader simply observes the
    /// pair while a second thread transitions it. The window is two adjacent stores wide, so this
    /// hammers for a bounded time rather than a fixed iteration count.
    /// </summary>
    /// <remarks>
    /// The reader brackets the pair with the attachment revision and judges only the observations
    /// where the revision did not move. Without that bracket the two reads are a time-of-check
    /// race that no publication scheme can win: a whole detach can commit between them, and the
    /// reader then reports two accurate readings of two different committed states. Bracketing
    /// discards exactly those and leaves the observations that came from a single unchanged
    /// revision, where a disagreeing pair can only be a torn publication.
    /// </remarks>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAttachmentIsTransitionedConcurrently_ThenTheAnchorAndTheContextAreNeverPublishedApart()
    {
        // Arrange: a lifecycle-free context, so a detach is exactly the pair of stores under test
        // with no topology work in between.
        var context = InterceptorSubjectContext.Create();
        var subject = (IInterceptorSubject)new Person { FirstName = "S" };
        var executor = subject.Executor;

        var stop = 0;
        var tornObservations = 0;
        var disagreeingObservations = 0;
        var transitions = 0;
        var reads = 0;

        var reader = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                var revisionBefore = executor.AttachmentRevision;
                var anchor = executor.AttachmentAnchor;
                var attachedContext = executor.AttachedContext;
                var revisionAfter = executor.AttachmentRevision;

                if (anchor != SubjectAttachmentAnchorKind.None && attachedContext is null)
                {
                    Interlocked.Increment(ref disagreeingObservations);
                    if (revisionBefore == revisionAfter)
                    {
                        Interlocked.Increment(ref tornObservations);
                    }
                }

                Interlocked.Increment(ref reads);
            }
        })
        {
            IsBackground = true
        };

        var transitioner = new Thread(() =>
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < HammerDuration && Volatile.Read(ref tornObservations) == 0)
            {
                subject.AttachToContext(context);
                subject.DetachFromContext(context);
                Interlocked.Increment(ref transitions);
            }

            Volatile.Write(ref stop, 1);
        })
        {
            IsBackground = true
        };

        // Act
        reader.Start();
        transitioner.Start();
        var transitionerCompleted = transitioner.Join(HammerDuration + TimeSpan.FromSeconds(20));
        Volatile.Write(ref stop, 1);
        var readerCompleted = reader.Join(TimeSpan.FromSeconds(20));

        // Assert: both threads did real work, so a green result cannot come from an idle run.
        Assert.True(transitionerCompleted && readerCompleted, "a hammer thread never finished");
        Assert.True(Volatile.Read(ref transitions) > 0, "no attachment transition ran");
        Assert.True(Volatile.Read(ref reads) > 0, "no attachment read ran");

        // A non-None anchor within one unchanged revision implies an attached context.
        Assert.True(Volatile.Read(ref tornObservations) == 0,
            $"observed {Volatile.Read(ref tornObservations)} attachment states with a non-None anchor " +
            $"and no context inside one unchanged revision (of {Volatile.Read(ref disagreeingObservations)} " +
            $"disagreeing pairs overall) in {Volatile.Read(ref transitions)} transitions and " +
            $"{Volatile.Read(ref reads)} reads: the context and the anchor are published as two separate " +
            "stores, so a lock-free reader can land between them");
    }
}
