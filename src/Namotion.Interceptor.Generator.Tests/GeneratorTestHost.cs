using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Namotion.Interceptor.Interceptors;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// The outcome of running the generator over a single source snippet.
/// </summary>
internal sealed record GeneratorRunResult(
    IReadOnlyList<GeneratedSourceResult> Sources,
    IReadOnlyList<Diagnostic> GeneratorDiagnostics,
    IReadOnlyList<Diagnostic> CompilationDiagnostics,
    Compilation OutputCompilation)
{
    /// <summary>
    /// Warnings are kept alongside the errors because a generated shape can compile and still be
    /// wrong: a shadowing property that omits 'new' produces CS0108, which never surfaces here as
    /// an error.
    /// </summary>
    public IReadOnlyList<Diagnostic> CompilationErrors { get; } = CompilationDiagnostics
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        .ToList();

    /// <summary>
    /// Warnings from the generated compilation, minus the ones the test host itself causes.
    /// CS8632 is excluded because <see cref="GeneratorTestHost"/> builds without a nullable
    /// context, so every existing test source that uses '?' reports it (SourceGeneratorTests.cs:16
    /// among many). Everything else must be empty: src/Directory.Build.props sets
    /// TreatWarningsAsErrors for consumers, so a warning in generated code is a broken build.
    /// </summary>
    public IReadOnlyList<Diagnostic> CompilationWarnings { get; } = CompilationDiagnostics
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
        .Where(diagnostic => diagnostic.Id != "CS8632")
        .ToList();

    public string SingleSource() => Sources.Single().SourceText.ToString();

    public string AllSources() => string.Join("\n\n", Sources.Select(s => s.SourceText));

    private Assembly? _loadedAssembly;

    /// <summary>
    /// Emits the post-generator compilation and loads it, so the shape can be exercised for real.
    /// Generator diagnostics are not part of the compilation, so a shape the generator reports can
    /// still be run, which is the only way to show whether the report was justified. Loaded once per
    /// run: two loads of the same bytes would produce two assemblies whose types are not identical.
    /// </summary>
    public Assembly LoadAssembly()
    {
        if (_loadedAssembly is not null)
        {
            return _loadedAssembly;
        }

        using var stream = new MemoryStream();
        var emitResult = OutputCompilation.Emit(stream);

        Assert.True(
            emitResult.Success,
            "Generated code did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        _loadedAssembly = Assembly.Load(stream.ToArray());
        return _loadedAssembly;
    }

    /// <summary>
    /// Creates an instance of a generated type through its generated parameterless constructor.
    /// </summary>
    public object CreateInstance(string fullTypeName)
    {
        var type = LoadAssembly().GetType(fullTypeName);
        Assert.NotNull(type);
        return Activator.CreateInstance(type)!;
    }
}

/// <summary>
/// Runs the incremental generator against an in-memory compilation.
/// </summary>
internal static class GeneratorTestHost
{
    private static readonly IReadOnlyList<MetadataReference> References = CreateReferences();

    /// <summary>
    /// The library assemblies loaded from memory, by simple name. The default load context binds by
    /// probing the file system, so an assembly loaded from a byte array is never found by name, and a
    /// main assembly referencing one fails with FileNotFoundException on first type load.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Assembly> LoadedLibraryAssemblies = new();

    static GeneratorTestHost()
    {
        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
            assemblyName.Name is not null && LoadedLibraryAssemblies.TryGetValue(assemblyName.Name, out var assembly)
                ? assembly
                : null;
    }

    public static GeneratorRunResult Run(string source)
    {
        // The name is fixed because one library fixture grants InternalsVisibleTo("TestAssembly").
        return RunCore(source, References, "TestAssembly");
    }

    /// <summary>
    /// Same as <see cref="Run"/> but under a name unique to this run, so the emitted assembly can be
    /// loaded next to those of other runs without two of them sharing an identity. Use together with
    /// <see cref="GeneratorRunResult.LoadAssembly"/>.
    /// </summary>
    public static GeneratorRunResult RunForExecution(string source)
    {
        return RunCore(source, References, "TestAssembly_" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Runs the generator against <paramref name="mainSource"/> in a compilation that additionally
    /// references a separate assembly compiled from <paramref name="librarySource"/>. Use this to
    /// verify accessibility rules that only manifest across an assembly boundary (e.g. an
    /// <c>internal</c> or <c>protected internal</c> interface default member declared in a
    /// referenced assembly, where the generated code's own assembly has no InternalsVisibleTo).
    /// </summary>
    public static GeneratorRunResult RunWithLibraryReference(
        string librarySource,
        string mainSource,
        bool runGeneratorOverLibrary = false)
        => RunWithLibraryReferenceCore(
            librarySource, mainSource, runGeneratorOverLibrary, "TestLibrary", "TestAssembly", loadLibrary: false);

    /// <summary>
    /// Same as <see cref="RunWithLibraryReference"/>, but under names unique to this run and with the
    /// library assembly loaded first, so that <see cref="GeneratorRunResult.LoadAssembly"/> can bind
    /// its reference. Use this when a cross-assembly shape has to be exercised for real, which is the
    /// only evidence there is for a shape that produces no compiler diagnostic. The generator always
    /// runs over the library here: a base whose interception members are not generated cannot host a derived-mode
    /// subclass, so there would be nothing to run.
    /// </summary>
    public static GeneratorRunResult RunWithLibraryReferenceForExecution(string librarySource, string mainSource)
    {
        var runIdentity = Guid.NewGuid().ToString("N");
        return RunWithLibraryReferenceCore(
            librarySource,
            mainSource,
            runGeneratorOverLibrary: true,
            "TestLibrary_" + runIdentity,
            "TestAssembly_" + runIdentity,
            loadLibrary: true);
    }

    private static GeneratorRunResult RunWithLibraryReferenceCore(
        string librarySource,
        string mainSource,
        bool runGeneratorOverLibrary,
        string libraryAssemblyName,
        string mainAssemblyName,
        bool loadLibrary)
    {
        var libraryCompilation = CSharpCompilation.Create(
            assemblyName: libraryAssemblyName,
            syntaxTrees: [CSharpSyntaxTree.ParseText(librarySource)],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Opt in, not automatic. NI0012's stale-base fixture is a base built by an OLDER generator:
        // running the current generator over it would emit protected helpers, satisfy the contract,
        // and make NI0012 unreachable.
        if (runGeneratorOverLibrary)
        {
            GeneratorDriver libraryDriver = CSharpGeneratorDriver.Create(new InterceptorSubjectGenerator());
            libraryDriver.RunGeneratorsAndUpdateCompilation(libraryCompilation, out var updated, out _);
            libraryCompilation = (CSharpCompilation)updated;
        }

        using var libraryStream = new MemoryStream();
        var emitResult = libraryCompilation.Emit(libraryStream);
        Assert.True(
            emitResult.Success,
            "Library compilation did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, emitResult.Diagnostics.Select(d => d.ToString())));

        var libraryBytes = libraryStream.ToArray();
        var libraryReference = MetadataReference.CreateFromImage(libraryBytes);

        // Loaded before the main assembly, so the reference the main assembly carries resolves by
        // name to this one instead of failing to bind.
        if (loadLibrary)
        {
            var libraryAssembly = Assembly.Load(libraryBytes);
            LoadedLibraryAssemblies[libraryAssembly.GetName().Name!] = libraryAssembly;
        }

        return RunCore(mainSource, References.Append(libraryReference).ToList(), mainAssemblyName);
    }

    private static GeneratorRunResult RunCore(
        string source,
        IReadOnlyList<MetadataReference> references,
        string assemblyName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new InterceptorSubjectGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var runResult = driver.GetRunResult();

        return new GeneratorRunResult(
            runResult.Results.SelectMany(result => result.GeneratedSources).ToList(),
            runResult.Diagnostics.ToList(),
            outputCompilation.GetDiagnostics().ToList(),
            outputCompilation);
    }

    /// <summary>
    /// Runs the generator and fails the test if the resulting compilation has any error.
    /// Use for inputs that are themselves valid C#. Inputs that are invalid by construction
    /// (CS0754, CS0592) must use <see cref="Run"/> and assert on the expected error instead.
    /// </summary>
    public static GeneratorRunResult RunExpectingCleanCompilation(string source)
    {
        var result = Run(source);

        Assert.True(
            result.CompilationErrors.Count == 0,
            "Generated code did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationErrors.Select(d => d.ToString())));

        return result;
    }

    /// <summary>
    /// Same as <see cref="RunExpectingCleanCompilation"/>, and additionally fails on any warning
    /// other than CS8632. Use this for any shape where the risk is a hiding or sealed-member
    /// warning (CS0108, CS0109, CS0628), which a consumer build turns into an error.
    /// </summary>
    public static GeneratorRunResult RunExpectingNoWarnings(string source)
    {
        var result = RunExpectingCleanCompilation(source);

        Assert.True(
            result.CompilationWarnings.Count == 0,
            "Generated code compiled with warnings, which are errors in consumer builds:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationWarnings.Select(d => d.ToString())));

        return result;
    }

    /// <summary>
    /// Same contract as <see cref="RunExpectingCleanCompilation"/>, but for
    /// <see cref="RunWithLibraryReference"/>.
    /// </summary>
    public static GeneratorRunResult RunWithLibraryReferenceExpectingCleanCompilation(string librarySource, string mainSource)
    {
        var result = RunWithLibraryReference(librarySource, mainSource);

        Assert.True(
            result.CompilationErrors.Count == 0,
            "Generated code did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationErrors.Select(d => d.ToString())));

        return result;
    }

    /// <summary>
    /// The trusted platform assembly set is used instead of the loaded assemblies, because
    /// System.Text.Json is not loaded in the test AppDomain and the generated code needs
    /// JsonIgnore to resolve.
    /// </summary>
    private static IReadOnlyList<MetadataReference> CreateReferences()
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path));

        var loadedAssemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .Select(assembly => assembly.Location);

        return trustedAssemblies
            .Concat(loadedAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }
}
