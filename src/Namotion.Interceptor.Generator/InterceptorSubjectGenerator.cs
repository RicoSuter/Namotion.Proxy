using System;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Namotion.Interceptor.Generator;

[Generator]
public class InterceptorSubjectGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classWithAttributeProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                // A struct or interface can never be a valid subject (InterceptorSubjectAttribute's
                // AttributeUsage is Class-only, so the compiler already reports CS0592 on those);
                // this predicate has never matched them, so nothing here is skipping GetDeclaredSymbol
                // or GetSemanticModel work that would otherwise run for them. Records are the one
                // deliberate addition: the compiler accepts the attribute on a record class, so
                // NI0003 below is the only report that case gets, at a measured cost of about 3
                // milliseconds per 150 attributed records.
                predicate: (node, _) =>
                    node is ClassDeclarationSyntax { AttributeLists.Count: > 0 } or
                    RecordDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: (ctx, ct) =>
                {
                    var model = ctx.SemanticModel;
                    var typeDeclaration = (TypeDeclarationSyntax)ctx.Node;

                    // Get the type symbol to access all partial declarations
                    var typeSymbol = model.GetDeclaredSymbol(typeDeclaration, ct);
                    if (typeSymbol is null)
                        return null;

                    // Check if ANY partial declaration has the InterceptorSubjectAttribute
                    var hasAttributeInAnyPartial = typeSymbol.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax(ct))
                        .OfType<TypeDeclarationSyntax>()
                        .Any(c =>
                        {
                            var declarationModel = model.Compilation.GetSemanticModel(c.SyntaxTree);
                            return HasInterceptorSubjectAttribute(c, declarationModel, ct);
                        });

                    return hasAttributeInAnyPartial
                        ? new
                        {
                            Model = model,
                            TypeDeclaration = typeDeclaration,
                            TypeSymbol = typeSymbol
                        }
                        : null;
                })
            .Select((tuple, _) =>
            {
                if (tuple is null)
                {
                    return null;
                }

                var typeSymbol = tuple.TypeSymbol;
                return new
                {
                    tuple.Model,
                    tuple.TypeDeclaration,
                    TypeSymbol = typeSymbol,
                    TypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                };
            })
            .Where(m => m is not null)
            .Collect()
            .SelectMany((items, _) => items
                .GroupBy(x => x!.TypeName)
                .Select(g => g.First())); // take only one per type name to avoid duplicates

        context.RegisterSourceOutput(classWithAttributeProvider, (spc, cls) =>
        {
            if (cls is null) return;

            try
            {
                var extraction = SubjectMetadataExtractor.Extract(
                    cls.TypeSymbol,
                    cls.TypeDeclaration,
                    cls.Model,
                    spc.CancellationToken);

                foreach (var diagnostic in extraction.Diagnostics)
                {
                    spc.ReportDiagnostic(diagnostic);
                }

                if (extraction.Metadata is null)
                {
                    return;
                }

                var fileName = SubjectCodeGenerator.GetFileName(extraction.Metadata);
                var generatedCode = SubjectCodeGenerator.Generate(extraction.Metadata);

                spc.AddSource(fileName, SourceText.From(generatedCode, Encoding.UTF8));
            }
            catch (Exception exception)
            {
                var className = cls.TypeDeclaration.Identifier.ValueText;

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.GeneratorFailed,
                    cls.TypeDeclaration.Identifier.GetLocation(),
                    className,
                    exception.GetType().Name,
                    exception.Message));

                // The file keeps the full frames; a diagnostic message renders as one line. The
                // hint name must be unique across the whole generator run, not just readable: two
                // failing subjects that share a simple class name, or a failing "N.Foo" alongside a
                // succeeding global-namespace "Foo" (a pair GetFileName's own namespace-qualified
                // naming never produces), collide on the bare class name, and AddSource throws
                // ArgumentException on a duplicate hint name from inside this very catch block.
                // Roslyn turns that into CS8785, which drops every generated file for the run, not
                // just this subject's. cls.TypeName is the fully-qualified display name already used
                // to de-duplicate subjects upstream, so it is unique per type; sanitise it into a
                // valid hint name instead.
                spc.AddSource(GetFailureHintName(cls.TypeName), SourceText.From($"/* {exception} */", Encoding.UTF8));
            }
        });
    }

    /// <summary>
    /// Turns a fully-qualified type display name (e.g. "global::N.Foo&lt;string&gt;") into a hint
    /// name that <c>SourceProductionContext.AddSource</c> accepts and that stays unique per type,
    /// by replacing every character AddSource would reject with '_' and keeping everything else,
    /// including the dots that make the origin still readable in build output.
    /// </summary>
    internal static string GetFailureHintName(string fullyQualifiedTypeName)
    {
        var builder = new StringBuilder(fullyQualifiedTypeName.Length + 5);
        foreach (var character in fullyQualifiedTypeName)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' ? character : '_');
        }

        return builder.Append(".g.cs").ToString();
    }

    private static bool HasInterceptorSubjectAttribute(TypeDeclarationSyntax typeDeclaration, SemanticModel semanticModel, CancellationToken ct)
    {
        return SymbolExtensions.HasAttribute(typeDeclaration.AttributeLists, KnownTypes.InterceptorSubjectAttribute, semanticModel, ct);
    }
}
