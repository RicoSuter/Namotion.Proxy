namespace Namotion.Interceptor.Tests;

public class ExecutorPublicationTests
{
    [Fact]
    public void WhenContextIsAccessedConcurrently_ThenAllThreadsSeeTheSameExecutor()
    {
        // Arrange
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var subject = new Car();
            var contexts = new IInterceptorSubjectContext[2];
            using var start = new ManualResetEventSlim(false);
            var first = new Thread(() => { start.Wait(); contexts[0] = ((IInterceptorSubject)subject).Context; });
            var second = new Thread(() => { start.Wait(); contexts[1] = ((IInterceptorSubject)subject).Context; });
            first.Start();
            second.Start();

            // Act
            start.Set();
            first.Join();
            second.Join();

            // Assert
            Assert.Same(contexts[0], contexts[1]);
        }
    }
}
