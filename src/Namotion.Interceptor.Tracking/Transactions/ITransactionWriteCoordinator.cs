using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>Experimental synchronization spanning source writes, local application, and compensation.</summary>
public interface ITransactionWriteCoordinator
{
    /// <summary>Begins a scope that ends after local reconciliation and source compensation.</summary>
    IDisposable BeginCoordination();
}
