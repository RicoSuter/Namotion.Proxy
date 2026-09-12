using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests
{
    [InterceptorSubject]
    public partial class PersonWithBackgroundService : BackgroundService
    {
        public partial string? FirstName { get; set; }

        public partial string? LastName { get; set; }

        public bool WasDisposedByHandler { get; private set; }

        public int StartCount;

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StartCount);
            return base.StartAsync(cancellationToken);
        }

        public override void Dispose()
        {
            WasDisposedByHandler = true;
            base.Dispose();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                FirstName = "John";
                LastName = "Doe";
                
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                FirstName = "Disposed";
            }
        }
    }
}