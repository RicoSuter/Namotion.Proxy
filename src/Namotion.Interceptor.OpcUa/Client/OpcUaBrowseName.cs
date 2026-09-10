namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Parsing for the OPC UA browse-name collection/dictionary convention, where a member's
/// browse name ends with a bracketed index or key, for example "Items[3]" or "Items[name]".
/// The same bracket content is used to classify a parent node (array vs dictionary vs single
/// reference) and to extract the dictionary key from a member browse name, so both consumers
/// share this single parser to keep classification and key extraction from drifting apart.
/// </summary>
internal static class OpcUaBrowseName
{
    /// <summary>
    /// Extracts the content of the trailing "[...]" segment of a browse name. Returns false
    /// when the name has no trailing bracket pair or the brackets are empty, since empty
    /// brackets carry no index or key information.
    /// </summary>
    public static bool TryGetBracketContent(string browseName, out ReadOnlySpan<char> content)
    {
        var bracketStart = browseName.LastIndexOf('[');
        if (bracketStart >= 0 && browseName.EndsWith(']'))
        {
            var contentLength = browseName.Length - bracketStart - 2;
            if (contentLength > 0)
            {
                content = browseName.AsSpan(bracketStart + 1, contentLength);
                return true;
            }
        }

        content = default;
        return false;
    }
}
