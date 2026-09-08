using System.Reflection;

namespace HomeBlaze.Services;

/// <summary>
/// Central provider for types from assemblies. Used by registries for lazy scanning.
/// </summary>
public class TypeProvider
{
    private readonly Lock _lock = new();

    // Copy-on-write: readers take the current array without a lock, writers publish a replacement.
    // Registration is a short burst at startup while the lookups below run for the life of the process
    // and sit on request paths, so the cost belongs on the write side. Publishing a new array also means
    // a reader that is midway through an enumeration keeps walking the snapshot it started on.
    private Type[] _types = [];

    /// <summary>
    /// Gets all collected types.
    /// </summary>
    public IReadOnlyCollection<Type> Types => Volatile.Read(ref _types);

    /// <summary>
    /// Adds exported types from an assembly.
    /// </summary>
    public TypeProvider AddAssembly(Assembly assembly)
    {
        Type[] exportedTypes;
        try
        {
            exportedTypes = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            exportedTypes = exception.Types.Where(type => type is not null).ToArray()!;
        }

        AddTypes(exportedTypes);
        return this;
    }

    /// <summary>
    /// Adds types directly (e.g., from plugins).
    /// </summary>
    public void AddTypes(IEnumerable<Type> types)
    {
        var addedTypes = types as Type[] ?? types.ToArray();
        if (addedTypes.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            var existingTypes = _types;
            var combinedTypes = new Type[existingTypes.Length + addedTypes.Length];

            existingTypes.CopyTo(combinedTypes, 0);
            addedTypes.CopyTo(combinedTypes, existingTypes.Length);

            Volatile.Write(ref _types, combinedTypes);
        }
    }
}
