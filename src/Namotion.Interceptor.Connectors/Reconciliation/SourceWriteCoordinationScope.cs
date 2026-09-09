namespace Namotion.Interceptor.Connectors.Reconciliation;

internal sealed class SourceWriteCoordinationScope : IDisposable
{
    private static readonly AsyncLocal<SourceWriteCoordinationScope?> Ambient = new();
    private readonly SourceWriteCoordinationScope? _previous = Ambient.Value;
    private readonly List<IDisposable> _operations = [];

    public SourceWriteCoordinationScope() => Ambient.Value = this;

    public static IDisposable? RetainOrReturn(IDisposable? operation)
    {
        if (operation is null || Ambient.Value is not { } scope) return operation;
        lock (scope._operations) scope._operations.Add(operation);
        return null;
    }

    public void Dispose()
    {
        Ambient.Value = _previous;
        foreach (var operation in _operations) operation.Dispose();
    }
}
