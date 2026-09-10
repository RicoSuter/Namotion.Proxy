# Subject Property Precedence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a derived subject's own property declaration win over anything it inherits, and turn every remaining "one property name, two readers" shape into a build error.

**Architecture:** The generated `DefaultProperties` becomes a precedence-ordered sequence (own declarations, inherited, own interface defaults) deduplicated with `DistinctBy`, which keeps the first occurrence. Three diagnostics enforce what precedence cannot repair: NI0005 and NI0008 rise from warning to error, and a new NI0015 rejects a declaration that displaces an ancestor subject's property. Two smaller correctness fixes ride along in the same methods: an unambiguous `PropertyInfo` lookup and a skip for interface properties that would emit no usable accessor.

**Tech Stack:** C# 13, Roslyn incremental source generator (`Microsoft.CodeAnalysis.CSharp` 4.14.0), xUnit, Verify.Xunit snapshots.

## Global Constraints

- Design spec: `docs/superpowers/specs/2026-09-10-subject-property-precedence-design.md`. Read it before starting.
- `src/Directory.Build.props` sets `TreatWarningsAsErrors`. Any warning, including CS0108 and CS0109 inside generated files, fails the build.
- The generator project targets .NET Standard 2.0. Use `IsAbstract`/`IsInitOnly`-style symbol APIs only; no `System.Linq` methods newer than .NET Standard 2.0 inside the generator itself. `DistinctBy` appears only in *emitted* code, which runs on .NET 9.
- Emitted code already imports `System.Linq`, so `DistinctBy` needs no new using.
- Never use em dashes in docs, READMEs or PR descriptions.
- Markdown paragraphs go on one line. Do not hard wrap at a column.
- No AI attribution in commit messages: no agent names, no `Co-Authored-By` trailers, no "Generated with" footers.
- Snapshot loop: prefix snapshot test runs with `DiffEngine_Disabled=true` so no diff tool launches. Accept a snapshot by replacing the `.verified.txt` with the test's `.received.txt` only after reading the diff.
- Test naming is `When<Condition>_Then<ExpectedBehavior>`, with explicit `// Arrange`, `// Act`, `// Assert` comments.

## File Structure

| File | Responsibility | Tasks |
|---|---|---|
| `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs` | Emits the generated partial. `EmitDefaultProperties` is rewritten into tiers; the class-branch `GetProperty` call gains `DeclaredOnly` | 1, 2 |
| `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs` | Builds `SubjectMetadata`. Gains the init-only skip and the NI0015 scan, and NI0005 learns to stand down where NI0015 fired | 3, 5 |
| `src/Namotion.Interceptor.Generator/Diagnostics.cs` | Descriptors. NI0005 and NI0008 severities change; NI0015 is added | 4, 5 |
| `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md` | Release tracking table. Roslyn's RS2000 analyzer fails the build if it disagrees with the descriptors | 4, 5 |
| `src/Namotion.Interceptor.Generator.Tests/DiagnosticTests.cs` | Diagnostic tests | 3, 4, 5 |
| `src/Namotion.Interceptor.Generator.Tests/PropertyPrecedenceTests.cs` (new) | Runtime behaviour of the precedence rule, compiled by the generator inside the test project | 1, 2 |
| `docs/generator.md`, `docs/design/generator-supported-shapes.md`, `docs/subject-guidelines.md` | User and design documentation | 6 |

Tests for precedence go in a new file rather than into `BaseClassInterceptionBehaviorTests.cs`, because they need their own model hierarchy and that file is already large.

---

### Task 1: Unambiguous `PropertyInfo` lookup

A `new` property whose type differs from the one it hides makes `typeof(Leaf).GetProperty(name, Public | NonPublic | Instance)` throw `AmbiguousMatchException`, so the first `Properties` access throws `TypeInitializationException`. `BindingFlags.DeclaredOnly` fixes it, and is correct because every property reaching this branch came from the subject's own declarations.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:254`
- Create: `src/Namotion.Interceptor.Generator.Tests/PropertyPrecedenceTests.cs`
- Modify: all 16 `src/Namotion.Interceptor.Generator.Tests/Snapshots/*.verified.txt` that contain a class-branch `GetProperty` line

**Interfaces:**
- Consumes: nothing
- Produces: nothing consumed by later tasks. Task 2 rewrites the surrounding method and must preserve this flag.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Generator.Tests/PropertyPrecedenceTests.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

public class PlainOriginBase
{
    public string Origin => "base";
}

[InterceptorSubject]
public partial class NarrowedOriginSubject : PlainOriginBase
{
    public new partial int Origin { get; set; }
}

public class PropertyPrecedenceTests
{
    [Fact]
    public void WhenNewPropertyTypeDiffersFromTheHiddenOne_ThenPropertiesDoesNotThrow()
    {
        // Arrange
        var subject = new NarrowedOriginSubject();

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal(typeof(int), properties["Origin"].Type);
        Assert.Equal(typeof(NarrowedOriginSubject), properties["Origin"].PropertyInfo?.DeclaringType);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenNewPropertyTypeDiffersFromTheHiddenOne"
```

Expected: FAIL with `TypeInitializationException`, inner exception `AmbiguousMatchException`.

- [ ] **Step 3: Add `DeclaredOnly` to the class branch**

In `SubjectCodeGenerator.cs`, in `EmitDefaultProperties`, replace this line:

```csharp
                builder.AppendLine($"                        typeof({metadata.ClassName}).GetProperty(nameof({property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,");
```

with:

```csharp
                // DeclaredOnly because a 'new' property whose type differs from the one it hides makes
                // the unfiltered lookup ambiguous, which throws at type init. Every property reaching
                // this branch came from the subject's own declarations, so the filter drops nothing.
                builder.AppendLine($"                        typeof({metadata.ClassName}).GetProperty(nameof({property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!,");
```

Leave the interface branch (the `typeof({accessorInterfaceTypeName})` line) untouched. Reflection on an interface type does not return members of base interfaces, so the flag would be a no-op there and would only churn snapshots.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenNewPropertyTypeDiffersFromTheHiddenOne"
```

Expected: PASS.

- [ ] **Step 5: Update the snapshots**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~Snapshot|FullyQualifiedName~SourceGeneratorTests|FullyQualifiedName~InterfaceDefaultPropertyTests|FullyQualifiedName~VirtualPartialTests"
```

Read each `.received.txt` diff. The only change in every one must be `BindingFlags.Instance` becoming `BindingFlags.Instance | BindingFlags.DeclaredOnly` on class-branch lines. If any other line moved, stop and investigate. Then replace each `.verified.txt` with its `.received.txt` and re-run to confirm green.

- [ ] **Step 6: Run the full unit suite**

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs src/Namotion.Interceptor.Generator.Tests/
git commit -m "fix: make the emitted PropertyInfo lookup unambiguous

A 'new' property whose type differs from the one it hides made
typeof(T).GetProperty(name, Public | NonPublic | Instance) ambiguous, so the
first Properties access threw TypeInitializationException. Every property on
that branch comes from the subject's own declarations, so DeclaredOnly drops
nothing and resolves the ambiguity."
```

---

### Task 2: Three-tier precedence

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs` (`EmitDefaultProperties`, lines 205 to 270)
- Modify: `src/Namotion.Interceptor.Generator.Tests/PropertyPrecedenceTests.cs`
- Modify: 3 generator snapshots and 1 registry snapshot, named in Step 6

**Interfaces:**
- Consumes: `PropertyMetadata.IsFromInterface` (bool), `SubjectMetadata.BaseClass.TypeName` (`string?`), both existing.
- Produces: emitted `DefaultProperties` whose derived form ends `.DistinctBy(pair => pair.Key).ToFrozenDictionary();` and whose root form ends `.ToFrozenDictionary();` with no `Concat` and no `DistinctBy`.

- [ ] **Step 1: Write the failing tests**

Append to `src/Namotion.Interceptor.Generator.Tests/PropertyPrecedenceTests.cs`, above the `PropertyPrecedenceTests` class:

```csharp
public interface IHasLevel
{
    int Level => 0;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class PrecedenceMarkerAttribute : Attribute;

[InterceptorSubject]
public partial class PrecedenceMachine : IHasLevel
{
    public partial string Name { get; set; }
}

[InterceptorSubject]
public partial class PrecedencePump : PrecedenceMachine, IHasLevel
{
    [PrecedenceMarker]
    public partial int Level { get; set; }
}

[InterceptorSubject]
public partial class PrecedenceSmartPump : PrecedencePump
{
    public partial string Label { get; set; }
}
```

and these tests inside the `PropertyPrecedenceTests` class:

```csharp
    [Fact]
    public void WhenLeafDeclaresAnInheritedInterfaceDefault_ThenTheLeafDeclarationWins()
    {
        // Arrange
        var pump = new PrecedencePump();
        pump.Level = 42;

        // Act
        var metadata = ((IInterceptorSubject)pump).Properties["Level"];

        // Assert
        Assert.Equal(typeof(PrecedencePump), metadata.PropertyInfo?.DeclaringType);
        Assert.True(metadata.IsIntercepted);
        Assert.NotNull(metadata.SetValue);
        Assert.Equal(42, metadata.GetValue?.Invoke(pump));
        Assert.Contains(metadata.Attributes, attribute => attribute is PrecedenceMarkerAttribute);
    }

    [Fact]
    public void WhenASubjectBelowTheDeclarationAddsNothing_ThenTheDeclarationStillWins()
    {
        // Arrange
        var smartPump = new PrecedenceSmartPump();
        smartPump.Level = 7;

        // Act
        var metadata = ((IInterceptorSubject)smartPump).Properties["Level"];

        // Assert
        Assert.Equal(typeof(PrecedencePump), metadata.PropertyInfo?.DeclaringType);
        Assert.True(metadata.IsIntercepted);
        Assert.Equal(7, metadata.GetValue?.Invoke(smartPump));
    }

    [Fact]
    public void WhenAnInterfaceDefaultIsNotDeclaredByAnySubject_ThenItIsStillExposed()
    {
        // Arrange
        var machine = new PrecedenceMachine();

        // Act
        var properties = ((IInterceptorSubject)machine).Properties;

        // Assert
        Assert.Equal(0, properties["Level"].GetValue?.Invoke(machine));
        Assert.False(properties["Level"].IsIntercepted);
    }
```

`PrecedencePump` re-lists `IHasLevel` deliberately. Without it the shape trips NI0005, which Task 4 turns into an error.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~PropertyPrecedenceTests"
```

Expected: the first two FAIL. `DeclaringType` is `IHasLevel`, `IsIntercepted` is `False`, `SetValue` is null, the value read is `0`, and the marker attribute is absent. The third passes already.

- [ ] **Step 3: Rewrite `EmitDefaultProperties`**

Replace the whole method in `SubjectCodeGenerator.cs` with:

```csharp
    /// <summary>
    /// Precedence, highest first: the subject's own declarations, then everything it inherited, then
    /// the interface default implementations it adopted. An adopted default is a fallback, so it must
    /// rank below an inherited real property; otherwise a subject that declares nothing re-injects the
    /// default and overwrites an ancestor's declaration.
    /// </summary>
    private static void EmitDefaultProperties(StringBuilder builder, SubjectMetadata metadata)
    {
        var newModifier = metadata.BaseClass.TypeName is not null ? "new " : "";

        builder.AppendLine($"        public {newModifier}static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties {{ get; }} =");

        // A root has nothing to lose a key to, so it stays a single dictionary: the extractor already
        // drops an interface default whose name the class declares, and no Concat means no duplicate
        // to resolve.
        if (metadata.BaseClass.TypeName is null)
        {
            EmitPropertyDictionary(builder, metadata, metadata.Properties);
            builder.AppendLine("            .ToFrozenDictionary();");
            builder.AppendLine();
            return;
        }

        // Partitioned on IsFromInterface alone. A class-declared explicit implementation is a
        // declaration in the subject's own class and belongs in the first tier, even though it is
        // emitted through an interface cast like an adopted default.
        var ownProperties = metadata.Properties.Where(property => !property.IsFromInterface).ToList();
        var interfaceDefaults = metadata.Properties.Where(property => property.IsFromInterface).ToList();

        EmitPropertyDictionary(builder, metadata, ownProperties);
        builder.AppendLine($"            .Concat({metadata.BaseClass.TypeName}.DefaultProperties)");

        if (interfaceDefaults.Count > 0)
        {
            builder.AppendLine("            .Concat(");
            EmitPropertyDictionary(builder, metadata, interfaceDefaults);
            builder.AppendLine("            )");
        }

        // Keeps the FIRST occurrence, which is what makes the order above a precedence order. Without
        // it ToFrozenDictionary keeps the last, which is how the base used to overwrite the subject.
        builder.AppendLine("            .DistinctBy(pair => pair.Key)");
        builder.AppendLine("            .ToFrozenDictionary();");
        builder.AppendLine();
    }

    private static void EmitPropertyDictionary(
        StringBuilder builder,
        SubjectMetadata metadata,
        IReadOnlyList<PropertyMetadata> properties)
    {
        builder.AppendLine("            new Dictionary<string, SubjectPropertyMetadata>");
        builder.AppendLine("            {");

        // Each entry is emitted as an indexer assignment (["Name"] = ...) rather than a
        // collection-initializer Add(...), so a duplicate key within one tier silently overwrites
        // rather than throwing at type init. The extractor dedups names and reports NI0008, so this
        // should never trigger; across tiers DistinctBy resolves duplicates instead.
        foreach (var property in properties)
        {
            // An explicitly implemented member is unreachable through the class, so it is emitted
            // through the interface exactly like an interface default property.
            var accessorInterfaceTypeName = property.IsFromInterface
                ? property.InterfaceTypeName
                : property.ExplicitInterfaceTypeName;

            if (accessorInterfaceTypeName is not null)
            {
                var getterLambda = property.HasGetter
                    ? $"(o) => (({accessorInterfaceTypeName})o).{property.Name}"
                    : "null";
                var setterLambda = property.HasSetter
                    ? $"(o, v) => (({accessorInterfaceTypeName})o).{property.Name} = ({property.FullTypeName})v"
                    : "null";

                builder.AppendLine($"                    [\"{property.Name}\"] = new SubjectPropertyMetadata(");
                builder.AppendLine($"                        typeof({accessorInterfaceTypeName}).GetProperty(nameof({accessorInterfaceTypeName}.{property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,");
                builder.AppendLine($"                        {getterLambda},");
                builder.AppendLine($"                        {setterLambda},");
                builder.AppendLine("                        isIntercepted: false,");
                builder.AppendLine("                        isDynamic: false),");
            }
            else
            {
                var getterLambda = property.HasGetter
                    ? $"(o) => (({metadata.ClassName})o).{property.Name}"
                    : "null";
                // Note: init-only properties cannot have a setter lambda because they can only be set during construction
                var setterLambda = property.HasSetter
                    ? $"(o, v) => (({metadata.ClassName})o).{property.Name} = ({property.FullTypeName})v"
                    : "null";

                builder.AppendLine($"                    [\"{property.Name}\"] = new SubjectPropertyMetadata(");
                // DeclaredOnly because a 'new' property whose type differs from the one it hides makes
                // the unfiltered lookup ambiguous, which throws at type init. Every property reaching
                // this branch came from the subject's own declarations, so the filter drops nothing.
                builder.AppendLine($"                        typeof({metadata.ClassName}).GetProperty(nameof({property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!,");
                builder.AppendLine($"                        {getterLambda},");
                builder.AppendLine($"                        {setterLambda},");
                builder.AppendLine($"                        isIntercepted: {(property.IsPartial ? "true" : "false")},");
                builder.AppendLine("                        isDynamic: false),");
            }
        }

        builder.AppendLine("            }");
    }
```

`SubjectCodeGenerator.cs` already imports `System.Linq`, `System.Text` and `Namotion.Interceptor.Generator.Models`. Add `using System.Collections.Generic;` for the `IReadOnlyList<PropertyMetadata>` parameter.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~PropertyPrecedenceTests"
```

Expected: all four PASS, Task 1's test included.

- [ ] **Step 5: Verify `EmitProperties` was not disturbed**

`EmitProperties` and the accessor branch of the emitter group properties by `IsFromInterface || ExplicitInterfaceTypeName is not null`, which is a different partition from the tier split. Confirm neither was changed:

```bash
grep -n "IsFromInterface" src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs
```

Expected: the tier split in `EmitDefaultProperties`, the `accessorInterfaceTypeName` line in `EmitPropertyDictionary`, and whatever `EmitProperties` already had, unchanged.

- [ ] **Step 6: Update the snapshots**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Exactly these four should move:

- `src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithInheritance_ThenPartialClassIsGenerated.verified.txt`
- `src/Namotion.Interceptor.Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithInheritanceAndCustomAttribute_ThenBasePropertiesAreIncluded.verified.txt`
- `src/Namotion.Interceptor.Generator.Tests/Snapshots/VirtualPartialTests.Test_VirtualInheritanceChain_GeneratesCorrectly.verified.txt`
- `src/Namotion.Interceptor.Registry.Tests/SubjectRegistryTests.WhenCreatingSubjectWithInheritance_ThenAllPropertiesAreAvailable.verified.txt`

The four `InterfaceDefaultPropertyTests.*.verified.txt` snapshots are root subjects and **must not move**. If one does, the root branch is emitting a `Concat` or a `DistinctBy` it should not.

The registry snapshot is a bare ordered name list. Read the diff and confirm the set of names is unchanged and only their order moved.

- [ ] **Step 7: Check the `override` metadata change**

Six production properties now report the overriding declaration rather than the ancestor:

```bash
grep -rn "override partial" src/HomeBlaze/Namotion.Devices.Philips.Hue/
```

Expected: `HueLightbulb.cs` (`Title`, `IconName`, `IconColor`), `HueButtonDevice.cs`, `HueMotionDevice.cs`. Confirm each repeats `[Derived]` on the override, so no attribute is lost.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs src/Namotion.Interceptor.Generator.Tests/ src/Namotion.Interceptor.Registry.Tests/
git commit -m "fix: let a subject's own property declaration beat what it inherits

DefaultProperties concatenated the base after the subject's own entries and
ToFrozenDictionary keeps the last, so inheritance precedence was inverted at
every level: a derived subject's declaration was discarded along with its
interception and its attributes.

Emits a precedence-ordered sequence instead, own declarations then inherited
then adopted interface defaults, deduplicated with DistinctBy so the first
occurrence wins. An adopted default ranks last because a subject that declares
nothing otherwise re-injects it over an ancestor's real property."
```

---

### Task 3: Skip interface properties that emit no usable accessor

An interface property whose only accessible accessor is `init`, such as `{ protected get; init; }`, passes the reachability guard because the `init` accessor is accessible, then emits both lambdas as null. The key exists in `Properties` and reading or writing it does nothing.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:644` and the `hasGetter`/`hasSetter` block around line 664
- Modify: `src/Namotion.Interceptor.Generator.Tests/DiagnosticTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing test**

Add to `DiagnosticTests.cs`:

```csharp
    [Fact]
    public void WhenInterfaceDefaultHasOnlyAnInitAccessorReachable_ThenItIsNotASubjectProperty()
    {
        // Arrange: the property-level check passes because init is accessible, but generated code
        // cannot call an init accessor from a lambda and cannot read a protected getter, so the entry
        // would carry two null accessors and do nothing.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IProbe
    {
        string Probe { protected get => ""x""; init { } }
    }

    [InterceptorSubject]
    public partial class Subject : IProbe
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain("\"Probe\"", generated.Sources.Single().SourceText.ToString());
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenInterfaceDefaultHasOnlyAnInitAccessorReachable"
```

Expected: FAIL, because `"Probe"` is present in the generated source with both accessor lambdas null.

- [ ] **Step 3: Move the guard below the accessor computation**

In `ExtractInterfaceDefaultProperties`, delete this block:

```csharp
                if (!isGetterAccessible && !isSetterAccessible)
                {
                    continue;
                }
```

and change the accessor computation from:

```csharp
                var hasGetter = property.GetMethod != null && isGetterAccessible;
                var hasSetter = property.SetMethod is { IsInitOnly: false } && isSetterAccessible;
                var hasInit = property.SetMethod?.IsInitOnly == true && isSetterAccessible;
```

to:

```csharp
                var hasGetter = property.GetMethod != null && isGetterAccessible;
                var hasSetter = property.SetMethod is { IsInitOnly: false } && isSetterAccessible;
                var hasInit = property.SetMethod?.IsInitOnly == true && isSetterAccessible;

                // Asked of the accessors that can actually be emitted, not of raw accessibility. An
                // init accessor is accessible but cannot be called from the emitted lambda, so a
                // property whose only reachable accessor is init would produce an entry with two null
                // accessors: a key that exists and does nothing. HasInit does not rescue it, being
                // consulted only when emitting a partial property's own accessor, which an interface
                // default never is.
                //
                // Skipped in silence, like any interface member generated code cannot reach: the model
                // stays truthful with the property absent, and the interface may be third-party,
                // leaving the subject's author no remedy to follow.
                if (!hasGetter && !hasSetter)
                {
                    continue;
                }
```

Move the `winnerByPropertyName[resolvedName] = ...` assignment and the NI0007 report so they run **after** this new guard, so a skipped property neither claims the name nor reports.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenInterfaceDefaultHasOnlyAnInitAccessorReachable"
```

Expected: PASS.

- [ ] **Step 5: Run the full unit suite**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: all pass and no snapshot moves. This guard only removes entries whose accessors were both null, so nothing that worked can change.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs src/Namotion.Interceptor.Generator.Tests/DiagnosticTests.cs
git commit -m "fix: drop interface properties that would emit no usable accessor

A property whose only accessible accessor is init passed the reachability
guard, because the init accessor is accessible, and then emitted both accessor
lambdas as null: a key in Properties that exists and does nothing. The guard
now asks whether an accessor can actually be emitted."
```

---

### Task 4: NI0005 and NI0008 become errors

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Diagnostics.cs:52-59` and `:82-92`
- Modify: `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md`
- Modify: `src/Namotion.Interceptor.Generator.Tests/DiagnosticTests.cs`, `VirtualPartialTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: NI0005 and NI0008 at `DiagnosticSeverity.Error`, relied on by Task 5's precedence assertion.

- [ ] **Step 1: Update the existing severity assertion so it fails**

In `DiagnosticTests.cs`, in `WhenDerivedRedeclaresBaseImplementedProperty_ThenNI0005IsReported`, change:

```csharp
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
```

to:

```csharp
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenDerivedRedeclaresBaseImplementedProperty"
```

Expected: FAIL, `Assert.Equal() Failure: Expected Error, Actual Warning`.

- [ ] **Step 3: Change both descriptors**

In `Diagnostics.cs`, for `ShadowsBaseImplementation` change `defaultSeverity: DiagnosticSeverity.Warning` to `defaultSeverity: DiagnosticSeverity.Error`, and rewrite the message and description so they name the remedy:

```csharp
    public static readonly DiagnosticDescriptor ShadowsBaseImplementation = new(
        id: "NI0005",
        title: "Property re-declares a member already implemented by the base class",
        // Two sentences, so RS1032 requires the trailing period.
        messageFormat: "'{0}' re-declares '{1}' without taking the interface slot, so the subject and the interface report different values forever. Re-list the interface in the class's base list, or rename the property when its type differs from the interface member's.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Reading through the interface resolves to the base class implementation, not this property. C# fixes the interface map at the class that declares the interface, so a property further down takes the slot only when that class re-lists the interface, which is what every generated subject does for IInterceptorSubject.");
```

For `PropertyNameCollision` change `defaultSeverity: DiagnosticSeverity.Warning` to `defaultSeverity: DiagnosticSeverity.Error`. Leave its message alone; it already names the winner and the dropped member.

- [ ] **Step 4: Update the release tracking table**

In `AnalyzerReleases.Unshipped.md`, change the `Severity` column from `Warning` to `Error` for the NI0005 and NI0008 rows. Nothing is in `AnalyzerReleases.Shipped.md`, so no "Changed Rules" section is needed.

- [ ] **Step 5: Fix the tests that now see an error**

Two categories:

Tests asserting NI0005 or NI0008 is reported keep working, since they assert on `Id`. Only the severity assertion above changed.

Tests whose *source* trips one of these rules now fail compilation instead of warning. Find them:

```bash
grep -rn "NI0005\|NI0008" src/Namotion.Interceptor.Generator.Tests/
```

For each hit in `VirtualPartialTests.cs` and `ExplicitInterfaceBehaviorTests.cs`, check whether the test calls a `RunExpecting...` helper that fails on errors. Where a test intends to exercise the shape rather than the diagnostic, add `#pragma warning disable NI0005` around the offending declaration in the embedded source, matching what `ExplicitInterfaceBehaviorTests.cs` already does. Where a test asserts the diagnostic, leave it.

- [ ] **Step 6: Run the full unit suite**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: all pass. If a *production* project fails to build, a real subject in this repository is in one of these shapes; fix the shape by re-listing the interface, and note it for the pull request description.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Generator/ src/Namotion.Interceptor.Generator.Tests/
git commit -m "fix: reject a property that fails to take its interface slot

NI0005 and NI0008 both describe one subject property name with two readers,
where the losing member is silently unreachable and nothing in the declaration
reveals it. Both become errors, and NI0005's message now names the remedy:
re-list the interface so the declaration takes the slot."
```

---

### Task 5: NI0015 for a declaration displacing an ancestor subject's property

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Diagnostics.cs` (append the descriptor)
- Modify: `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs` (`Extract`, and `ReportPropertiesShadowingABaseImplementation`)
- Modify: `src/Namotion.Interceptor.Generator.Tests/DiagnosticTests.cs`

**Interfaces:**
- Consumes: `SubjectAncestry.HasInterceptorSubjectAttribute(INamedTypeSymbol? type)` returning `bool`; `SymbolExtensions.EnumerateChain(INamedTypeSymbol? type)` returning `IEnumerable<INamedTypeSymbol>`, which stops before `System.Object` and yields the passed type first; `IsNeverASubjectProperty(IPropertySymbol property)` returning `bool`, a private static already in `SubjectMetadataExtractor.cs`. All exist today.
- Produces: `Diagnostics.DisplacesAncestorSubjectProperty`, id `NI0015`, severity `Error`, two format arguments: the subject's display string and the property name.

- [ ] **Step 1: Write the failing tests**

Add to `DiagnosticTests.cs`:

```csharp
    [Fact]
    public void WhenSubjectHidesAnAncestorSubjectProperty_ThenNI0015IsReported()
    {
        // Arrange: two backing fields under one property name. Both are intercepted, writes through
        // either raise a change under the same key, and the metadata can only read one of them.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class BaseSubject
    {
        public partial string Origin { get; set; }
    }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject
    {
        public new partial string Origin { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0015");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void WhenSubjectExplicitlyImplementsAnAncestorSubjectPropertyName_ThenNI0015IsReported()
    {
        // Arrange: the explicit implementation lands in the highest precedence tier and would flip the
        // ancestor's intercepted property to a non-intercepted interface read.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IKind { string Kind { get; } }

    [InterceptorSubject]
    public partial class BaseSubject
    {
        public partial string Kind { get; set; }
    }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject, IKind
    {
        string IKind.Kind => ""explicit"";
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0015");
    }

    [Fact]
    public void WhenSubjectHidesAPlainBaseClassProperty_ThenNI0015IsNotReported()
    {
        // Arrange: a plain base contributes nothing to any DefaultProperties, so exactly one subject
        // property exists and it is the derived one.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public class PlainBase { public string Origin => ""base""; }

    [InterceptorSubject]
    public partial class DerivedSubject : PlainBase
    {
        public new partial string Origin { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(generated.GeneratorDiagnostics, d => d.Id == "NI0015");
    }

    [Fact]
    public void WhenSubjectOverridesAnAncestorSubjectProperty_ThenNI0015IsNotReported()
    {
        // Arrange: an override shares one slot and one backing field, so there is nothing to displace.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class BaseSubject
    {
        public virtual partial string Origin { get; set; }
    }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject
    {
        public override partial string Origin { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(generated.GeneratorDiagnostics, d => d.Id == "NI0015");
    }

    [Fact]
    public void WhenAPlainClassBetweenTwoSubjectsDeclaresTheName_ThenNI0005IsReportedAndNI0015IsNot()
    {
        // Arrange: the plain class contributes to no DefaultProperties, so the conflict belongs to the
        // ancestor subject's adopted interface default and the remedy is re-listing the interface.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasLevel { int Level => 0; }

    [InterceptorSubject]
    public partial class Machine : IHasLevel { }

    public class Plain : Machine { public int Level => 1; }

    [InterceptorSubject]
    public partial class Pump : Plain
    {
        public partial int Level { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(generated.GeneratorDiagnostics, d => d.Id == "NI0015");
        Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0005");
    }
```

`GeneratorTestHost.Run` is used rather than `RunExpectingCleanCompilation` because these sources are expected to produce generator errors.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~NI0015"
```

Expected: the two positive tests FAIL with "The collection was expected to contain a single element" (no NI0015 exists yet). The three negative tests pass vacuously.

- [ ] **Step 3: Add the descriptor**

Append to `Diagnostics.cs`, after `HijacksInterfaceImplementation`:

```csharp
    public static readonly DiagnosticDescriptor DisplacesAncestorSubjectProperty = new(
        id: "NI0015",
        title: "Property displaces a property an ancestor subject already exposes",
        // Two sentences, so RS1032 requires the trailing period.
        messageFormat: "'{0}' declares '{1}', which displaces the property an ancestor subject already exposes under that name, leaving two members and one key. Make the ancestor property virtual and override it, or rename one of them.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Subject properties are keyed by simple name, so only one of the two members is reachable through the metadata while both remain writable through a differently typed reference, and both raise changes under the same name.");
```

Add the row to `AnalyzerReleases.Unshipped.md`:

```
NI0015 | Namotion.Interceptor | Error | Property displaces a property an ancestor subject already exposes
```

- [ ] **Step 4: Implement the scan**

In `SubjectMetadataExtractor.cs`, add this method next to `ReportPropertiesShadowingABaseImplementation`:

```csharp
    /// <summary>
    /// A declaration in the subject's own class that displaces a property an ancestor subject already
    /// contributes to its DefaultProperties. Reported by effect rather than by the 'new' keyword,
    /// because omitting the keyword is only CS0108 and the displacement is identical either way.
    /// </summary>
    /// <remarks>
    /// Restricted to ancestors carrying [InterceptorSubject]. A hand-written base satisfying the base
    /// contract has a DefaultProperties whose contents no symbol query can reveal, so it is left alone
    /// rather than guessed at, and a plain class contributes nothing at all.
    /// </remarks>
    private static HashSet<string> ReportPropertiesDisplacingAnAncestorSubject(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var reported = new HashSet<string>();

        var subjectAncestors = SymbolExtensions
            .EnumerateChain(typeSymbol.BaseType)
            .Where(SubjectAncestry.HasInterceptorSubjectAttribute)
            .ToList();

        if (subjectAncestors.Count == 0)
        {
            return reported;
        }

        foreach (var property in classProperties)
        {
            // An override shares the slot and the backing field it already had, so it displaces
            // nothing. An explicit implementation is deliberately NOT skipped here: it lands in the
            // highest precedence tier and would flip an ancestor's intercepted property to a
            // non-intercepted interface read.
            if (property.IsOverride || !reported.Add(property.Name))
            {
                continue;
            }

            var isDisplacing = subjectAncestors.Any(ancestor => ancestor
                .GetMembers(property.Name)
                .OfType<IPropertySymbol>()
                .Any(candidate => !IsNeverASubjectProperty(candidate) && !candidate.IsAbstract));

            if (isDisplacing)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.DisplacesAncestorSubjectProperty, location,
                    typeSymbol.Name, property.Name));
            }
            else
            {
                reported.Remove(property.Name);
            }
        }

        return reported;
    }
```

Wire it into `Extract`, replacing:

```csharp
        ReportPropertiesShadowingABaseImplementation(typeSymbol, classProperties, location, diagnostics);
```

with:

```csharp
        // NI0015 runs first and its names are handed to NI0005, which stands down on them: the two
        // rules are not mutually exclusive, and NI0015 is both the more severe failure and the one
        // whose remedy subsumes the other's.
        var displacedNames = ReportPropertiesDisplacingAnAncestorSubject(
            typeSymbol, classProperties, location, diagnostics);

        ReportPropertiesShadowingABaseImplementation(
            typeSymbol, classProperties, displacedNames, location, diagnostics);
```

Change `ReportPropertiesShadowingABaseImplementation`'s signature to take the extra parameter:

```csharp
    private static void ReportPropertiesShadowingABaseImplementation(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties,
        HashSet<string> displacedNames,
        Location location,
        List<Diagnostic> diagnostics)
```

and extend its per-property skip from:

```csharp
            if (property.ExplicitInterfaceTypeName is not null || property.IsOverride)
            {
                continue;
            }
```

to:

```csharp
            if (property.ExplicitInterfaceTypeName is not null ||
                property.IsOverride ||
                displacedNames.Contains(property.Name))
            {
                continue;
            }
```

Add `using System.Collections.Generic;` if it is not already in the file.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~NI0015"
```

Expected: all five PASS.

- [ ] **Step 6: Run the full unit suite**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: all pass and no snapshots move. NI0015 fires nowhere in this repository today, so any new error is a genuine finding: report it rather than suppressing it.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Generator/ src/Namotion.Interceptor.Generator.Tests/
git commit -m "feat: reject a declaration that displaces an ancestor subject's property

Two backing fields under one property name, both intercepted, both raising
changes under the same key while the metadata can read only one. Precedence
can pick a winner but cannot make the loser safe, so the shape is rejected.
Reported by effect rather than by the 'new' keyword, since omitting it is only
CS0108 and the displacement is identical either way. An override shares one
slot and is exempt; a plain base contributes nothing and is exempt."
```

---

### Task 6: Documentation

**Files:**
- Modify: `docs/generator.md`
- Modify: `docs/design/generator-supported-shapes.md`
- Modify: `docs/subject-guidelines.md`

**Interfaces:**
- Consumes: the behaviour established in Tasks 1 to 5.
- Produces: nothing.

- [ ] **Step 1: Update `docs/generator.md`**

Four edits.

Replace the note under "Interface Default Properties" that reads "**Note:** If a class implements a property that an interface also provides as a default, the class implementation takes precedence." with:

```markdown
**Precedence across a hierarchy**, highest first: the subject's own declarations, then everything it inherits from its base subject, then the interface default implementations it adopts. An adopted default is a fallback, so a real property declared anywhere in the chain beats it, including one declared above the subject that adopted the interface.
```

Rewrite the "New and Sealed Properties" section. Its current example fails to build under NI0005, and its claim that `new` silences the accompanying warning is wrong. The example becomes:

```csharp
public interface IHuman { string Origin { get; } }
public class BaseSubject : IHuman { public string Origin => "base"; }

[InterceptorSubject]
public partial class DerivedSubject : BaseSubject, IHuman
{
    // "new" hides BaseSubject.Origin and silences CS0108. Re-listing IHuman is what makes this
    // property the interface implementation; without it, reading through IHuman returns "base"
    // forever and the generator reports NI0005.
    public new partial string Origin { get; set; }
}
```

Add, in the same section, that `new` over a plain base class is supported, while `new` over an ancestor that is itself a subject is NI0015, because two intercepted backing fields cannot share one property name.

In the diagnostics table, change the NI0005 row's severity to Error and replace its remedy with "Re-list the interface in the class's base list so the property takes the slot; rename the property when its type differs from the interface member's". Change the NI0008 row's severity to Error. Add:

```markdown
| NI0015 | Error | A subject declares a property, or an explicit interface implementation, whose name an ancestor subject already exposes. Two members then share one key: the metadata can read only one, both stay writable through a differently typed reference, and both raise changes under that name. Not reported for an `override`, which shares one slot, nor when the hidden member is on a plain class, which contributes nothing to any property set | Make the ancestor property `virtual` and `override` it, or rename one of the two |
```

Update the suppression guidance paragraph, which currently lists NI0005 and NI0008 among rules that suppression genuinely resolves. They are errors now, and suppressing either leaves an unreachable property in place.

Add a "Hierarchy Hazards" subsection:

```markdown
### Hiding `RaisePropertyChanged` swallows change notifications

Generated property setters call the inherited helper by simple name, and C# hides by name, so a derived subject declaring its own `RaisePropertyChanged` captures every call emitted in that class:

```csharp
[InterceptorSubject]
public partial class Swallows : Root
{
    public partial string LeafName { get; set; }

    protected new void RaisePropertyChanged(string propertyName)
    {
    }
}
```

`LeafName` then raises nothing, while properties declared on `Root` still raise, because their setters were emitted in `Root` and bound there. The values stay correct, so nothing looks wrong until a binding or a connector stops updating. A body that forwards to `base.RaisePropertyChanged(propertyName)` behaves correctly, which is why no diagnostic exists: the two differ by one line and separating them needs dataflow analysis. Do not hide this member.
```

- [ ] **Step 2: Update `docs/design/generator-supported-shapes.md`**

Delete the first two bullets under "Hierarchy gaps found while reviewing the per hierarchy interception members" (the inverted merge, and the `new` property with a differing type), and the "Known gaps" bullet about the `init`-only interface property. All three are fixed.

Add a section recording the implemented design: the three tiers and why an adopted interface default ranks last (a subject that declares nothing re-injects it over an ancestor's real property); the `DistinctBy` choice and the measured fact that `FrozenDictionary` does not preserve insertion order in general, so no part of the design may rest on enumeration order; the diagnostic policy that a shape whose defect is invisible from the declaration must not compile; and why discriminating NI0005 from NI0015 by which class declares the name was rejected, with the plain-class-between-two-subjects counterexample.

Add to "Language semantics this design depends on":

```markdown
**A property declared in a derived class does not take an interface slot bound further up.** C# fixes the interface map at the class that declares the interface, so a same-named property below it is an unrelated member until that class re-lists the interface.

```
Pump      instance=42  ((IHasLevel)i)=0   slot implemented by IHasLevel.get_Level
GoodPump  instance=42  ((IHasLevel)i)=42  slot implemented by GoodPump.get_Level
```

This is why NI0005's remedy is re-listing the interface, and it is the same rule that forces every generated subject to re-list `IInterceptorSubject`.

**`Type.GetProperty(name, Public | NonPublic | Instance)` throws `AmbiguousMatchException`** when a `new` property's type differs from the one it hides. `DeclaredOnly` resolves it; the same-type case resolves on its own through the hiding rule.
```

- [ ] **Step 3: Update `docs/subject-guidelines.md`**

Add guidance that a subject declaring a property an ancestor exposes through an interface must re-list that interface in its base list, and that hiding an ancestor subject's property with `new` is not supported; use `virtual` and `override`.

- [ ] **Step 4: Verify style**

```bash
grep -rn "—" docs/generator.md docs/design/generator-supported-shapes.md docs/subject-guidelines.md
```

Expected: no output. Confirm no paragraph was hard wrapped at a column.

- [ ] **Step 5: Commit**

```bash
git add docs/
git commit -m "docs: document subject property precedence and the new diagnostics

Records the three-tier precedence rule, corrects the interface-default note
that only held within a single class, fixes the New and Sealed Properties
example which no longer builds, and adds NI0015 plus the RaisePropertyChanged
hazard."
```

---

## Final verification

- [ ] **Run everything**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: all pass.

- [ ] **Confirm the four intended snapshot moves and no others**

```bash
git diff --stat master -- "*.verified.txt"
```

Expected: the three generator snapshots and one registry snapshot named in Task 2 Step 6, plus the class-branch `DeclaredOnly` change across the snapshots touched in Task 1. No `InterfaceDefaultPropertyTests` snapshot content change beyond `DeclaredOnly`.

- [ ] **Write the pull request description**

Open `.github/pull_request_template.md` and fill it in. Title prefix `fix:`. Apply an `area:` and a `type:` label at creation with `gh pr create --label`, choosing from `gh label list`.

The description must carry full migration information:

- What was impossible and now works: a derived subject can override an inherited property name; `override` now reports the overriding declaration's own attributes, so a validation attribute written on an override finally takes effect; a `new` property whose type differs from the one it hides no longer throws at first `Properties` access.
- What was possible and is not now: a property that fails to take its interface slot (NI0005) and a within-class name collision (NI0008) are errors rather than warnings; a declaration displacing an ancestor subject's property is a new error (NI0015).
- What silently changed: `Properties` now reports the most derived declaration for any re-declared name, which changes the resolved entry for every `override` in a consuming codebase.
- Remedies: for NI0005, re-list the interface in the class's base list, or rename the property when its type differs from the interface member's, since re-listing is then CS0738. For NI0015, make the ancestor property `virtual` and `override` it, or rename.
- A note that consumers who worked around the inverted precedence by calling `AddProperties` in a constructor can now delete that call. It still works, since dynamic properties win, but it allocates a per-instance `FrozenDictionary` on every subject and uses runtime reflection.
- A note that a project whose models are emitted into files Roslyn treats as generated (`.g.cs`, `.generated.cs`, or an `<auto-generated>` header) will not see any of these diagnostics, including the new errors.

---

## Self-review

**Spec coverage.** Section 1 three-tier precedence is Task 2. Section 2 diagnostics: NI0005 and NI0008 severities are Task 4, NI0015 and the precedence between the rules are Task 5. Section 3 unambiguous `PropertyInfo` lookup is Task 1. Section 4 degenerate interface entries is Task 3. The spec's Testing section maps onto Task 2 Steps 1 and 6, Task 3 Step 1, and Task 5 Step 1. The spec's Documentation section is Task 6. The spec's pull request requirements are the final verification block.

**Placeholders.** None. Every code step carries the code, every command carries its expected output. `GeneratorRunResult.Sources` is `IReadOnlyList<GeneratedSourceResult>`, so Task 3 Step 1 reads generated text as `generated.Sources.Single().SourceText.ToString()`, matching how `DiagnosticTests.cs` already asserts on `generated.Sources`.

**Type consistency.** `Diagnostics.DisplacesAncestorSubjectProperty` is named identically in Task 5 Steps 3 and 4. `ReportPropertiesDisplacingAnAncestorSubject` returns `HashSet<string>` and `ReportPropertiesShadowingABaseImplementation` takes `HashSet<string> displacedNames`, matching the call site. `EmitPropertyDictionary(StringBuilder, SubjectMetadata, IReadOnlyList<PropertyMetadata>)` is called with three arguments at all four call sites in Task 2 Step 3. Task 1's `DeclaredOnly` line is reproduced verbatim inside Task 2's rewritten method, so Task 2 cannot silently revert it.
