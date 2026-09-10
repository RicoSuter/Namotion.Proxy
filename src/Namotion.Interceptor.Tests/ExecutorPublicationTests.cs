using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class ExecutorPublicationTests
{
    [Fact]
    public void WhenExecutorIsAccessedConcurrently_ThenAllThreadsSeeTheSameExecutor()
    {
        // Arrange
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var subject = new Car();
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
