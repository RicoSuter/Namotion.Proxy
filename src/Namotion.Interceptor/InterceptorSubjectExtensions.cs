namespace Namotion.Interceptor;

public static class InterceptorSubjectExtensions
{
    public static void SetData(this IInterceptorSubject subject, string key, object? value)
    {
        subject.Data[(null, key)] = value;
    }

    public static bool TryGetData(this IInterceptorSubject subject, string key, out object? value)
    {
        return subject.Data.TryGetValue((null, key), out value);
    }

    /// <summary>
    /// Adds subject data for the specified key only if the key is not already present.
    /// This operation is atomic and thread-safe, so it doubles as a one-shot latch: it returns
    /// <c>true</c> exactly once per subject and key.
    /// </summary>
    /// <returns><c>true</c> if the value was stored; <c>false</c> if a value was already present.</returns>
    public static bool TryAddData(this IInterceptorSubject subject, string key, object? value)
    {
        return subject.Data.TryAdd((null, key), value);
    }
}