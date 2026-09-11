namespace Namotion.Interceptor;

/// <summary>Experimental conditional admission for one synchronous property write.</summary>
/// <remarks>Methods run under the property's subject lock. Implementations must not invoke subject setters or acquire locks shared with unrelated subjects.</remarks>
public interface IPropertyWriteGuard
{
    /// <summary>Attempts admission. A false result must leave no guard lock held.</summary>
    bool TryEnter(PropertyReference property);

    /// <summary>Releases successful admission, including when the setter throws.</summary>
    void Exit(bool committed);
}
