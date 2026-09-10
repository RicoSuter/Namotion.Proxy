namespace HomeBlaze.History.Abstractions;

/// <summary>
/// The typed column a value is routed into.
/// </summary>
/// <remarks>
/// These values are persisted: the SQLite store writes the ordinal into <c>paths.value_column</c> and
/// reads it back to decide which column a property's values live in. Reordering the members or reusing
/// a number silently reinterprets every database file already on disk, with no error at any layer.
/// Assign the next free number when adding a member, and never renumber an existing one.
/// </remarks>
public enum ValueColumn
{
    /// <summary>Integer types and bool (bool as 0/1).</summary>
    Long = 0,

    /// <summary>double, float, and decimal (decimal also archives its exact text in the persistent store).</summary>
    Double = 1,

    /// <summary>string, enum, and (v1.1) path references.</summary>
    Json = 2
}

/// <summary>
/// Single source of truth for routing a value into a column (write) and building
/// column-targeted SQL (read).
/// </summary>
public static class HistoryColumns
{
    /// <summary>
    /// Returns the column a value of <paramref name="propertyType"/> is stored in.
    /// </summary>
    public static ValueColumn GetValueColumnFor(Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return ValueColumn.Double;
        if (IsBigIntCompatible(type)) return ValueColumn.Long;
        return ValueColumn.Json; // string, enum, (v1.1) path references
    }

    /// <summary>
    /// Returns true if a value of <paramref name="propertyType"/> can be recorded as a scalar sample;
    /// complex types are deferred. This is the type half of history eligibility. The graph half (is it
    /// a scalar [State] property) needs the registry and so lives in <c>HomeBlaze.History</c>.
    /// </summary>
    public static bool IsRecordable(Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(double) || type == typeof(float)) return true; // value_double
        if (IsBigIntCompatible(type)) return true;                        // value_long
        if (type == typeof(decimal)) return true;                         // value_double (exact text in value_json)
        if (type == typeof(string)) return true;                          // value_json
        if (type.IsEnum) return true;                                     // value_json (enum name)
        return false;                                                     // complex types deferred
    }

    /// <summary>
    /// Returns true for ulong (or ulong?) properties. ulong values above long.MaxValue spill
    /// to value_json; read paths COALESCE across both columns.
    /// </summary>
    public static bool IsUlongProperty(Type propertyType) =>
        (Nullable.GetUnderlyingType(propertyType) ?? propertyType) == typeof(ulong);

    /// <summary>
    /// Returns true for integer types and bool, which all store losslessly in value_long.
    /// Shared by column dispatch and eligibility so both agree on what lands in value_long.
    /// </summary>
    private static bool IsBigIntCompatible(Type type) =>
        type == typeof(long) || type == typeof(int) || type == typeof(short) ||
        type == typeof(sbyte) || type == typeof(byte) || type == typeof(ushort) ||
        type == typeof(uint) || type == typeof(ulong) || type == typeof(bool);
}
