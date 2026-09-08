using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class TransactionCascadeSubject
{
    public partial string? Plain { get; set; }

    public partial string? SideEffect { get; set; }

    public partial string? Failing { get; set; }

    private bool _throwOnFailingWrite;

    internal bool ThrowOnFailingWrite
    {
        get => Volatile.Read(ref _throwOnFailingWrite);
        set => Volatile.Write(ref _throwOnFailingWrite, value);
    }

    partial void OnFailingChanging(ref string? newValue, ref bool cancel)
    {
        if (ThrowOnFailingWrite)
        {
            throw new InvalidOperationException("Setter failed.");
        }
    }

    [Derived]
    public partial string? DerivedWithSetter { get; set; }

    [Derived]
    public string Combined => $"{Plain}|{DerivedWithSetter}";

    [Derived]
    public string CombinedAgain => $"[{Combined}]";

    [Derived]
    public string? Independent => DerivedWithSetter;

    internal string? ExternalSuffix { get; set; }

    [Derived]
    public string ManualCombined => $"{Plain}|{ExternalSuffix}";

    internal Func<TransactionCascadeSubject, string?>? ProbeEvaluator { get; set; }

    [Derived]
    public string? Probe => ProbeEvaluator?.Invoke(this) ?? Plain;
}
