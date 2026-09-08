namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// A type that lexically contains an interceptor subject. The keyword is carried because the
/// generated partial declaration must repeat it: "partial class" against a record container is
/// a CS0261.
/// </summary>
internal sealed record ContainingType(string Keyword, string Name);
