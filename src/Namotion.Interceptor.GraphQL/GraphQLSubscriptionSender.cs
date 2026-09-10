using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.GraphQL
{
    public class GraphQLSubscriptionSender<TSubject> : BackgroundService
        where TSubject : IInterceptorSubject
    {
        private readonly TSubject _subject;
        private readonly ITopicEventSender _sender;

        public GraphQLSubscriptionSender(TSubject subject, ITopicEventSender sender)
        {
            _subject = subject;
            _sender = sender;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var changes in _subject
                .GetContext()
                .GetPropertyChangeObservable()
                .ToAsyncEnumerable()
                .WithCancellation(stoppingToken))
            {
                // TODO: Send only changes
                await _sender.SendAsync(nameof(Subscription<TSubject>.Root), _subject, stoppingToken);
            }
        }
    }
}