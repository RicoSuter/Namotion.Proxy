using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Dynamic.Tests;

/// <summary>
/// <see cref="DynamicSubject"/> must publish its executor race-free: a lazy assignment lets two
/// threads racing the first access each build an executor and discard one, along with the per-subject
/// commit revision counter on it.
///
/// It shares the implementation with the generated subjects through
/// <c>InterceptorExecutor.GetOrCreate</c>, so this does not pin a second copy of that logic. What it
/// pins is that this accessor still routes through the shared helper at all, which
/// <c>ExecutorPublicationTests</c> cannot see: that test would keep passing if only this type
/// regressed to a lazy assignment.
/// </summary>
public class DynamicSubjectExecutorPublicationTests
{
    [Fact]
    public void WhenExecutorIsAccessedConcurrently_ThenAllThreadsSeeTheSameExecutor()
    {
        // Arrange: the parameterless constructor is the one that leaves the executor unpublished. The
        // context-taking overload resolves Context while constructing, so it could never race.
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var subject = new DynamicSubject();
            var executors = new IInterceptorExecutor[2];
            using var start = new ManualResetEventSlim(false);
            var first = new Thread(() => { start.Wait(); executors[0] = ((IInterceptorSubject)subject).Executor; });
            var second = new Thread(() => { start.Wait(); executors[1] = ((IInterceptorSubject)subject).Executor; });
            first.Start();
            second.Start();

            // Act
            start.Set();
            first.Join();
            second.Join();

            // Assert
            Assert.Same(executors[0], executors[1]);
        }
    }
}
