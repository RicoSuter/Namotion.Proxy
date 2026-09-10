using System.Reflection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseShapeTests
{
    [Fact]
    public void WhenBaseImplementsRaisePropertyChangedWithoutBeingASubject_ThenNoNotifyMembersAreRedeclared()
    {
        // Arrange: the base is INPC + IRaisePropertyChanged but NOT IInterceptorSubject and has no
        // attribute, so it is not a subject ancestor. BaseClassHasInpc must still be true, because
        // its second disjunct is asked of the subject, not of the ancestor.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public abstract class ManualBase : INotifyPropertyChanged, IRaisePropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class ManualDerived : ManualBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", generated);
        Assert.DoesNotContain("protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(Name))", generated);
    }

    [Fact]
    public void WhenAttributedAncestorRaisesThroughAnExplicitImplementation_ThenTheSetterCallsItThroughTheInterface()
    {
        // Arrange: Middle carries the attribute but emits no RaisePropertyChanged of its own,
        // because ManualInpcBase already provides the INPC members, and that base implements the
        // raise explicitly. Leaf's attributed ancestor therefore exposes no member of that name and
        // a simple-name call from Leaf is CS0103.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public abstract class ManualInpcBase : INotifyPropertyChanged, IRaisePropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class Middle : ManualInpcBase
                {
                    public partial string MiddleName { get; set; }
                }

                [InterceptorSubject]
                public partial class Leaf : Middle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var middle = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Middle.g.cs")).SourceText.ToString();
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Leaf.g.cs")).SourceText.ToString();

        // Assert
        Assert.DoesNotContain("void RaisePropertyChanged(string propertyName)", middle);
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(LeafName))", leaf);
    }

    [Fact]
    public void WhenTheRaiseSitsAboveTheAttributedAncestor_ThenTheSetterStillCallsItBySimpleName()
    {
        // Arrange: the same shape as above except that ManualInpcBase implements the raise as an
        // ordinary public member. Middle still emits none of its own, so only a walk of the whole
        // chain finds the member that answers Leaf's call; a lookup stopping at the attributed
        // ancestor would drop Leaf to the interface form. This is the shipped ManualInpcPersonBase
        // shape from Namotion.Interceptor.Tracking.Tests.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public abstract class ManualInpcBase : INotifyPropertyChanged, IRaisePropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class Middle : ManualInpcBase
                {
                    public partial string MiddleName { get; set; }
                }

                [InterceptorSubject]
                public partial class Leaf : Middle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var middle = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Middle.g.cs")).SourceText.ToString();
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Leaf.g.cs")).SourceText.ToString();

        // Assert: the boundary between the two forms. Middle has no attributed ancestor at all and
        // keeps the interface form, Leaf has one and reaches the inherited member directly.
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(MiddleName))", middle);
        Assert.Contains("RaisePropertyChanged(nameof(LeafName));", leaf);
        Assert.DoesNotContain("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(LeafName))", leaf);
    }

    [Fact]
    public void WhenReferencedAttributedBaseRaisesThroughAnExplicitImplementation_ThenTheSetterCallsItThroughTheInterface()
    {
        // Arrange: the same shape across an assembly boundary. The base satisfies every contract
        // clause, so the subject takes derived mode, but its raise is reachable through the
        // interface only.
        const string librarySource = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.ComponentModel;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Library
            {
                [InterceptorSubject]
                public class ExplicitRaiseBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
                {
                    private IInterceptorExecutor? _executor;
                    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                    public event PropertyChangedEventHandler? PropertyChanged;

                    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                    IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                        => _properties = ((IInterceptorSubject)this).Properties
                            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                            .ToFrozenDictionary();

                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

                    protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                        => _executor is not null ? _executor.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                    protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                    {
                        if (_executor is null)
                        {
                            setValue(this, newValue);
                            return true;
                        }

                        return _executor.SetPropertyValue(propertyName, newValue, currentValue, setValue);
                    }

                    protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                        => _executor is not null ? _executor.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.ExplicitRaiseBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource);
        var generated = result.SingleSource();

        // Assert: derived mode, no diagnostic, and the only call form that binds.
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0011" || d.Id == "NI0012");
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(LeafName))", generated);
    }

    [Fact]
    public void WhenReferencedAttributedBaseHasNoNotifyMembers_ThenTheSubjectDeclaresItsOwn()
    {
        // Arrange: the attribute alone is not evidence that the base owns the INPC members. This
        // base owns none of it, so a simple-name call is CS0103 and an interface cast throws at
        // runtime; the subject has to declare those members itself.
        const string librarySource = """
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public class NoNotifyBase
                {
                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.NoNotifyBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource);
        var generated = result.SingleSource();

        // Assert: the base only provides DefaultProperties, so it takes the NI0012 root-mode
        // fallback and declares the notify members it then calls by simple name.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Contains("IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged", generated);
        Assert.Contains("public event PropertyChangedEventHandler? PropertyChanged;", generated);
        Assert.Contains("RaisePropertyChanged(nameof(LeafName))", generated);
        Assert.DoesNotContain("((IRaisePropertyChanged)this).RaisePropertyChanged", generated);
    }

    [Fact]
    public void WhenSubjectIsSealedAndDerived_ThenItCompilesWithoutWarnings()
    {
        // Arrange: a sealed DERIVED subject is legal today, because RaisePropertyChanged is gated
        // on BaseClassHasInpc and so is not emitted into it. Only a sealed ROOT fails (Task 3).
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseSubject
                {
                    public partial string BaseName { get; set; }
                }

                [InterceptorSubject]
                public sealed partial class SealedLeaf : BaseSubject
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act & Assert
        GeneratorTestHost.RunExpectingNoWarnings(source);
    }

    [Fact]
    public void WhenAPlainClassSitsBetweenTwoSubjects_ThenTheDerivedSubjectCompilesAndMergesBaseProperties()
    {
        // Arrange: A is a subject, B is an ordinary class, C is a subject. At generation time B
        // neither carries the attribute nor implements IInterceptorSubject, because A's interface
        // list lives only in A.g.cs, so the immediate base tells the generator nothing.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class A
                {
                    public partial string P { get; set; }
                }

                public class B : A { }

                [InterceptorSubject]
                public partial class C : B
                {
                    public partial string Q { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var derived = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.C.g.cs")).SourceText.ToString();

        // Assert: the base facts come from A, not from B.
        Assert.Contains("public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties", derived);
        Assert.Contains(".Concat(global::Repro.A.DefaultProperties)", derived);
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", derived);
    }

    [Fact]
    public void WhenAPlainClassSitsBetweenTwoSubjectsAcrossAssemblies_ThenTheWalkSkipsItAndNamesTheAttributedAncestor()
    {
        // Arrange: same A/B/C shape as above, but A and B live in a referenced assembly whose
        // generated code is already in metadata. That is what separates SubjectAncestry's
        // Interfaces from AllInterfaces: B inherits IInterceptorSubject from A, so AllInterfaces
        // reports it on B and the walk would stop at the plain intermediate. The result still
        // compiles, so only the emitted shape asserted below catches the regression.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Lib
            {
                [InterceptorSubject]
                public partial class A
                {
                    public partial string P { get; set; }
                }

                public class B : A { }
            }
            """;
        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class C : Lib.B
                {
                    public partial string Q { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource, runGeneratorOverLibrary: true);
        var derived = result.SingleSource();

        // Assert
        Assert.True(
            result.CompilationErrors.Count == 0,
            "Generated code did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationErrors.Select(d => d.ToString())));
        Assert.Contains(".Concat(global::Lib.A.DefaultProperties)", derived);
        Assert.DoesNotContain("((IRaisePropertyChanged)this).RaisePropertyChanged", derived);
    }

    [Fact]
    public void WhenAPlainClassSitsBetweenTwoSubjectsAcrossAssemblies_ThenABaseDeclaredWriteReachesTheInterceptor()
    {
        // Arrange: the shape the nearest-subject-ancestor walk exists for, executed. Naming the
        // right ancestor in the emitted text is only half the claim; the other half is that the
        // ancestor's setter, compiled into the library one class above a plain intermediate, ends
        // up on the leaf's executor.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public partial class LibraryBase
                {
                    public partial string BaseName { get; set; }
                }

                public class PlainInBetween : LibraryBase
                {
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.PlainInBetween
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("App.AppLeaf");
        Assert.NotNull(leafType);
        var leaf = (IInterceptorSubject)Activator.CreateInstance(leafType, context)!;

        // Act
        leafType.GetProperty("BaseName")!.SetValue(leaf, "base-written");
        leafType.GetProperty("LeafName")!.SetValue(leaf, "leaf-written");

        // Assert
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "BaseName" && Equals(write.Value, "base-written"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "LeafName" && Equals(write.Value, "leaf-written"));
        Assert.Contains("BaseName", leaf.Properties.Keys);
        Assert.Contains("LeafName", leaf.Properties.Keys);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAPlainClassSitsBetweenAHandWrittenSubjectAndTheSubject_ThenTheWalkResolvesTheHandWrittenClass()
    {
        // Arrange: the ancestor carries no attribute and never names IInterceptorSubject directly,
        // it names IMySubject which derives from it. Only the transitive check on each declared
        // interface recognises that class as a subject.
        const string librarySource = """
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using Namotion.Interceptor;

            namespace Lib
            {
                public interface IMySubject : IInterceptorSubject { }

                public class HandWrittenSubject : IMySubject
                {
                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
                        new Dictionary<string, SubjectPropertyMetadata>();

                    public Namotion.Interceptor.Interceptors.IInterceptorExecutor Executor => throw new System.NotSupportedException();
                    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
                    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => DefaultProperties;
                    public void AddProperties(IEnumerable<SubjectPropertyMetadata> properties) { }
                }

                public class PlainInBetween : HandWrittenSubject { }
            }
            """;
        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Derived : Lib.PlainInBetween
                {
                    public partial string Q { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReferenceExpectingCleanCompilation(librarySource, mainSource);
        var derived = result.SingleSource();

        // Assert: the hand-written ancestor exposes a usable DefaultProperties but none of the
        // helpers, so it takes the NI0012 root-mode fallback. Pinned here because the mode is not
        // otherwise visible in the emitted shape, and it must not flip back unnoticed.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Contains(".Concat(global::Lib.HandWrittenSubject.DefaultProperties)", derived);
        Assert.DoesNotContain("global::Lib.PlainInBetween", derived);
    }

    [Fact]
    public void WhenSubjectIsSealedAndIsARoot_ThenProtectedMembersAreEmittedPrivate()
    {
        // Arrange: a sealed root emits protected RaisePropertyChanged today, which is CS0628 and
        // therefore a build error for consumers.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public sealed partial class SealedRoot
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.Contains("private void RaisePropertyChanged(string propertyName)", generated);
        Assert.DoesNotContain("protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)", generated);
    }

    [Fact]
    public void WhenSubclassIsHandWritten_ThenItCanUseTheProtectedHelpers()
    {
        // Arrange: one of the two directions goal 4 asks for. This was CS0122 on every helper the
        // subclass touches while the generator emitted them private, so the shape is pinned rather
        // than left to the emitter's modifier choice.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class GenBase
                {
                    public partial string BaseName { get; set; }
                }

                public class HandDerived : GenBase
                {
                    private string _own = "";

                    public HandDerived()
                    {
                        // Must run before the first intercepted write: PropertyReference.Metadata
                        // throws when the name is not registered.
                        ((IInterceptorSubject)this).AddProperties(
                            new SubjectPropertyMetadata(
                                nameof(Own),
                                typeof(string),
                                [],
                                o => ((HandDerived)o).Own,
                                (o, v) => ((HandDerived)o).Own = (string)v!,
                                isIntercepted: true,
                                isDynamic: false));
                    }

                    public string Own
                    {
                        get => GetPropertyValue(nameof(Own), static o => ((HandDerived)o)._own);
                        set => SetPropertyValue(nameof(Own), value, _own, static (o, v) => ((HandDerived)o)._own = v);
                    }

                    // The other two of the four members the design promises a hand-written subclass
                    // can reach.
                    public bool HasAddedProperties => GetInstanceProperties() is not null;

                    public string Describe(string prefix)
                        => (string)InvokeMethod(
                            nameof(Describe),
                            static (s, p) => (string)p[0]! + ((HandDerived)s)._own,
                            prefix)!;
                }
            }
            """;

        // Act & Assert
        GeneratorTestHost.RunExpectingNoWarnings(source);
    }

    [Fact]
    public void WhenHandWrittenSubclassWritesThroughTheHelpers_ThenTheInheritedExecutorInterceptsIt()
    {
        // Arrange: the test above proves the helpers are reachable, this one proves they work. A
        // hand-written subclass has no generated DefaultProperties, so its metadata only exists once
        // AddProperties has run, and the base's ": base(context)" constructor publishes the executor
        // before this constructor body starts. Registering after the first write would therefore
        // throw from PropertyReference.Metadata rather than silently skip interception.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class GenBase
                {
                    public partial string BaseName { get; set; }
                }

                public class HandDerived : GenBase
                {
                    public const string WrittenInConstructor = "written-in-constructor";

                    private string _own = "";

                    public HandDerived(IInterceptorSubjectContext context) : base(context)
                    {
                        ((IInterceptorSubject)this).AddProperties(
                            new SubjectPropertyMetadata(
                                nameof(Own),
                                typeof(string),
                                [],
                                o => ((HandDerived)o).Own,
                                (o, v) => ((HandDerived)o).Own = (string)v!,
                                isIntercepted: true,
                                isDynamic: false));

                        Own = WrittenInConstructor;
                    }

                    public string Own
                    {
                        get => GetPropertyValue(nameof(Own), static o => ((HandDerived)o)._own);
                        set => SetPropertyValue(nameof(Own), value, _own, static (o, v) => ((HandDerived)o)._own = v);
                    }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var subjectType = GeneratorTestHost.RunForExecution(source).LoadAssembly().GetType("Repro.HandDerived");
        Assert.NotNull(subjectType);
        var ownProperty = subjectType.GetProperty("Own");
        var baseNameProperty = subjectType.GetProperty("BaseName");
        Assert.NotNull(ownProperty);
        Assert.NotNull(baseNameProperty);

        // Act
        var subject = Activator.CreateInstance(subjectType, context);
        ownProperty.SetValue(subject, "written-after-construction");
        baseNameProperty.SetValue(subject, "base-written");

        // Assert: the hand-written property and the generated base property both reach the one
        // executor the root published.
        Assert.Equal("written-after-construction", ownProperty.GetValue(subject));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Own" && Equals(write.Value, "written-in-constructor"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Own" && Equals(write.Value, "written-after-construction"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "BaseName" && Equals(write.Value, "base-written"));
    }

    [Fact]
    public void WhenTheDocumentedHandWrittenBaseHostsAGeneratedSubclass_ThenItsWritesReachTheInterceptor()
    {
        // Arrange: the other of the two directions goal 4 asks for, and the only test that runs it.
        // The fixture is read out of docs/generator.md instead of being copied here, so the
        // contract the documentation asks a base class to satisfy and the contract this test proves
        // cannot drift apart.
        var source = ReadDocumentedConformingBaseFixture();

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var result = GeneratorTestHost.RunForExecution(source);
        var machineType = result.LoadAssembly().GetType("Machine");
        Assert.NotNull(machineType);
        var machine = (IInterceptorSubject)Activator.CreateInstance(machineType, context)!;

        // Act
        machineType.GetProperty("SerialNumber")!.SetValue(machine, "serial-written");

        // Assert: the generated setter routes through the hand-written base's SetPropertyValue and
        // lands on the executor the base's Context published. The field count is what shows it is
        // the base's and not a second copy emitted into the subclass, and the warning check is what
        // shows that second copy is not merely unused but absent: a private helper hiding the
        // inherited protected one is CS0108 in a file the consumer cannot edit.
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "SerialNumber" && Equals(write.Value, "serial-written"));
        Assert.Contains("SerialNumber", machine.Properties.Keys);
        Assert.Equal(1, CountExecutorFields(machineType));
        Assert.DoesNotContain(result.GeneratorDiagnostics, diagnostic => diagnostic.Id is "NI0011" or "NI0012");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseSubjectIsInAReferencedAssembly_ThenTheDerivedSubjectSharesItsInterceptionMembers()
    {
        // Arrange: mode selection branch 2. The library is compiled WITH the generator, so its
        // protected helpers exist as metadata symbols the contract check can see.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public partial class LibraryBase
                {
                    public partial string BaseName { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.LibraryBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource, runGeneratorOverLibrary: true);
        var generated = result.SingleSource();

        // Assert: derived mode, so no interception members of its own. Both members below are emitted by root
        // mode only, unlike the Properties line, which both modes emit identically.
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.DoesNotContain("private IInterceptorExecutor? _executor;", generated);
        Assert.DoesNotContain("void IInterceptorSubject.AddProperties", generated);
    }

    [Fact]
    public void WhenBaseSubjectIsInAReferencedAssembly_ThenABaseDeclaredWriteReachesTheInterceptor()
    {
        // Arrange: the same shape as above, executed. The emitted text cannot show this: the base
        // property's setter was compiled into the library against the library's own interception members, and
        // only running it shows that it reaches the executor the leaf's context published rather
        // than a second one the leaf kept for itself. This is what a consumer deriving from a
        // subject shipped in a package hits, and the contract check reads the base from metadata
        // here rather than from source.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public partial class LibraryBase
                {
                    public partial string BaseName { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.LibraryBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("App.AppLeaf");
        Assert.NotNull(leafType);
        var leaf = (IInterceptorSubject)Activator.CreateInstance(leafType, context)!;

        // Act
        leafType.GetProperty("BaseName")!.SetValue(leaf, "base-written");
        leafType.GetProperty("LeafName")!.SetValue(leaf, "leaf-written");

        // Assert
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "BaseName" && Equals(write.Value, "base-written"));
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "LeafName" && Equals(write.Value, "leaf-written"));
        Assert.Contains("BaseName", leaf.Properties.Keys);
        Assert.Contains("LeafName", leaf.Properties.Keys);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenReferencedBaseHasPrivateHelpers_ThenItFallsBackToRootModeWithNI0012()
    {
        // Arrange: an attributed base built by an older generator, so its helpers are private and it
        // has no GetInstanceProperties at all. Either cause alone fails the contract, so the
        // fallback is what the assertions pin, not one specific missing member.
        // Branch 1's "declared in source" qualifier is what stops this from selecting derived mode
        // and emitting CS0122 calls into generated code. The generator is NOT run over the library.
        const string librarySource = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.ComponentModel;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Library
            {
                [InterceptorSubject]
                public class StaleBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
                {
                    private IInterceptorExecutor? _executor;
                    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                    // The old generator paired its private helpers with a protected RaisePropertyChanged,
                    // so the fixture carries both: the attribute alone makes the subclass treat the base
                    // as the INPC owner and call RaisePropertyChanged by simple name.
                    public event PropertyChangedEventHandler? PropertyChanged;

                    protected void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);

                    IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties ?? DefaultProperties;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                        => _properties = (_properties ?? DefaultProperties)
                            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                            .ToFrozenDictionary();

                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    private TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                        => _executor is not null ? _executor.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                    private bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                    {
                        if (_executor is null)
                        {
                            setValue(this, newValue);
                            return true;
                        }

                        return _executor.SetPropertyValue(propertyName, newValue, currentValue, setValue);
                    }

                    private object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                        => _executor is not null ? _executor.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.StaleBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource);

        // Assert: warning, root mode, still compiles, and no stray 'new' that would be CS0109.
        // The private base helpers neither hide nor bind across the assembly boundary, so the
        // warning check is what pins the modifier decision: CS0109 is a warning, not an error.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Contains("private IInterceptorExecutor? _executor;", result.SingleSource());
    }

    [Fact]
    public void WhenHandWrittenBaseIsGeneric_ThenTheContractIsCheckedWithTypeArgumentsSubstituted()
    {
        // Arrange: the subject derives from a constructed GenericBase<SubjectPropertyMetadata>, so
        // the contract lookup has to see the substituted members, not the open definition's.
        // DefaultProperties is declared in terms of T on purpose: its type only equals the
        // IReadOnlyDictionary<string, SubjectPropertyMetadata> the check compares against once the
        // type argument is substituted, so a lookup running against the open definition fails.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.ComponentModel;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                public class GenericBase<T> : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
                {
                    private IInterceptorExecutor? _executor;
                    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                    public event PropertyChangedEventHandler? PropertyChanged;
                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                    public static IReadOnlyDictionary<string, T> DefaultProperties { get; }
                        = FrozenDictionary<string, T>.Empty;

                    IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
                        => GetInstanceProperties() ?? FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                    {
                        _properties = ((IInterceptorSubject)this).Properties
                            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                            .ToFrozenDictionary();
                    }

                    protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

                    protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                        => _executor is not null ? _executor.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                    protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                    {
                        if (_executor is null)
                        {
                            setValue(this, newValue);
                            return true;
                        }

                        return _executor.SetPropertyValue(propertyName, newValue, currentValue, setValue);
                    }

                    protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                        => _executor is not null ? _executor.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
                }

                [InterceptorSubject]
                public partial class GenericDerived : GenericBase<SubjectPropertyMetadata>
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert: derived mode, so no interception members of its own, and no contract diagnostic.
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0011" || d.Id == "NI0012");
        Assert.DoesNotContain("private IInterceptorExecutor? _executor;", result.SingleSource());
    }

    [Fact]
    public void WhenSubjectsAreInternalAndNested_ThenTheLeafTakesDerivedModeWithItsDeclaredAccessibility()
    {
        // Arrange: accessibility is checked with IsSymbolAccessibleWithin, and nested containing
        // types are re-declared by the generator, so both interact with the derived-mode split.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public partial class Container
                {
                    [InterceptorSubject]
                    internal partial class NestedRoot
                    {
                        public partial string RootName { get; set; }
                    }

                    [InterceptorSubject]
                    private protected partial class NestedLeaf : NestedRoot
                    {
                        public partial string LeafName { get; set; }
                    }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("NestedLeaf")).SourceText.ToString();

        // Assert: the private protected leaf reaches the internal root's protected helpers, so it
        // still takes derived mode through two re-declared containing types.
        Assert.Contains("private protected partial class NestedLeaf : IInterceptorSubject", leaf);
        Assert.DoesNotContain("private IInterceptorExecutor? _executor;", leaf);
    }

    [Fact]
    public void WhenRootModeSitsOnAnMvvmBase_ThenTheRedeclaredNotifyMembersCarryNew()
    {
        // Arrange: an ordinary MVVM base. It is not a subject ancestor, so root mode re-emits the
        // whole notify block, and both members it emits already exist above it. The base also
        // carries an interception member name to cover the helper half of the same lookup.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class ViewModelBase : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public object? InvokeMethod { get; set; }

                    protected void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class OnViewModelBase : ViewModelBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert: without the modifiers this is three CS0108 in a file the consumer cannot edit.
        Assert.Contains("new public event PropertyChangedEventHandler? PropertyChanged;", generated);
        Assert.Contains("new protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("new protected object? InvokeMethod(", generated);
    }

    [Fact]
    public void WhenTheCollidingBaseMemberIsStatic_ThenTheRedeclaredMemberStillCarriesNew()
    {
        // Arrange: C# hiding is not staticness-sensitive, so a static base member of an interception member name is hidden by the emitted instance member exactly like an instance one.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class StaticHolderBase
                {
                    public static object? GetInstanceProperties { get; set; }

                    protected static void RaisePropertyChanged(string propertyName) { }
                }

                [InterceptorSubject]
                public partial class OnStaticHolderBase : StaticHolderBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.Contains("new protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()", generated);
        Assert.Contains("new protected void RaisePropertyChanged(string propertyName)", generated);
    }

    [Fact]
    public void WhenTheBaseRaiseTakesEventArgs_ThenNoNewModifierIsEmitted()
    {
        // Arrange: RaisePropertyChanged(PropertyChangedEventArgs) shares only the name with the
        // emitted RaisePropertyChanged(string), so C# hiding does not apply to it. A 'new' here
        // would be CS0109, which fails a consumer build exactly like the CS0108 it guards against.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class ArgsOnlyBase
                {
                    protected void RaisePropertyChanged(PropertyChangedEventArgs args) { }
                }

                [InterceptorSubject]
                public partial class OnArgsOnlyBase : ArgsOnlyBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.DoesNotContain("new protected void RaisePropertyChanged", generated);
        Assert.Contains("protected void RaisePropertyChanged(string propertyName)", generated);
    }

    /// <summary>
    /// The single fenced code block in docs/generator.md that holds the base class
    /// satisfying the whole contract, together with the generated subclass it hosts.
    /// </summary>
    private static string ReadDocumentedConformingBaseFixture()
    {
        var blocks = new List<string>();
        var currentBlock = new List<string>();
        var insideBlock = false;

        foreach (var line in File.ReadAllLines(FindRepositoryFile(Path.Combine("docs", "generator.md"))))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (insideBlock)
                {
                    blocks.Add(string.Join(Environment.NewLine, currentBlock));
                    currentBlock.Clear();
                }

                insideBlock = !insideBlock;
                continue;
            }

            if (insideBlock)
            {
                currentBlock.Add(line);
            }
        }

        return Assert.Single(blocks, block => block.Contains("class TrackedEntityBase", StringComparison.Ordinal));
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not find '{relativePath}' in any directory above '{AppContext.BaseDirectory}'. This test " +
            "reads its fixture from docs/generator.md so the documented base cannot drift from the " +
            "tested one, which requires running inside the repository tree.");
    }

    /// <summary>
    /// Counts the executor fields over the whole hierarchy. A hand-written base that really hosts
    /// its generated subclass carries the only one; a subclass that emits its own interception members instead
    /// adds a second that nothing above it ever populates.
    /// </summary>
    private static int CountExecutorFields(Type type)
    {
        var count = 0;
        for (var current = type; current is not null; current = current.BaseType)
        {
            count += current
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Count(field => field.Name == "_executor");
        }

        return count;
    }
}
