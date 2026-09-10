using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Context;

public class ContextConcurrencyTests
{
    private const int Attempts = 200;
    private const int Mutations = 50;

    /// <summary>
    /// Registering services publishes a fresh state while attached subjects resolve compiled
    /// chains from the previous one; neither side may block the other, and once the writes
    /// settle a write must reach exactly the final interceptor set (quiescent consistency, so a
    /// chain compiled from a pre-mutation state cannot survive the mutation).
    /// </summary>
    [Theory]
    [InlineData(nameof(IInterceptorSubjectContext.AddService))]
    [InlineData(nameof(IInterceptorSubjectContext.TryAddService))]
    public async Task WhenContextIsMutatedWhileSubjectIsWritten_ThenNoDeadlockOccursAndTheFinalChainIsComplete(string mutation)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var subjectContext = InterceptorSubjectContext.Create();

            var car = new Car(subjectContext);
            using var start = new ManualResetEventSlim(false);

            var writer = Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var index = 0; index < 2_000; index++)
                {
                    car.Speed = index;
                }
            }, TaskCreationOptions.LongRunning);

            var mutator = Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var index = 0; index < Mutations; index++)
                {
                    switch (mutation)
                    {
                        case nameof(IInterceptorSubjectContext.AddService):
                            subjectContext.AddService<IWriteInterceptor>(new CountingWriteInterceptor());
                            break;

                        case nameof(IInterceptorSubjectContext.TryAddService):
                            subjectContext.TryAddService<IWriteInterceptor>(() => new CountingWriteInterceptor(), _ => false);
                            break;

                        default:
                            throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation.");
                    }
                }
            }, TaskCreationOptions.LongRunning);

            // Act
            start.Set();
            var both = Task.WhenAll(writer, mutator);
            try
            {
                await both.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Deadlock on attempt {attempt} of {Attempts}: writing a property and calling {mutation} " +
                    "on the subject's context blocked each other.",
                    exception);
            }

            // Assert: one settled write runs every registered interceptor exactly once.
            var interceptors = new List<CountingWriteInterceptor>();
            foreach (var interceptor in subjectContext.GetServices<IWriteInterceptor>())
            {
                interceptors.Add((CountingWriteInterceptor)interceptor);
            }

            Assert.Equal(Mutations, interceptors.Count);

            var countsBefore = interceptors.ConvertAll(interceptor => interceptor.WriteCount);
            car.Speed = -1;
            for (var index = 0; index < interceptors.Count; index++)
            {
                Assert.Equal(countsBefore[index] + 1, interceptors[index].WriteCount);
            }
        }
    }

    private sealed class CountingWriteInterceptor : IWriteInterceptor
    {
        private int _writeCount;

        internal int WriteCount => Volatile.Read(ref _writeCount);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref _writeCount);
            next(ref context);
        }
    }

    /// <summary>
    /// Pins the guarantee TryAddService actually makes: the exists check and the add are atomic
    /// against another mutator of the SAME context, because both serialize on its mutation lock.
    /// Two callers therefore have exactly one winner.
    /// </summary>
    [Fact]
    public async Task WhenTwoThreadsTryAddTheSameService_ThenOnlyOneSucceeds()
    {
        // Arrange
        const int concurrentAttempts = 3_000;
        var violations = 0;

        for (var attempt = 1; attempt <= concurrentAttempts; attempt++)
        {
            var context = InterceptorSubjectContext.Create();

            using var start = new Barrier(2);
            var results = new bool[2];

            var adders = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[0] = context.TryAddService(() => new MarkerService(), _ => true);
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[1] = context.TryAddService(() => new MarkerService(), _ => true);
                }, TaskCreationOptions.LongRunning)
            };

            // Act: bounded so that a deadlock regression fails the run instead of hanging it
            // without a message. Awaited rather than polled, because this runs thousands of times
            // and a poll interval would dominate the whole test.
            try
            {
                await Task.WhenAll(adders).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Two concurrent TryAddService calls deadlocked on attempt {attempt} of {concurrentAttempts}.",
                    exception);
            }

            // Assert
            if (results[0] == results[1])
            {
                violations++;
            }
        }

        Assert.True(violations == 0,
            $"TryAddService was not atomic in {violations} of {concurrentAttempts} attempts: two concurrent " +
            "calls for the same service type must have exactly one winner.");
    }

    [Fact]
    public async Task WhenServiceFactoryRegistersIntoSameContext_ThenNoDeadlockOccurs()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var added = false;

        // Act: the factory reenters the mutation lock of the context it is registered into.
        var work = Task.Run(() =>
        {
            added = context.TryAddService(
                () =>
                {
                    context.AddService(new OtherMarkerService());
                    return new MarkerService();
                },
                _ => true);
        });

        await AsyncTestHelpers.WaitUntilAsync(() => work.IsCompleted,
            message: "TryAddService deadlocked while its factory mutated the same context");
        await work;

        // Assert
        Assert.True(added);
        Assert.Single(context.GetServices<MarkerService>());
        Assert.Single(context.GetServices<OtherMarkerService>());
    }

    [Fact]
    public async Task WhenServiceExistsPredicateRegistersIntoSameContext_ThenNoDeadlockOccurs()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new MarkerService());
        var predicateRan = false;
        var added = false;

        // Act: the predicate reenters the mutation lock of the context it is querying.
        var work = Task.Run(() =>
        {
            added = context.TryAddService(
                () => new MarkerService(),
                _ =>
                {
                    predicateRan = true;
                    context.AddService(new OtherMarkerService());
                    return false;
                });
        });

        await AsyncTestHelpers.WaitUntilAsync(() => work.IsCompleted,
            message: "TryAddService deadlocked while its exists predicate mutated the same context");
        await work;

        // Assert
        Assert.True(predicateRan);
        Assert.True(added);
        Assert.Equal(2, context.GetServices<MarkerService>().Length);
        Assert.Single(context.GetServices<OtherMarkerService>());
    }

    [Fact]
    public async Task WhenServicesAreAddedConcurrentlyWithQueries_ThenQuiescentStateSeesAllServices()
    {
        // Arrange
        const int writerCount = 4;
        const int readerCount = 4;
        const int servicesPerWriter = 50;

        var context = InterceptorSubjectContext.Create();
        using var start = new ManualResetEventSlim(false);
        var activeWriters = writerCount;

        var writers = Enumerable.Range(0, writerCount)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var index = 0; index < servicesPerWriter; index++)
                {
                    context.AddService(new MarkerService());
                }

                Interlocked.Decrement(ref activeWriters);
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        var readers = Enumerable.Range(0, readerCount)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.Wait();

                // Keeps refilling the service cache while writers publish fresh states, so that a
                // cache entry surviving a mutation would poison the final query below.
                while (Volatile.Read(ref activeWriters) != 0)
                {
                    context.GetServices<MarkerService>();
                }
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        var workers = writers.Concat(readers).ToArray();

        // Act
        start.Set();
        await AsyncTestHelpers.WaitUntilAsync(() => workers.All(worker => worker.IsCompleted),
            message: "Concurrent service additions and queries did not finish");
        await Task.WhenAll(workers);

        // Assert
        Assert.Equal(writerCount * servicesPerWriter, context.GetServices<MarkerService>().Length);
    }

    private sealed class MarkerService;

    private sealed class OtherMarkerService;
}
