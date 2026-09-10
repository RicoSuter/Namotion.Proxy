namespace Namotion.Interceptor;

/// <summary>
/// Marks a service as the single authority for <typeparamref name="TContract"/> on the context it
/// is registered with. Registering a second service that also implements this interface for the
/// same contract on the same context throws, even when it is the same instance. A service may
/// implement this interface for several contracts and then reserves all of them.
/// </summary>
/// <typeparam name="TContract">The contract this service exclusively provides.</typeparam>
public interface ISingletonContextService<TContract>
{
}
