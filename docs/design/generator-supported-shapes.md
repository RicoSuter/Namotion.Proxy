# Source Generator: Supported Shapes

This document explains why the source generator rejects, accepts, or works around specific C# shapes that it does not treat as obvious. For user-facing documentation, including the diagnostics table and examples, see [Source Generator](../generator.md). This document is for maintainers of `Namotion.Interceptor.Generator`.

## Overview

The generator turns a partial class carrying `[InterceptorSubject]` into interception members. Most of that mapping is mechanical and self-explanatory from the code. A handful of decisions are not: they were reached by measuring the compiler's actual behaviour, not by reasoning about the language spec in the abstract, and in one case by getting it wrong three times first. This document preserves that reasoning so it is not rediscovered by trial and error the next time one of these areas changes.

## Why records are not supported as subjects

`[InterceptorSubject]` on a `record` or `record struct` is a compile error (NI0003). This looks like an arbitrary restriction until you look at what the generator actually emits into a record.

Every subject gets an auto-property backing field for `IInterceptorSubject.Data` and `IInterceptorSubject.SyncRoot`, both initialised with `= new()`, plus a lazily created `_context` field assigned through `InterceptorExecutor.GetOrCreate(ref _context, this)`. Two measured consequences follow if the containing type is a record:

- **Records synthesise `Equals` over every instance field, including auto-property backing fields.** Since `Data` and `SyncRoot` are each initialised with `= new()`, every instance holds distinct references for both. No two record subjects are ever equal, not even two positional records constructed from identical arguments, because the synthesised equality check always finds these two fields unequal.
- **The synthesised copy constructor is a shallow field copy.** `with` produces a clone that shares the original's `Data` and `SyncRoot` references and copies `_context` verbatim. Because `_context` is bound to the original instance the first time it was created, writes through the clone drive the original subject once `_context` is non-null, which is any subject that has been used at all.

Both are fixable by declaring `Equals(T)`, `GetHashCode()`, and a copy constructor, which suppress the compiler-synthesised versions. So the blocker is not the mechanics, it is what the fixed versions would mean: a subject is mutable, reference-identified, and tracked by the registry by reference. Value equality over mutable tracked properties means `GetHashCode` changes whenever a property changes, which breaks any hash-based collection holding the subject. Supporting records is a feature with its own design surface (what does `with` mean for an attached subject, is it attached to the same parent), not a bug fix, which is why NI0003 treats it as a hard error rather than working around it.

## Why the accessibility rule is a symbol query, not a list

The generator emits code that reaches an interface default property or a class-declared explicit implementation by casting the subject to the declaring interface, for example `((IHuman)o).Gender`. Whether that cast-and-read compiles depends on whether the member is accessible from the generated code, which lives in the subject's own assembly, not the interface's. Getting this guard right took four attempts, and each one broke in a different direction. The sequence is worth knowing before touching `GetAccessorAccessibility` again.

1. **A hardcoded `Private`/`Protected`/`ProtectedAndInternal` check against the property's own accessibility, unconditionally.** This looked reasonable in isolation, but Roslyn reports `IPropertySymbol.DeclaredAccessibility` as `Private` for every explicit interface implementation, regardless of the implemented member's real visibility, because the CLR requires explicit implementations to be emitted private. The naive check therefore skipped every explicit implementation, which would have silently reverted the fix for issue #428 (the whole point of this branch). Caught by the existing explicit-implementation tests failing before the change was ever committed.
2. **Exempting explicit implementations from the check entirely** (commit `d128ad6f`). This fixed the regression above but overcorrected: an explicit implementation of a `protected` interface member was now kept and emitted unconditionally, producing generated code that fails with CS1540, since a protected member is only reachable through a type that derives from its declaring type, which generated code never does.
3. **Checking the accessibility of the implemented member instead of the implementation** (commit `6fd3f9ff`), still with the same hardcoded `Private`/`Protected`/`ProtectedAndInternal` list. This fixed the protected case but the hand-rolled list only understands same-assembly accessibility. It missed a cross-assembly `internal` member (CS0122, since `internal` is only accessible with a matching `InternalsVisibleTo`) and accessor-level accessibility, such as `string Probe { get; private set; }`, where the setter alone is unreachable (CS0272).
4. **`Compilation.IsSymbolAccessibleWithin(member, subjectType, throughType: accessorInterface)`, plus a separate check per accessor** (commit `7334ee06`). This is the version that ships. Passing `throughType` is load-bearing: without it, `IsSymbolAccessibleWithin` answers "is this member reachable from the subject type at all", which for a protected member is true through inheritance, and the protected case regresses back to CS1540. With `throughType` it answers the question the generated code actually asks: "is this member reachable through a cast to this specific interface". The per-accessor pair (`GetAccessorAccessibility` in `SubjectMetadataExtractor.cs`) exists because a getter and setter can have independently weaker accessibility than the property itself; the emitter drops just the inaccessible half rather than the whole member.

Because the check goes through the compiler's own symbol model, it also correctly honours `InternalsVisibleTo` between assemblies, which no hardcoded accessibility list could express. This is pinned by `WhenInterfaceDefaultMemberIsInternalInReferencedAssemblyWithInternalsVisibleTo_ThenMemberIsKept` in `GeneratorShapeTests.cs`.
5. **The identical defect existed, unfixed, on the sibling code path** (commit `2f55079f`). `ExtractInterfaceDefaultProperties` (interface default properties) got steps 1 to 4 above, but `CollectProperties` (class-declared explicit implementations, `string IFoo.Kind => ...` written directly on the subject class) had no accessibility guard at all, and produced the same CS1540 for a class explicitly implementing a protected interface member. The fix extracted `GetAccessorAccessibility` as a shared helper so both paths run the same check instead of carrying two copies that can drift independently, which is exactly how this took four tries in the first place: the second bug fix did not touch the code path that needed it.

The lesson generalises beyond this one guard: an accessibility rule that does not go through `IsSymbolAccessibleWithin` (or an equivalent compiler query) is testing a proxy for the real question, and every proxy tried here was wrong in a different assembly-boundary or accessor-level way.

## Why the cast targets the implemented interface, not the declaring one

For `interface IMale : IHuman { Gender IHuman.Gender => Gender.Male; }`, the generator reaches the property through `IHuman`, the interface that declares the member being implemented, not `IMale`, the interface the explicit implementation syntax appears on. This is not a stylistic choice.

`IPropertySymbol.Name` for an explicit implementation is the fully qualified `"Namespace.IHuman.Gender"`, not the simple name `"Gender"`. The name and the cast target both come from `property.ExplicitInterfaceImplementations.FirstOrDefault()`, which is the implemented member's own symbol, so both resolve correctly from the symbol rather than by string-splitting the qualified name.

Casting to `IMale` instead, which is what a naive fix based on the qualified name suggests, compiles but breaks at a different layer. Reflection on an interface type does not search base interfaces:

```
typeof(IMale).GetProperty("Gender", Public | NonPublic | Instance)   ->  null
typeof(IHuman).GetProperty("Gender", Public | NonPublic | Instance)  ->  ok, public
```

`SubjectPropertyMetadata`'s `PropertyInfo` overload dereferences `propertyInfo.Name` inside a static initializer, so a null lookup throws at type-load time, not at the call site, which makes it a particularly unpleasant failure to diagnose from a stack trace alone.

Casting to the implemented interface is also dispatch-correct, not merely reflection-safe. When a base class provides an implicit implementation and an interface separately declares a default for the same member, the base class implementation wins at runtime regardless of which interface reference is used to call through, because a class implementation always beats a default interface implementation. The generator relies on this: it never needs to special-case "does a class implementation shadow this default", because casting to the implemented interface and letting normal dispatch resolve the call produces the right answer either way.

## Why the subject interception members are emitted once per hierarchy

The generator used to emit the whole `IInterceptorSubject` block into every subject in a hierarchy. That is what issue #437 was: the most derived explicit implementation wins the interface map, so the generated constructor's `((IInterceptorSubject)this).Context.AddFallbackContext(context)` only ever populated the most derived `_context`. Every base class kept a permanently null field and its generated accessors took the documented no-interception fast path, while `PropertyChanged` kept firing, because the setter raises it directly rather than through the chain. The property value was right and the interceptors never ran.

The block is now emitted in one of two modes, chosen per class in `SubjectBaseContract.Resolve`.

**Root mode** emits everything: `_context`, `_properties`, the explicit `Context`, `Data`, `SyncRoot`, `Properties` and `AddProperties`, plus `GetInstanceProperties()` and the three helpers `GetPropertyValue`, `SetPropertyValue` and `InvokeMethod`. The helpers changed from `private` to `protected` so a subject below can call them. `_context` and `_properties` stay private, because only the root's own members touch them.

**Derived mode** emits one line of that block, the explicit `IInterceptorSubject.Properties`, and inherits the rest. The class still re-lists `IInterceptorSubject` in its base list, because an explicit interface implementation is only legal in a class that lists the interface itself (CS0540), and inheriting the interface does not count.

Two smaller pieces move with it. `AddProperties` merges from `((IInterceptorSubject)this).Properties` rather than from `_properties`, so that the merge in the root starts from the most derived `DefaultProperties`. And in a `sealed` subject every member that would be `protected` is emitted `private` instead, including `RaisePropertyChanged`, because a protected member in a sealed class is CS0628 and `src/Directory.Build.props` turns that into a build error. Sealedness comes from `typeSymbol.IsSealed` rather than from the attributed declaration's modifiers, since `sealed` may sit on any partial declaration.

### Why `Properties` cannot move to the root

`DefaultProperties` is a `static` hidden by `new` at each level, so the expression `GetInstanceProperties() ?? DefaultProperties` binds at compile time to whichever class it was emitted into. Each level concatenates only its nearest subject ancestor, which has already folded in its own, so the leaf's static holds every level. Emitted in the leaf, the expression reports all levels. Emitted only in the root, every derived subject would report the root's property set alone, which trades a silent interception bug for a silent metadata bug.

That is also why the class keeps re-listing the interface, and therefore why the re-implementation hazards below exist at all.

### Why the base class facts come from the nearest subject ancestor

Mode selection and every base class fact are asked of the **nearest subject ancestor**, found by walking `BaseType` upward and skipping `System.Object`. An ancestor counts as a subject when it carries `[InterceptorSubject]` **or** declares `IInterceptorSubject` in its own interface list.

That second half reads `INamedTypeSymbol.Interfaces` and deliberately does not recurse into `BaseType`, unlike the general `SymbolExtensions.ImplementsInterface`. `AllInterfaces` reports interfaces inherited from a base class, so it would report a plain intermediate class as a subject whenever the real subject ancestor is a metadata symbol, which is every cross-assembly hierarchy. The walk would then name the intermediate as the ancestor. That class exposes none of the contract and no `DefaultProperties`, so the subject would either fall back to emitting its own interception members or be refused outright, and the real ancestor's properties would be neither merged nor intercepted. The bug this change exists to fix would come back for exactly the shapes that cross an assembly boundary.

Reading the immediate base instead of the nearest subject ancestor is what made a plain class between two subjects fail to build before this change: the intermediate carries no attribute and, at generation time, implements nothing (the base subject's interface list exists only in its generated file), so the leaf emitted a full root shape that collided with everything it had inherited, and `TreatWarningsAsErrors` turned the resulting CS0108 warnings into errors.

### Mode selection, in order

```
let ancestor = nearest subject ancestor, or none

if ancestor is none                                                 -> Root
else if ancestor carries the attribute and will be generated here   -> Derived
else if ancestor exposes the full contract accessibly               -> Derived
else if a usable static DefaultProperties resolves through ancestor -> Root, report NI0012
else                                                                -> suppress, report NI0011
```

The second branch exists because a generator cannot see its own output: for an ancestor declared in this compilation the generated members do not exist as symbols yet, so the attribute is the only available evidence, and it is sufficient because the same generator run produces them. "Will be generated here" is stricter than "declared in source": `WillBeGeneratedInThisCompilation` requires every declaration to be a `ClassDeclarationSyntax` carrying `partial`, so an attributed ancestor that NI0001 or NI0003 suppresses does not drag its subclass into derived mode.

The "nearest" qualifier is load-bearing rather than tidy. Asking whether *some* ancestor carries the attribute selects derived mode even when a hand-written `IInterceptorSubject` implementer sits between the generated root and the leaf. That shape reproduces #437 silently: `Context` resolves to the middle's executor because the middle re-implemented the interface, while the inherited helpers still read the root's field, which nothing populates. With the nearest ancestor being the hand-written middle, the first branch fails on the attribute, the second on the missing helpers, the third finds a static `DefaultProperties` through the chain, and the class correctly falls back to root mode with NI0012.

"Usable" means accessible **and** of a type the emitted `.Concat(...)` accepts, that is `IReadOnlyDictionary<string, SubjectPropertyMetadata>` or something implementing it. A static field counts as well as a property, because the emitted call site reads both the same way. Checking only that some static named `DefaultProperties` resolves lets `public static int DefaultProperties` through, and the generated code then fails with CS1929, which is exactly the raw compiler error in generated code the diagnostics exist to replace.

### The `INotifyPropertyChanged` decision is not the same question

`BaseClassHasInpc` decides whether the subject declares its own `PropertyChanged` and `RaisePropertyChanged`, and it is resolved independently of the mode. It is true when the type itself implements `IRaisePropertyChanged`, inherited or not, and otherwise when the nearest subject ancestor carries the attribute and there is real evidence it owns the notify members: either it will be generated in this compilation, or a callable `RaisePropertyChanged(string)` is reachable from the type's own body.

The attribute on its own is only a promise. An attributed base can be non-partial, so nothing is ever generated into it, and a hand-written attributed base may carry no notify members at all. Believing the promise leaves the subject with neither call form available: the simple name is CS0103 and the interface cast throws at runtime. The interface clause is deliberately asked of the type rather than of the ancestor, because a base that implements `IRaisePropertyChanged` by hand without implementing `IInterceptorSubject` is not a subject ancestor at all, and dropping the clause makes its subclass re-declare `PropertyChanged` and `RaisePropertyChanged`. `ManualInpcPersonBase` in `Namotion.Interceptor.Tracking.Tests` is exactly that shape and has a live test.

The emitted raise call follows from the same two facts: a simple-name call when the ancestor carries the attribute and a callable member is reachable, the `((IRaisePropertyChanged)this)` cast when the chain provides them some other way, and a simple-name call to the class's own member when it emits them itself.

## Why the virtual defaults hook was rejected, and what it measured

`Properties` could have moved into the root behind a hook, with each level overriding it:

```csharp
// root only
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
    => _properties ?? GetDefaultProperties();
protected virtual IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;

// every derived class, replacing the explicit Properties line
protected override IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;
```

This was built and verified to report all levels correctly. It is genuinely attractive: derived classes would stop listing `IInterceptorSubject`, CS0540 would stop constraining the design, the re-implementation hijack and the cross assembly rebuild gap below would both become structurally impossible, and `GetInstanceProperties()` would disappear from the emitted code and from the base class contract.

**It was not rejected on a measured cliff, and the record should not read as though it was.** `IInterceptorSubject.Properties` is read on every intercepted write through `PropertyReference.Metadata`, which is deliberately uncached (`PropertyReference.cs:20-29`, with callers in `IWriteInterceptor`, `LifecycleInterceptor` and `DerivedPropertyChangeHandler`), so the first draft of this design assumed the hook would cost something meaningful there. It was then measured, against two hand-written three level hierarchies reproducing the two dispatch shapes. Those mimics were written for this decision and removed once it was taken, since they modelled a shape the generator never emitted and so could never regress; recover them from the history of `PropertiesDispatchShapeBenchmark.cs` if the question is reopened. What survives in `SubjectHierarchyBenchmark` is the chosen shape measured on real generated subjects, monomorphic and polymorphic, plus the `PropertyReference.Metadata` lookup itself.

- At a **monomorphic** call site, the hook is free. Through the real `PropertyReference.Metadata` the alternative measured 4.681 ns against the chosen design's 4.801 ns, which is inside the noise floor and has no sign worth trusting. The JIT devirtualizes the hook when it sees one type.
- At a **polymorphic** call site the hook costs **0.133 ns per `Properties` read**. The polymorphic rows do three reads per operation, so the 0.400 ns gap between 3.823 ns and 3.423 ns is 0.133 ns per read. The gap reproduced in both runs and is larger than either arm's run to run spread, so it is real rather than noise.
- Scaled to a write: with two to four `Properties` reads per intercepted write, that is roughly 0.27 ns to 0.53 ns on a write measured at 11.86 ns, so **about 2 to 4 percent**. That figure is arithmetic on the measured per read delta, not a measured write, and it sits at or below this machine's 5 percent noise floor.

The polymorphic number is the representative one, because `PropertyReference.Metadata` is a single member in `Namotion.Interceptor` and its `Subject.Properties` read is one shared call site that every subject type in the process passes through. Two caveats belong with it: the two families are hand written mimics, so they pin the cost of the dispatch shape and not of any code the generator emits, and the polymorphic array holds three types, which is the shape guarded devirtualization handles best.

The decision to keep the current design was taken deliberately with those numbers in hand, not by default. The cost is small but it is paid by every subject forever, including the large majority that have no base class at all, while the hazard the hook would remove is caught at compile time by NI0014 for every consumer that recompiles. `AGENTS.md` ranks correctness above performance, so this is a close call rather than an obvious one, and anyone revisiting it should start from the numbers above rather than from the phrase "rejected on performance".

The numbers come from BenchmarkDotNet 0.15.5 on an Apple M4 Max under .NET 9.0.10, with `WarmupCount=5 IterationCount=15 LaunchCount=1`, and every configuration was run twice in separate processes so that the run to run spread supplies the noise floor rather than the within process error bars. Re-run them with `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release -- --filter "*PropertiesDispatchShapeBenchmark*"`.

## Residual risks accepted with per hierarchy interception members

**Interface re-implementation can move a slot.** Because each subject re-lists `IInterceptorSubject`, the interface map is recomputed at that class, and a public member matching `Context`, `Data`, `SyncRoot` or `AddProperties`, or an explicit implementation of one of them, takes the slot from the root. `Context` is what makes the severity: the inherited helpers keep reading the root's `_context`, which nothing populates, so interception dies entirely and the unguarded `(IInterceptorExecutor)` casts in `DynamicSubjectFactory` and `RegisteredSubject` throw. `SyncRoot`, `Data` and `AddProperties` are milder than they look, since every product consumer of `SyncRoot` reads it through the interface and a hijack therefore redirects all of them to the same object rather than splitting the lock; they stay in the rule for consistency and because a user-owned lock object is still an aliasing hazard. NI0014 makes all of it a build error.

`Properties` is excluded from NI0014, because the class emits its own explicit implementation of it, which always wins. Members declared in the same class as the explicit implementation they would displace are excluded for the same reason, which is why the scan starts at the subject and stops before the ancestor that provides them.

**Cross assembly rebuild.** NI0014 fires where the derived subject is compiled, so a member added to a base assembly afterwards is not seen. The risk is narrower than a first reading suggests. All four of these have to hold:

1. the referenced assembly's subject hierarchy is more than one level;
2. the new member is public, non-static and instance, and is added to a class **between** the root and the consuming subject rather than to the root itself, because a class's own explicit implementation beats its own public members;
3. it matches an `IInterceptorSubject` member by name and signature exactly;
4. the consuming assembly ships without being recompiled.

Recompiling the consumer turns it into an NI0014 build error, so the exposure window is precisely "shipped, not rebuilt". The virtual hook above would make it structurally impossible.

**Interface evolution.** Any member added to `IInterceptorSubject` in future has to be evaluated for the same hijack question and added to NI0014's list, because derived subjects keep re-listing the interface.

**Writes before the context is published.** A derived class's field initializers run before the base constructor, so the context is null there and those writes take the fast path, as does anything in a constructor body that runs before `AddFallbackContext`. This is narrower than "construction time writes", and the difference is a deliberate behaviour change: `((IInterceptorSubject)this).Context` dispatches virtually, so a hand-written `Leaf(IInterceptorSubjectContext context) : base(context)` publishes the executor inside the base constructor, and a base declared property written afterwards in the leaf constructor body is now intercepted where before it silently was not. The hand written subclass contract depends on exactly this ordering.

**A suppressed ancestor produces calls to members that do not exist.** If an in source ancestor carries the attribute and is partial, but its own generation is suppressed by NI0002, NI0009 or NI0010, the derived class still selects derived mode and emits calls to members that were never generated. This is accepted: the build is already failing on the ancestor's own diagnostic and on CS9248 for its partial properties, so it adds noise to an already red build rather than hiding anything. NI0001 and NI0003 are not in that list, because `WillBeGeneratedInThisCompilation` already excludes a non-partial declaration and a record declaration.

## Five breaking changes

Two of them, NI0013 and NI0014, are new errors of this generator, and both reject source that compiled before. The third is not a diagnostic of this generator at all: sharing the interception members widened the inherited helper surface, so the compiler now reports hiding where it reported nothing. The fourth is NI0012 on the upgrade path: a leaf project that takes the new package while a referenced model library is still built by the old generator gets a base whose helpers are `private`, which fails the contract. The fifth is the wrapper guard: a `*WithoutInterceptor` method whose stripped name is one the generated half occupies is now rejected with NI0006 at any arity, so a wrapper that compiled and worked before, such as `InvokeMethodWithoutInterceptor(string, object[])`, now needs renaming. That is deliberate: three attempts at a signature precise rule each admitted a silent capture, and a rename request is loud and recoverable where a capture is neither. That one is a warning, so it breaks the build only where warnings are errors, and rebuilding the base assembly clears it. All four are listed here rather than presented as pure safety nets.

**NI0013** fires in derived mode when the subject, or any class between it and its subject ancestor, declares a member named `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` or `GetInstanceProperties`. The rule is deliberately over-approximate: name only, any member kind, no signature test, statics included. A precise rule would be both harder and unsound, because capture has two quiet routes. A `new` annotated member of the same shape captures the generated call with no compiler diagnostic at all, and an applicable overload with a different signature can win overload resolution without hiding anything. On an intermediate class the scan is restricted to members accessible from the subject, because a private member there neither hides nor binds and reporting it would be a pure false positive.

The break is asymmetric. A subject may legally declare `public void InvokeMethod(string name)` today, since the signature differs from the generated private helper. After this change the same source is an error in derived mode and stays legal in root mode. The rationale for leaving root mode unguarded is narrower than it first looks. An *identical* signature there is a hard CS0111, so that half really is covered by the compiler. A different signature is not covered by anything: a root subject declaring `protected bool SetPropertyValue(string, string, string, Action<IInterceptorSubject, string>)` alongside a `string` partial property compiles with zero diagnostics, wins overload resolution against the generated generic helper, and captures every write to that property. That residual case is a known gap rather than a guarded one. It is left open because the colliding member and the generated call are two halves of one class the author owns, so the fix is local and the shape is visible from the declaration, whereas in derived mode the capturing member and the code it captures live in different classes. `RaisePropertyChanged` is deliberately outside the rule: it was already inherited whenever the base provided the notify members, so a `new` annotated override of it may well be deliberate and predates this change.

**NI0014** fires in the same range, plus the subject ancestor under the conditions below, for the four hijackable interface members. Two details differ from the first draft of the design and are worth knowing before the rule is narrowed again. The public member clause matches only a genuine implicit implementation, comparing the member type, parameter types and ref kinds, and requiring the accessors to be publicly callable, so an ordinary `string Data` or a `bool AddProperties(...)` on a domain model is not reported. And the explicit implementation clause is reported on the subject itself as well, without the "declaring class is not a subject" qualifier the design first proposed: in derived mode the subject's generated half contains nothing but `IInterceptorSubject.Properties`, so a hand-written explicit `Context` on the user's half is not a conflict the compiler catches, it is the severe silent case.

The break here is that a derived subject declaring `public object SyncRoot { get; }` compiled cleanly before, precisely because that class emitted its own explicit implementation which beat its own public member. It now takes the slot, and it is now an error.

NI0013 is scoped from the subject up to the nearest subject ancestor and excludes that ancestor, whose members are the ones the contract demanded of it. NI0014 is not bounded: it walks the whole chain up to `object`, because the nearest subject ancestor is exactly where a hand-written hijacker sits, and a second one further up used to be invisible. Below that ancestor the report is unconditional. At it and above it, the member is reported only when the declarer's own base already implements the same member, which keeps the ordinary hand-written subject deriving from `object` quiet and cannot be asked below the ancestor, where an ancestor generated in the same compilation does not implement the interface as a symbol yet.

Two refinements that once sat on that condition are gone. Exempting an explicit implementation was dead: CS0540 forces the declaring class to list `IInterceptorSubject`, listing it makes that class the nearest subject ancestor and therefore always the contract provider, so the exemption fired on every base class there is and hid the only form a hand-written ancestor can express. An `override` is skipped instead, since it occupies the slot it already had and displaces nothing.

The design first specified a per member contract provider located by re-running mode selection upward; the implementation uses the nearest subject ancestor for both rules, which is the same class in every shape where the contract is satisfied as a whole and is simpler to reason about.

**The inherited helper surface is wider.** `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` and `GetInstanceProperties` are emitted `protected` instead of `private`, because that is what lets a derived subject inherit them instead of re-emitting them. A private member is invisible to a subclass and hides nothing, so on master a hand-written class deriving from a generated subject could name its own members anything it liked. It now inherits four `protected` members, and a member that genuinely hides one of them is CS0108. `src/Directory.Build.props` sets `TreatWarningsAsErrors`, so that is a build failure on source that compiled clean before.

This break reaches a class the generator never scans. NI0013 covers a class that is itself `[InterceptorSubject]`, or one sitting between a subject and its subject ancestor. `SubjectBaseContract.Resolve` is only ever asked about a subject, so a plain hand-written subclass of a generated subject is not examined and no NI0013 can fire on it. That is not a hole, because this break is loud rather than silent: the compiler names the file, the line and the hidden member. The consumer adds `new` where the hiding is intended, or renames the member. The one shape CS0108 does not cover is an overload that differs in signature, and there it is also harmless, because nothing generated calls the helpers from such a class.

Only two of the four report CS0108 for a member of another kind. `GetPropertyValue` and `SetPropertyValue` are generic, and the compiler does not report a field or property as hiding a generic method, so only a method matching their signature is caught. `InvokeMethod` and `GetInstanceProperties` are not generic, and a field, property or method of that name is caught for both.

## Known gaps

These are not fixed by this work and are not tracked by a diagnostic. They were found incidentally while implementing the shapes above, are out of scope for issue #428, and should become their own issues if they turn out to matter in practice.

- **A `*WithoutInterceptor` method whose stripped name collides with an existing method on the class** emits a duplicate member and fails with CS0111. For example, declaring both `void Probe()` and `void ProbeWithoutInterceptor()` on the same subject: the generator strips the suffix and emits a second `void Probe()` wrapper, which the compiler rejects as a duplicate. The generator does not currently check for this collision before emitting the wrapper. The one case it does check is a collision with the names the generated half occupies, which is reported as NI0006 and the wrapper is not emitted, because there the wrapper captured the generated call, or an interface slot, instead of colliding with it. That check is name only, at any arity, with no exemptions. Two attempts to narrow it each let a capture through: comparing the parameter count ignored that `InvokeMethod` ends in `params object?[]`, so the emitted call sites span every arity from two upward and an overload applicable in normal form beats the helper, which needs the expanded form; and exempting `Context`, `Data` and `SyncRoot` treated "explicit interface property" as a property of the name rather than of the base, which is false for a hand-written base that satisfies the contract with public members, where the wrapper is a CS0108 in the generated file. The price is a false positive on a legitimate wrapper such as `GetPropertyValueWithoutInterceptor(string, string)`, which is loud, actionable and fixed by renaming.
- **An interface property whose only accessible accessor is `init`, such as `{ protected get; init; }`, explicitly implemented by a class, yields a metadata entry with both accessor lambdas null.** This is not the "both accessors inaccessible" case, which is skipped entirely and never reaches the emitter: the property-level accessibility check passes here because `init` is accessible, so the property is kept. The getter lambda is then omitted because `protected get` is not reachable from generated code, and the setter lambda is *also* omitted, even though the property was kept for the setter's sake, because `EmitDefaultProperties` only emits a setter lambda when `HasSetter` is true, and an `init` accessor sets `HasInit`, never `HasSetter`; `HasInit` is consulted only when emitting a partial property's own accessor, a code path an explicit implementation never reaches. The result is a degenerate but valid entry (the property key exists, reading or writing it via the metadata does nothing observable), and is a strict improvement over the previous behaviour, which was CS1540.

### Hierarchy gaps found while reviewing the per hierarchy interception members

All of these reproduce identically on the commit before that work, so none is a regression. They are recorded here rather than fixed because each needs a decision of its own, and two of them would change metadata for every existing subject and so want their own regeneration evidence.

- **For an `override` or `new` property, the base's metadata entry wins the merge.** `EmitDefaultProperties` emits `{own entries}.Concat(base.DefaultProperties).ToFrozenDictionary()`, and `ToFrozenDictionary` keeps the last entry for a duplicate key, so the base overwrites the subject's own. With `Root { virtual partial string Name }` and `Leaf : Root { [MaxLength(5)] override partial string Name }`, `Properties["Name"]` reports `DeclaringType == Root` and no attributes, so data annotation validation never sees the `[MaxLength(5)]`. For an `override` the value is still correct, because the base's getter lambda dispatches virtually; for a `new` property it is not, because the lambda reads the base's declaration. Reversing the concatenation order fixes both, and changes the resolved entry for every override in the repository, which is why it is not folded in here.
- **A `new` partial property whose type differs from the one it shadows throws at type init.** `EmitDefaultProperties` calls `typeof(Leaf).GetProperty(name, Public | NonPublic | Instance)`, which throws `AmbiguousMatchException` when both declarations are visible, so the first `Properties` access throws `TypeInitializationException`. The overload taking a return type, or a `GetProperties` filter on `DeclaringType`, would fix it.
- **A base subject with only a parameterized constructor breaks its subclass.** `DetectConstructorState` never inspects the base, so the generated parameterless constructor on the derived subject fails with CS7036.
- **A plain intermediate class that lists an interface deriving from `IInterceptorSubject`** stops the ancestor walk, because `DeclaresInterceptorSubject` is satisfied by the declaration alone. The contract then fails and, when the real root is in the same compilation, `HasUsableDefaultProperties` cannot see the root's not yet generated `DefaultProperties` either, so the shape falls through to NI0011 rather than to the NI0012 fallback the mode selection ladder implies. Cross assembly the same shape works, because the symbols exist. A domain interface on an abstract intermediate is an ordinary modelling shape, and NI0011's remedy text does not fit it.
- **`DefaultProperties` is emitted by every subject but is not in the hiding table.** A plain base that declares a member of that name gets CS0108 in the generated file, because `EmitDefaultProperties` decides `new` from "there is a subject ancestor" rather than from the hiding rule the four accessor helpers now use.
- **Hiding `RaisePropertyChanged` silently swallows change notifications.** A derived subject declaring `protected new void RaisePropertyChanged(string)` captures the generated setter's call, so its own property changes raise nothing, with no diagnostic, no error and no warning. NI0013 deliberately excludes that name because redirecting it can be intentional and a `new` member that calls `base` is legitimate, which cannot be distinguished without dataflow. Two independent reviews found this, so if it is ever reconsidered, a `Warning` gated on the single string parameter overload is the shape to reach for.

## Why the test strategy has three layers

Issue #428 shipped because the existing tests asserted on generated **text**: `Assert.Contains(@"""Status""")` passes on code that can never compile. A wrong string embedded in a larger wrong string still contains the substring being asserted. Fixing that required more than adding regression tests for the reported shape; it required a strategy that cannot pass on broken output by construction.

1. **Verify snapshots of the full generated source**, using Verify.Xunit's `.verified.txt`/`.received.txt` workflow. This catches unintended output changes, but only for the sixteen shapes that already had a snapshot before this work (ordinary, virtual, override, inheritance, accessor visibility, nesting, and interface-default properties). None of the shapes this work added has one: not explicit interface implementations, not non-public subjects, not subjects nested in a record, struct or interface, and not `in` / `ref readonly` wrapper parameters. Layers 2 and 3 below are what cover those instead. A snapshot can also itself capture code that does not compile, so even for the sixteen shapes it does cover, it is necessary and not sufficient.
2. **A compile-clean assertion** (`GeneratorTestHost.RunExpectingCleanCompilation` and its library-reference variant), which fails the test if `outputCompilation.GetDiagnostics()` contains any error. This is the layer that would have caught the original #428 defect directly, since the generated code for a sub-interface explicit implementation never compiled.
3. **Real subject models compiled by the generator inside the test project**, exercised through the registry at runtime rather than through the generator's own output. A regression here is not a failing test, it is a failing build, because the test project itself cannot compile against a broken generator. This is the layer that catches behavioural regressions that layer 2 cannot see at all, because the input is valid C# either way, so a regression there would still compile clean: case Z (a class that both declares a property and explicitly implements the same interface member with a colliding name) is one dictionary-emission change away from throwing `TypeInitializationException` on a duplicate key at type-load time, and case AA (two explicit implementations of one generic interface at different instantiations) is one deduplication change away from silently dropping a member that currently resolves. Both compile cleanly today and are pinned by layer 3 tests asserting the correct runtime value, so a regression in either shows up as a failing test rather than a silent behaviour change that only a diligent code reviewer would catch.

Three inputs are invalid C# by construction and are asserted the opposite way: instead of demanding a clean compilation, the test asserts that the expected compiler error is present, the generator itself does not throw, and no additional generator-caused error appears beyond the expected one. `partial` combined with an explicit interface implementation is illegal (CS0754); `[InterceptorSubject]` on a `struct` or `interface` is rejected by the compiler before the generator's own diagnostics apply (CS0592); and a non-attributed class declaring partial properties without `[InterceptorSubject]` is not a generator input at all, it is the contrast case proving the generator ignores types it was not asked to process (CS9248).

## Language semantics this design depends on

Each of these was established by compiling the shape rather than by reading the specification, and each one pins a decision above. They are recorded so that a future change does not have to rediscover them, and so that a change which contradicts one is recognised as contradicting it.

**An explicit interface implementation is only legal in a class that lists the interface itself.** Inheriting the interface does not count.

```
error CS0540: 'DerivedNoRelist.ISubject.Properties': containing type does not implement interface 'ISubject'
```

This is why a derived subject keeps re-listing `IInterceptorSubject` even though its base already implements it, and therefore why interface re-implementation is part of the emitted shape at all.

**Interface mapping under re-implementation prefers a class's own explicit implementation over its own public members, but a matching public member in a strictly more derived class wins.**

```
RootWithPublic : SyncRoot=EXPLICIT       public member beside the explicit implementation loses
DerivedRelist  : SyncRoot=Object         the base's explicit implementation wins when nothing matches
DerivedHijack  : SyncRoot=USER-OWNED     a public member in a derived class takes the slot
LeafOverPlain  : SyncRoot=MIDDLE-OWNED   and so does one on a plain class in between
```

The first line is why the root is never hijacked by its own member, and the last is why NI0014 walks the chain instead of inspecting only the subject.

**A public member whose signature does not match does not take the slot, and produces no diagnostic.** `public object Data { get; }` on a derived subject leaves `((IInterceptorSubject)x).Data` resolving to the base's explicit implementation, with no CS0738. This is why NI0014 matches against the real interface member instead of by name: an ordinary property called `Data` or `Context` is harmless.

**`[MethodImpl]` is not valid on a property declaration**, only on constructors and methods (`error CS0592`). Relevant if `GetInstanceProperties()` is ever turned back into a property.

**A protected member in a sealed class is CS0628, and an unnecessary `new` is CS0109.** Both are warnings, and `src/Directory.Build.props` turns both into build errors, including inside generated files. Any modifier decision in the emitter has to be right in both directions.

**Hiding is by signature for methods and by name for every other member kind, and it is not staticness sensitive.** A `new static` method hides an inherited instance method of the same name, and generated code calling that name by simple name binds to it. That is why NI0013 does not skip statics.

**`Type.GetProperties(Instance | Public | NonPublic)` returns inherited protected properties but never methods.** This is the entire reason `GetInstanceProperties()` is a method rather than a property: `DynamicSubjectFactory` reflects with exactly that filter and converts anything it does not recognise into an intercepted subject property, so a protected property here would give every Castle proxied subject a phantom property.

## Known limitations of the diagnostics

Most of these surface as a compile error. Two do not, and both are worth knowing: a base that satisfies every symbol check NI0011 performs can still be behaviourally wrong, described below; and in root mode a differently signatured helper captures the generated call with no diagnostic at all, as the NI0013 breaking change above records.

**The `new` modifier lookup matches on name and parameter count, not parameter types.** A base exposing a same-name, same-arity overload with different parameter types draws a `new` that hides nothing, which is CS0109 and therefore a build error. Narrow, since it needs one of four unusual names, and loud when it happens. Making it exact means comparing parameter types in `HidableMembers`.

**NI0013 is deliberately name-only, any member kind, no signature test.** It therefore reports members that could not actually capture the generated call. A precise rule is both harder and unsound: the dangerous case is a `new` annotated member of the same shape, which captures the call and produces no compiler diagnostic at all, and an applicable overload with a different signature can win overload resolution without hiding anything. The false positives are on four names nobody chooses by accident.

**NI0013 and NI0014 fire only in derived mode.** A root mode subject that declares its own `GetInstanceProperties` gets a raw CS0111 inside the generated file rather than a diagnostic. Accepted because the name is new, so no existing source can carry it, and the error is loud.

**NI0011 verifies shape, not behaviour.** A base can satisfy every symbol check and still be wrong in three ways, listed under the base class contract in `docs/subject-guidelines.md`. The most damaging is a base whose helpers route through a different executor from the one its `Context` publishes, which reproduces the original bug exactly while passing every check.

**The contract check compares return types and the leading `string` parameter, not the delegate parameters.** A base whose `readValue` parameter is `Func<IInterceptorSubject, int>` rather than `Func<IInterceptorSubject, TProperty>` still satisfies the check and then fails at the call site. The cheap checks catch every realistic typo in a hand copied signature; constructing the expected `Func<>` and `Action<>` types to compare exactly is the remaining work.

**`WillBeGeneratedInThisCompilation` tests only that every declaration is a partial class.** An ancestor carrying the attribute whose own generation is suppressed for another reason, NI0002, NI0009, NI0010 or NI0011, therefore still puts its subclass into derived mode, which turns one actionable diagnostic on the ancestor into a handful of raw CS0103 and CS0117 errors on the subclass. Accepted because the build is already failing on the ancestor's own diagnostic, but it is noise, and modelling the remaining guards is a few cheap symbol and syntax checks on a symbol already in hand.

**NI0013 and NI0014 report at the subject's location, not the offending member's.** When the member lives on a plain class between the subject and the contract provider, the error points at the subject, the file actually containing the member is never named, and the diagnostic repeats once per subclass rather than once per member. Reporting at `member.Locations.FirstOrDefault(l => l.IsInSource)` with the current location as the fallback would fix both.

## Compile time cost of the base class resolution

Measured with an interleaved A/B harness over synthetic projects, comparing this design against the one it replaced. A realistic 360 subject project costs about ten percent more generator time, which is roughly one percent of that project's build, with allocations flat. Subjects with no subject base, which is the overwhelming majority, cost two percent more.

Two shapes cost considerably more, and both have the same cause: the same question is asked of the same ancestor once per descendant rather than once per ancestor.

| shape, 300 subjects | before | after |
|---|---|---|
| in source chain, depth 10 | 47.4 ms | 60.7 ms |
| in source chain, depth 50 | 48.9 ms | 96.5 ms |
| base in a referenced assembly | 69.0 ms | 109.5 ms |

The contract check costs about 93 microseconds per subject and its answer depends on the ancestor, not on the subject, apart from two accessibility clauses. Memoizing it per compilation, keyed by the ancestor symbol, was measured to recover 35 percent of the cross assembly cost, and memoizing the two mutually recursive notify predicates recovers 26 percent at depth 50 along with 15 percent of the allocations. Both produce byte identical generated output, so the regeneration gate above can prove they change nothing.

Nothing in this repository is affected: the deepest subject chain is three and there is no cross assembly subject inheritance anywhere. The cross assembly shape is what a consumer deriving from a subject shipped in a package hits, which is the case NI0011 and NI0012 exist for.

This is not a net cost. Derived mode emits 13 to 19 percent less source for a hierarchy, and the compiler recovers that in every phase after the generator returns, which dominates. Measured end to end over generator plus bind plus emit, against a noise floor of 1.5 percent: a 360 subject mix is 0.6 percent faster, depth 10 chains are 21 percent faster, depth 50 chains 20 percent faster, and the cross assembly shape 16 percent faster. Only a project with no hierarchy at all pays anything, and it pays 0.7 percent, inside the floor, because it emits 1.2 percent more source rather than less.

The generator's own share of a compile is around 1.4 percent, which is why a large regression inside it does not move the build. The reason to take the memoizations anyway is the incremental case: the pipeline re-runs every subject on every edit, so the per subject cost is paid on each keystroke rather than once per build. A one file edit in a 300 subject depth 50 project costs 54 percent more than before.

The numbers in this section were taken with a throwaway interleaved harness that is not in the repository, so they are recorded rather than reproducible. Rebuild it before acting on them.

## How to verify a change to this generator

**Regenerate everything and diff it.** The test suite proves the shapes someone wrote a test for; this proves the rest. Build both trees with `-p:EmitCompilerGeneratedFiles=true` and a `-p:CompilerGeneratedFilesOutputPath`, then `diff -r`. For a refactor the diff must be empty.

Use `--no-incremental` on both builds. Without it the Razor generator does not re-emit and about 21 files show as present in one tree and absent in the other. That is distinguishable from a real difference, since `diff -r` reports absence separately, but it makes the comparison useless.

Diff the `Namotion.Interceptor.Generator` subdirectory, not the whole output. One `CompilerGeneratedFilesOutputPath` is shared by every project in the solution, and Razor writes `_Imports_razor.g.cs` from each of them to the same path, so whichever project finishes last wins. That produces a large diff between two builds of identical source. The subjects this generator emits are named after their full type name and do not collide.

**Compare resolved `DefaultProperties` blocks, not key lists.** `.Concat(Base.DefaultProperties)` puts the base last and `ToFrozenDictionary` is last wins, so a changed entry does not show up as a key difference.

**Benchmarks need a control group.** Use benchmarks that contain no subjects, currently `ServiceOrderResolverBenchmark` alone, as the noise floor. A comparison without one looked clean at a median of +1.4% while carrying two rows at +28% and +33% that belonged to an unrelated pull request. Three further cautions, all learned the hard way: point local `master` at `origin/master` first, or the comparison credits other people's commits to the branch; run both arms back to back under `caffeinate -is`, or a sleep between them shifts every row by roughly ten percent; and `-Short` leaves some rows unmeasurable, with error bars from 16 to 62 percent, so a row outside the noise floor must be re-measured at full iteration counts before it is called a regression.

**Assert on an interceptor, never on the value or on `PropertyChanged`.** Both of those behave correctly while interception is broken, which is why issue #437 survived so long. Two tests in this repository asserted around the bug while appearing to cover it.

## Defects that predate this work

All six reproduce on the commit before the explicit interface implementation work, so none of them came from that change. They are listed because each one had no test, and a future change in this area should expect the same: shapes that nobody has written down are where the bugs are.

1. Properties declared on a base subject were never intercepted, the subject of issue #437.
2. A plain class between two subjects did not build, three CS0108.
3. A sealed root subject did not build, CS0628.
4. A hand written base implementing `IRaisePropertyChanged` explicitly gave CS0103 in generated code.
5. A hand written base without a static `DefaultProperties` gave CS0117 in generated code.
6. A subject over an ordinary non-subject base gave CS0108, hitting seven of ten probed base shapes.
