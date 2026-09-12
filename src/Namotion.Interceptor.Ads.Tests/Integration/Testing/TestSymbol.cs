namespace Namotion.Interceptor.Ads.Tests.Integration.Testing;

/// <summary>
/// Defines a symbol to register on the test ADS server.
/// </summary>
/// <param name="Path">The symbol path (e.g., "GVL.Temperature").</param>
/// <param name="DataType">The .NET type of the symbol value.</param>
/// <param name="InitialValue">The initial value of the symbol.</param>
/// <param name="StringEncoding">For a string symbol, the PLC encoding. Defaults to single-byte,
/// i.e. a plain <c>STRING(n)</c>; pass <see cref="System.Text.Encoding.Unicode"/> for a
/// <c>WSTRING(n)</c>, which the any-type notification path cannot marshal.</param>
public record TestSymbol(
    string Path,
    Type DataType,
    object? InitialValue,
    System.Text.Encoding? StringEncoding = null);
