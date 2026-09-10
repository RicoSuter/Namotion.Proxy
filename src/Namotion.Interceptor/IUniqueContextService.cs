namespace Namotion.Interceptor;

/// <summary>
/// Marks a service as an authority for <typeparamref name="TContract"/>. A resolved context cone
/// may contain at most one distinct instance implementing that authority contract.
/// </summary>
/// <typeparam name="TContract">The authority contract whose cardinality is constrained.</typeparam>
public interface IUniqueContextService<TContract>
{
}
