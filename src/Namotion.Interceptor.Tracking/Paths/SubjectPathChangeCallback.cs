namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>Delegate form of <see cref="ISubjectPathChangeObserver{TValue}"/>. Must be fast, non-blocking, and must not throw.</summary>
public delegate void SubjectPathChangeCallback<TValue>(in SubjectPathChange<TValue> change);

/// <summary>Zero-closure observer for a path subscription; mirrors <c>IPropertyChangeObserver</c>. Implementations must be fast, non-blocking, and must not throw.</summary>
public interface ISubjectPathChangeObserver<TValue>
{
    void OnChange(in SubjectPathChange<TValue> change);
}
