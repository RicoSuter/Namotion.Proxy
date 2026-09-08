namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Resolves the directory that holds the SQLite partition database files. Relative paths resolve under the
/// application data directory (<see cref="Environment.SpecialFolder.LocalApplicationData"/> plus "HomeBlaze"),
/// which is durable across application updates and is never under the scanned HomeBlaze <c>Data/</c> storage
/// root. Absolute paths are used as-is.
/// </summary>
internal static class SqliteDatabaseLocation
{
    /// <summary>
    /// The folder name used when no database path is configured.
    /// </summary>
    public const string DefaultFolderName = "History";

    /// <summary>
    /// The base directory under which relative database paths resolve: the per-user local application data
    /// folder with a "HomeBlaze" subfolder. This is outside the scanned HomeBlaze <c>Data/</c> storage root.
    /// </summary>
    public static string DefaultBaseDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeBlaze");

    /// <summary>
    /// Resolves the configured database path against the given base directory. Null, empty, or whitespace
    /// resolves to the default "History" folder under the base directory. A rooted (absolute) path is returned
    /// unchanged; a relative path is combined with the base directory.
    /// </summary>
    public static string Resolve(string? configuredPath, string baseDirectory)
    {
        var value = string.IsNullOrWhiteSpace(configuredPath) ? DefaultFolderName : configuredPath.Trim();

        return Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value);
    }
}
