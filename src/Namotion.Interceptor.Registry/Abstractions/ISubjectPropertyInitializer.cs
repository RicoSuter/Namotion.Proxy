namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// Contributes attributes or derived properties to a subject's properties as the subject is
/// registered. Initializers are picked up from the context
/// (<c>context.AddService&lt;ISubjectPropertyInitializer&gt;(...)</c>) or from a .NET attribute on the
/// property that implements this interface, and run for every property of the subject, including
/// properties other initializers added dynamically.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be idempotent. A subject that leaves the graph is dropped from the registry
/// but keeps the properties an initializer added, so the next attach runs every initializer again
/// over properties that are already there and an initializer that adds unconditionally throws.
/// Moving a subject between parents is enough to trigger this whenever the old reference is removed
/// before the new one is added.
/// </para>
/// <code>
/// public void InitializeProperty(RegisteredSubjectProperty property)
/// {
///     if (property.IsAttribute || property.TryGetAttribute("Unit") is not null)
///     {
///         return;
///     }
///
///     property.AddAttribute("Unit", typeof(string), _ => "C", null);
/// }
/// </code>
/// </remarks>
public interface ISubjectPropertyInitializer
{
    /// <summary>
    /// Initializes a single registered property. Called once per property per registration, so more
    /// than once for a subject that is re-attached.
    /// </summary>
    /// <param name="property">The property being registered.</param>
    void InitializeProperty(RegisteredSubjectProperty property);
}
