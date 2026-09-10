# Subject property precedence across a hierarchy

Status: proposed
Date: 2026-09-10

## Problem

A derived subject cannot override a property name it inherits. Whatever it inherited wins, and the subject's own declaration is silently discarded from `IInterceptorSubject.Properties`.

Reproduced on current master with three types:

```csharp
public interface IHasLevel
{
    int Level => 0;
}

[InterceptorSubject]
public partial class Machine : IHasLevel
{
}

[InterceptorSubject]
public partial class Pump : Machine
{
    [Source("device/level")]
    public partial int Level { get; set; }
}
```

```
pump.Level                  = 42
Properties["Level"] ->
   declared on              = IHasLevel   (not Pump)
   value read through it    = 0           (not 42)
   can write through it     = False
   IsIntercepted            = False
   [Source] found           = <none>
```

Every consumer that goes through the property metadata (the registry, connectors, validation, tracking) sees a property hardwired to `0`, with no setter, not intercepted, carrying none of the declaring property's attributes. Direct C# access to `pump.Level` returns `42`. The two views disagree permanently.

The reported symptom was that a source-binding attribute declared on the leaf property never took effect, because `GetAllProperties()` returned a `RegisteredSubjectProperty` whose `PropertyInfo` is the interface member and therefore does not carry it. That is downstream of the same defect.

## Root cause

`EmitDefaultProperties` emits the subject's own entries first and appends the base's afterwards:

```csharp
new Dictionary<string, SubjectPropertyMetadata> { /* own entries */ }
    .Concat(global::Base.DefaultProperties)
    .ToFrozenDictionary();
```

`ToFrozenDictionary` keeps the last entry for a duplicate key, so the base overwrites the subject's own. **Inheritance precedence is inverted at every level: what a subject inherited beats what it declared.**

Recorded as a known gap in `docs/design/generator-supported-shapes.md` under "Hierarchy gaps found while reviewing the per hierarchy interception members", which also notes that reversing the concatenation order would fix it. That is necessary but not sufficient.

### Reversing the order alone moves the bug down a level

`ExtractInterfaceDefaultProperties` skips an interface default implementation only when *that class* declares the name. It never looks at what the class inherited. A subject below the one that declared the real property therefore re-injects the interface default:

```csharp
[InterceptorSubject] public partial class SmartPump : Pump
{
    public partial string Label { get; set; }
}
```

generates

```csharp
["Label"] = ... typeof(SmartPump) ...
["Level"] = new SubjectPropertyMetadata(          // injected again
    typeof(IHasLevel).GetProperty(...), (o) => ((IHasLevel)o).Level, null,
    isIntercepted: false, isDynamic: false),
}
.Concat(global::Pump.DefaultProperties)
```

With a plain reversal, `SmartPump`'s own interface entry would beat `Pump`'s real property.

### Affected shapes

| Shape | Diagnostic today | Result today |
|---|---|---|
| Leaf declares a name provided by an interface default adopted by an ancestor, does not re-list the interface | NI0005 warning | interface entry wins, not intercepted, attributes lost |
| Same, but the leaf re-lists the interface | none | interface entry still wins, not intercepted, attributes lost |
| Leaf hides an ancestor subject's own property with `new` | none | ancestor entry wins; metadata reads and writes the ancestor's backing field while the leaf's property uses its own |
| Leaf `override`s a `virtual` ancestor property | none | ancestor entry wins; the value is correct through virtual dispatch, but `DeclaringType` and the overriding declaration's own attributes come from the ancestor, so a `[MaxLength(5)]` written on the override is never seen |

The second row is why the reported workaround was necessary. Re-listing the interface is the correct C# fix and does not help, because the base's `DefaultProperties` wins regardless of what the C# interface map says.

## Design

### 1. Three-tier precedence

An interface default implementation is a fallback and must rank below anything inherited. A subject's own declaration must rank above everything.

1. own class-declared properties, including explicit interface implementations declared in the subject's own class (highest)
2. inherited `base.DefaultProperties`
3. own interface default implementations (lowest)

Emitted as a precedence-ordered sequence deduplicated keeping the first occurrence:

```csharp
public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
    new Dictionary<string, SubjectPropertyMetadata> { /* own declarations */ }
        .Concat(global::Base.DefaultProperties)
        .Concat(new Dictionary<string, SubjectPropertyMetadata> { /* own interface defaults */ })
        .DistinctBy(pair => pair.Key)
        .ToFrozenDictionary();
```

The partition needs no new analysis. `PropertyMetadata.IsFromInterface` has exactly two construction sites, both passing the flag as a literal, and `metadata.Properties` is already ordered class-declared then interface-derived. A class-declared explicit implementation carries `IsFromInterface: false` and therefore lands in tier 1, which is intended: it is a declaration in the subject's own class.

Emit a tier only when it is non-empty, and keep the root case (no subject base) as a single dictionary with no `Concat` and no `DistinctBy`.

`EmitProperties` and the accessor branch of `EmitDefaultProperties` group by `IsFromInterface || ExplicitInterfaceTypeName is not null`, which is a *different* partition from the tier partition. If the implementation replaces `SubjectMetadata.Properties` with two lists rather than partitioning at the emit site, those two sites must still iterate tier 1 only.

Why `DistinctBy` rather than relying on last-wins:

- For a subject with no interface default implementations the key set and the first-occurrence order are identical before and after, so the frozen layout is unchanged whichever internal implementation `ToFrozenDictionary` selects. That is the overwhelming majority of subjects, and it is what keeps snapshots and order-sensitive consumers from churning.
- Precedence stops depending on `ToFrozenDictionary`'s duplicate handling, which is not documented. After `DistinctBy` no duplicate reaches it.
- The emitted code states the precedence rule rather than implying it.

Note that `FrozenDictionary` does **not** preserve insertion order in general. It selects an implementation from the key set, and order survives only for some of them. Measured:

```
8 realistic keys       LengthBucketsFrozenDictionary                 preserved=True
wide length spread     LengthBucketsFrozenDictionary                 preserved=True
14 same-length keys    OrdinalStringFrozenDictionary_LeftJustified   preserved=False
```

Fourteen same-length property names is ordinary in a generated model, so no part of this design may rest on enumeration order. `Type.GetProperties` order is likewise documented as unspecified. The argument above is deliberately about the layout being *unchanged*, not about it being ordered.

`DistinctBy` needs .NET 6 or later, which `ToFrozenDictionary` already forces, so no target framework changes. Its default comparer matches `ToFrozenDictionary()`'s, so there is no comparer mismatch.

`AddProperties` keeps its existing last-wins concat. Dynamic properties overriding defaults is a separate and deliberate rule.

### 2. Diagnostics

Policy: **fail loudly for anything the library cannot fully support, and prefer over-constraining to hiding.** A shape whose defect is invisible from the declaration must not compile.

Every change below is strictly more constrained than master. NI0005's trigger is unchanged, so the same shapes are caught; only its severity moves. Nothing that is reported on master becomes silent.

#### NI0005 raised to Error, trigger unchanged

Trigger stays exactly as on master: the subject declares a property, an interface in `AllInterfaces` that is also in `baseType.AllInterfaces` has a member of that name, and `FindImplementationForInterfaceMember` resolves outside the subject.

Keeping the trigger matters for more than compatibility. It is a direct symbol query. It needs no knowledge of which ancestor contributed a key, no re-derivation of any ancestor's property set, and it resolves the dotted name of an explicit implementation declared inside an interface correctly. Every alternative trigger considered required re-deriving an ancestor's contribution, which cannot be done soundly for a hand-written base, for a base built by a different generator version, or for an interface default that `ExtractInterfaceDefaultProperties` silently skipped as inaccessible. An error that rests on a re-derivation which can be wrong would break correct builds.

Severity: Error. The subject's declaration does not take the interface slot, so the subject and the interface report different values forever, and nothing in the declaration reveals it. In the interface-default case the shape needs no modifier at all and produces no compiler diagnostic.

Remedy, which the message must name: re-list the interface in the subject's base list, which makes the subject's property the real implementation and leaves exactly one reader in every view. Where the property type differs from the interface member's, re-listing is CS0738 and the remedy is renaming.

Re-listing an interface is the library's own convention: every generated derived subject re-lists `IInterceptorSubject` for exactly this reason, because an explicit interface implementation in a derived class does not take the slot otherwise (CS0540).

Not reported when the subject re-lists the interface. That shape is correct and stays silent and legal.

#### NI0015, new, Error

Trigger: a subject's own declaration displaces a property an **ancestor subject** contributes. Two forms, both reported:

- a class-declared property colliding with a name an ancestor subject contributes, detected by effect rather than by the `new` keyword so the CS0108 spelling is caught too;
- an explicit interface implementation in the subject's own class whose simple name collides with an ancestor subject's intercepted property, which under tier 1 would silently flip that property from intercepted to a non-intercepted interface read.

Scoped to ancestors carrying `[InterceptorSubject]`. A hand-written base satisfying the base contract has a `DefaultProperties` whose contents the generator cannot see, so no diagnostic is reported for it rather than a guessed one. Hiding a plain class's property promotes nothing and stays legal and silent.

Not reported for an `override`, which shares one slot and one backing field.

Remedy: make the ancestor property `virtual` and `override` it, or rename.

#### NI0008 raised to Error

Two members provide one simple property name, the generator drops one, and the message already says which. A property the author declared that silently does not exist is not a warning-grade fact, and it is the same "one key, two readers" failure as NI0015 reached from within a single class.

#### Precedence between the rules

NI0005 and NI0015 are **not** mutually exclusive. A subject can hide an ancestor subject's property that also implements an interface, and a plain class between two subjects can declare the name without contributing anything, which makes any attempt to discriminate by "who declared the name" report the wrong rule with the wrong remedy.

Rule: when both apply, report NI0015 only. It is the more severe failure (two intercepted members, two backing fields) and its remedy subsumes the other.

`AnalyzerReleases.Unshipped.md` gets NI0015 added and the NI0005 and NI0008 severities changed. `AnalyzerReleases.Shipped.md` is empty, so no "Changed Rules" section is needed.

### 3. Unambiguous `PropertyInfo` lookup

`EmitDefaultProperties` emits `typeof({ClassName}).GetProperty(nameof({Name}), Public | NonPublic | Instance)`. Measured:

```
DeclaredOnly=False -> AmbiguousMatchException
DeclaredOnly=True  -> DiffType.Origin : Int32
```

When a `new` property's type differs from the one it hides, the first `Properties` access throws `TypeInitializationException`. Adding `BindingFlags.DeclaredOnly` fixes it. Every property reaching that branch comes from `CollectProperties` scanning the subject's own declarations, so `DeclaredOnly` is exactly right and stays O(1); filtering on `DeclaringType` would mean a `GetProperties()` scan per property at type init.

This is load-bearing rather than optional. NI0015 makes the crash unreachable over a subject ancestor only because the build fails, and the generator emits code even when it reports an error, so a consumer who suppresses NI0015 gets the crash back. It is also reachable over a plain base, which this change documents as supported.

### 4. Degenerate interface property entries

An interface property whose only accessible accessor is `init`, such as `{ protected get; init; }`, currently produces an entry with both accessor lambdas null. The key exists in `Properties` and reading or writing it does nothing observable, which is a phantom property.

The cause is that the reachability guard asks about raw accessor accessibility:

```csharp
if (!isGetterAccessible && !isSetterAccessible)
{
    continue;
}
```

while what decides whether anything can be emitted is computed further down:

```csharp
var hasGetter = property.GetMethod != null && isGetterAccessible;
var hasSetter = property.SetMethod is { IsInitOnly: false } && isSetterAccessible;
```

An `init` accessor is accessible, so the guard passes, and then `hasSetter` is false because `IsInitOnly` excludes it. The emitter writes a lambda only for `HasGetter` and `HasSetter`, so both come out null. `HasInit` is dead on this path: it is consulted only when emitting a partial property's own accessor, and an interface default is never partial.

Move the guard below those computations and ask `!hasGetter && !hasSetter`. The only entries this removes are ones where both lambdas are null, so nothing that works today is skipped.

Skipped in silence, matching the existing treatment of interface members generated code cannot reach: the resulting model is truthful, the property is simply absent rather than present and inert, and the interface may be third-party with no remedy available to the subject's author.

## Consequences

`override` gains correct metadata. The overriding declaration's own attributes become visible, so a `[MaxLength(5)]` written on an override is finally seen by validation. Attributes declared on the *base* property were already inherited, because `PropertyInfoExtensions` reads them with `inherit: true`, so this adds to the attribute set rather than relocating it. `DeclaringType` also changes, from the ancestor to the overriding class.

The reported workaround becomes deletable. Beyond the redundancy, an `AddProperties` call in a constructor allocates a per-instance `FrozenDictionary` on every subject, which defeats the shared static `DefaultProperties` entirely, and `this.GetType().GetProperty(...)` is runtime reflection in a library targeting Native AOT. It keeps working if it is not removed, because dynamic properties still win, so there is no forced migration.

## Breaking changes

1. **Inherited entries no longer win.** Any subject that re-declares an inherited property name changes which metadata `Properties` reports. This is the fix, and for `override` it changes behaviour for code that never opted in.
2. **NI0005 becomes an error.** Same trigger as master, so the affected source is exactly the source that warns today. Under `TreatWarningsAsErrors` those builds already fail.
3. **NI0008 becomes an error.**
4. **NI0015 is a new error.** Source that compiled silently now fails.

Pre-1.0, approved.

## Testing

Following the three layers in `docs/design/generator-supported-shapes.md`:

1. Verify snapshots for the emitted shape: root, derived, derived with interface defaults, derived with an empty tier. The four `InterfaceDefaultPropertyTests` snapshots are root subjects with no base concat and must **not** move, which checks that the root case kept its single-dictionary form.
2. Compile-clean assertions for every new shape, plus diagnostic tests asserting each rule fires and does not fire on each row of the affected-shapes table, on the plain-class-between-two-subjects shape, and on the re-listed-interface shape.
3. Real subject models in the test project, exercised at runtime: the `Machine`/`Pump`/`SmartPump` chain asserting the leaf's property wins three levels down; the re-listed-interface shape; the `override` shape asserting the override's own attributes are visible; the `new`-over-a-plain-base shape asserting no diagnostic and no `TypeInitializationException` when the types differ; and an interface with `{ protected get; init; }` asserting the key is absent from `Properties` rather than present and inert.

**NI0015 fires nowhere in this repository.** Both existing `new partial` subjects sit over plain bases, which the rule exempts, so it ships with no real-code validation and its tests carry the whole weight. The explicit-implementation form in particular has no precedent anywhere and needs its own runtime test.

Existing NI0005 tests keep their triggers and need only their expected severity updated: `DiagnosticTests` `WhenDerivedRedeclaresBaseImplementedProperty_ThenNI0005IsReported` and `WhenDerivedRedeclaresPropertyTheBaseClassItselfImplements_ThenNI0005IsReported`, and two assertions in `VirtualPartialTests`.

Run `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`. Expected snapshot movement, to be inspected individually rather than accepted wholesale, because an inverted precedence would also produce a plausible-looking diff:

- `Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithInheritance_ThenPartialClassIsGenerated.verified.txt`
- `Generator.Tests/Snapshots/SourceGeneratorTests.WhenGeneratingClassWithInheritanceAndCustomAttribute_ThenBasePropertiesAreIncluded.verified.txt`
- `Generator.Tests/Snapshots/VirtualPartialTests.Test_VirtualInheritanceChain_GeneratesCorrectly.verified.txt`
- `Registry.Tests/SubjectRegistryTests.WhenCreatingSubjectWithInheritance_ThenAllPropertiesAreAvailable.verified.txt`, a bare ordered name list

`override` metadata changes in production code, six properties across `Namotion.Devices.Philips.Hue` (`HueLightbulb`, `HueButtonDevice`, `HueMotionDevice`). All of them repeat `[Derived]` on the override, so none loses an attribute, but each should be confirmed.

## Documentation

- `docs/generator.md`: rewrite the "Note: If a class implements a property that an interface also provides as a default, the class implementation takes precedence" line, which is currently true only within a single class, to state the three-tier rule for a hierarchy. Rewrite "New and Sealed Properties": its `DerivedSubject : BaseSubject` example now fails with NI0005 and must re-list `IHuman`, and the sentence claiming `new` silences the warning accompanying NI0005 is wrong today (a test proves it does not). Separate `new` over a plain base with no interface involved, which stays supported and silent, from `new` over a subject ancestor, which is NI0015. Correct the NI0005 row, whose remedy of "add `new`" does not fix the divergence and is CS0109 when the ancestor's implementation is an interface default. Add the NI0015 row and update the NI0008 severity. Update the suppression guidance, which currently lists NI0005 and NI0008 as rules that suppression genuinely resolves. Add a "Hierarchy Hazards" subsection for hiding `RaisePropertyChanged`, with the repro and the note that a body forwarding to `base` is correct.
- `docs/design/generator-supported-shapes.md`: move the first two "Hierarchy gaps" entries into a section describing the implemented precedence, since both are fixed here. Record the diagnostic policy and the reason the discrimination-by-declaring-class approach was rejected. Add the `DistinctBy` decision and the measured `FrozenDictionary` ordering fact. Add the two verified language facts (interface re-implementation requires re-listing, and `GetProperty` ambiguity on a differently-typed `new` with the `DeclaredOnly` remedy). Remove the "Known gaps" entry for the `init`-only interface property, which section 4 fixes.
- `docs/subject-guidelines.md`: add the guidance that a subject declaring a property an ancestor exposes through an interface must re-list that interface.

## Pull request description

Must carry full migration information: what was possible before and is not now, what was impossible and now works, and what silently changed. Specifically the four breaking changes, the `override` metadata change with its blast radius, the workaround that can be removed and why removing it matters for allocations and AOT, and the two remedies for NI0005 including the CS0738 case where only renaming works.

## Out of scope

### Hiding `RaisePropertyChanged`, documented rather than diagnosed

A derived subject that hides the inherited helper swallows every change notification for its own properties. No error, no warning, and the property values stay correct, so nothing looks wrong until a binding or a connector stops updating.

```csharp
[InterceptorSubject]
public partial class Root
{
    public partial string RootName { get; set; }
}

[InterceptorSubject]
public partial class Swallows : Root
{
    public partial string LeafName { get; set; }

    protected new void RaisePropertyChanged(string propertyName)
    {
    }
}
```

```
Swallows     raised: RootName
CallsBase    raised: RootName, LeafName
```

Generated setters call the helper by simple name, and C# hides by name, so `LeafName`'s setter binds to the hiding member. `RootName` survives because its setter was emitted in `Root`'s generated half and bound there at compile time, which is why the type looks half-functional rather than broken.

No rule is added. A variant whose body is `base.RaisePropertyChanged(propertyName);` is byte-identical apart from that line and behaves correctly, so separating the two needs dataflow analysis. The shape is also opt-in: omitting `new` is CS0108, which this repository treats as an error. This is a hazard consumers should not choose, not a shape the generator should try to police.

Documented in the "Hierarchy Hazards" section of `docs/generator.md`, alongside the two slot-taking hazards already there.

Should it ever be reconsidered, the sequencing matters: the generated method is `protected` and not `virtual`, so hiding is currently the only hook a consumer has, and rejecting it would remove the mechanism with nothing in its place. Emitting it `virtual` would have to come first, which changes every root subject's shape and touches the base class contract and NI0012. Note that an `override` forgetting its `base` call swallows notifications identically, so that move does not remove the hazard, it only gives a diagnostic somewhere to point.

### Other

The remaining "Hierarchy gaps" entries: the base with only a parameterized constructor, the plain intermediate class listing a subject interface, and `DefaultProperties` missing from the hiding table.

Making `((IHasLevel)instance).Level` agree with the subject is not possible here. That is C#'s interface map and only the declaring class's base list can change it. NI0005 tells the author to do so; the library cannot.
