namespace Namotion.Interceptor;

/// <summary>
/// The attempted stage of the origin lifecycle: an origin claim paired with the value evidence it
/// was stamped with, carried unverified through the write until the terminal write finalizes the
/// origin (verifies it or demotes it to Local).
/// </summary>
internal readonly struct AttemptedOrigin
{
    /// <summary>The origin claimed for the write.</summary>
    public readonly ChangeOrigin Origin;

    /// <summary>The value the source sent, valid when <see cref="Origin"/> is stamped.</summary>
    public readonly object? SentValue;
    internal readonly IPropertyWriteGuard? Guard;

    public AttemptedOrigin(ChangeOrigin origin, object? sentValue, IPropertyWriteGuard? guard = null)
    {
        Origin = origin;
        SentValue = sentValue;
        Guard = guard;
    }
}
