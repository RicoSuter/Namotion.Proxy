using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class SwitchableDerivedSubject : IDisposable
{
    private int _getterCallCount;
    private int _blockNextEvaluation;

    internal ManualResetEventSlim EvaluationEntered { get; } = new(false);
    internal ManualResetEventSlim ContinueEvaluation { get; } = new(false);
    internal int GetterCallCount => Volatile.Read(ref _getterCallCount);
    internal bool UseSecond { get; set; }

    public partial int First { get; set; }
    public partial int Second { get; set; }

    internal void BlockNextEvaluation() => Volatile.Write(ref _blockNextEvaluation, 1);

    [Derived]
    public int Selected
    {
        get
        {
            Interlocked.Increment(ref _getterCallCount);
            if (Interlocked.Exchange(ref _blockNextEvaluation, 0) == 1)
            {
                EvaluationEntered.Set();
                if (!ContinueEvaluation.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the derived getter.");
                }
            }

            return UseSecond ? Second : First;
        }
    }

    public void Dispose()
    {
        EvaluationEntered.Dispose();
        ContinueEvaluation.Dispose();
    }
}
