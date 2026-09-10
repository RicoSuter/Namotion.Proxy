using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseDiagnosticsTests
{
    /// <summary>
    /// A generated root plus a generated leaf, with <c>{0}</c> replaced by the leaf member under
    /// test. Every NI0013 and NI0014 case is this shape with one member swapped.
    /// </summary>
    private const string LeafMemberTemplate = """
        using System;
        using System.Collections.Generic;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Attributes;

        namespace Repro
        {
            [InterceptorSubject]
            public partial class RootSubject
            {
                public partial string RootName { get; set; }
            }

            [InterceptorSubject]
            public partial class LeafSubject : RootSubject
            {
                public partial string LeafName { get; set; }

                {0}
            }
        }
        """;

    private static string LeafDeclaring(string memberDeclaration)
        => LeafMemberTemplate.Replace("{0}", memberDeclaration);

    private const string NonConformingBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _executor;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    private const string DefaultPropertiesOnlyBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _executor;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// Same as <see cref="DefaultPropertiesOnlyBase"/> plus an unrelated overload of one of the four
    /// interception member names. C# hides a method by signature, so this overload hides nothing the generator
    /// emits and a 'new' modifier on the emitted member would be CS0109.
    /// </summary>
    private const string DifferentSignatureOverloadBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _executor;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                protected int GetInstanceProperties(int unused) => unused;

                IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// Same as <see cref="DefaultPropertiesOnlyBase"/> plus a GetInstanceProperties whose signature
    /// does match the emitted one, so the emitted member hides it and needs the 'new' modifier.
    /// </summary>
    private const string MatchingSignatureMemberBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _executor;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

                IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// A DefaultProperties whose display string mentions SubjectPropertyMetadata but which the
    /// emitted .Concat(...) cannot consume.
    /// </summary>
    private const string WronglyTypedDefaultPropertiesBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _executor;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyList<SubjectPropertyMetadata> DefaultProperties { get; }
                    = new List<SubjectPropertyMetadata>();

                IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// The same correctly typed DefaultProperties as <see cref="DefaultPropertiesOnlyBase"/>, but
    /// declared as a field. This shape compiles today, so it must not become an error.
    /// </summary>
    private const string DefaultPropertiesFieldBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _executor;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    /// <summary>
    /// A base that satisfies every clause of the contract except one: SetPropertyValue returns void
    /// where the generated setter needs a bool. This is the single-typo shape a hand-written base
    /// realistically has, and the name, arity and parameter count all still match.
    /// </summary>
    private const string WrongReturnTypeBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.ComponentModel;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
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

                protected void SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                {
                    if (_executor is null)
                    {
                        setValue(this, newValue);
                        return;
                    }

                    _executor.SetPropertyValue(propertyName, newValue, currentValue, setValue);
                }

                protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                    => _executor is not null ? _executor.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
            }
        }
        """;

    /// <summary>
    /// A base that satisfies every clause of the contract and returns the FrozenDictionary it holds
    /// from GetInstanceProperties rather than the declared interface. The emitted
    /// "GetInstanceProperties() ?? DefaultProperties" consumes that just as happily, which is why
    /// the DefaultProperties half of the same expression has always accepted both forms.
    /// </summary>
    private const string ImplementingInstancePropertiesBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.ComponentModel;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
            {
                private IInterceptorExecutor? _executor;
                private FrozenDictionary<string, SubjectPropertyMetadata>? _properties;

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

                protected FrozenDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

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

    /// <summary>
    /// A base that satisfies every clause of the contract except that GetInstanceProperties returns a
    /// struct implementing the dictionary interface. The emitted
    /// "GetInstanceProperties() ?? DefaultProperties" rejects a value type as its left operand with
    /// CS0019, so accepting this base puts a raw compiler error into a generated file.
    /// </summary>
    private const string ValueTypeInstancePropertiesBase = """
        using System;
        using System.Collections;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.ComponentModel;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public readonly struct PropertyMap : IReadOnlyDictionary<string, SubjectPropertyMetadata>
            {
                public SubjectPropertyMetadata this[string key] => throw new KeyNotFoundException();
                public IEnumerable<string> Keys => Array.Empty<string>();
                public IEnumerable<SubjectPropertyMetadata> Values => Array.Empty<SubjectPropertyMetadata>();
                public int Count => 0;
                public bool ContainsKey(string key) => false;

                public bool TryGetValue(string key, out SubjectPropertyMetadata value)
                {
                    value = default;
                    return false;
                }

                public IEnumerator<KeyValuePair<string, SubjectPropertyMetadata>> GetEnumerator()
                    => Enumerable.Empty<KeyValuePair<string, SubjectPropertyMetadata>>().GetEnumerator();

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public class HandBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
            {
                private IInterceptorExecutor? _executor;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                public event PropertyChangedEventHandler? PropertyChanged;

                void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
                    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties ?? DefaultProperties;

                void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                    => _properties = ((IInterceptorSubject)this).Properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                protected PropertyMap GetInstanceProperties() => default;

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

    /// <summary>
    /// The ordinary hand-written subject: it satisfies the contract with plain public members and
    /// derives from object, so it has nothing above it whose interface slot it could take. This is
    /// the shape NI0014 must stay silent on, and it has to keep intercepting.
    /// </summary>
    private const string PublicMemberBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.ComponentModel;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
            {
                private IInterceptorExecutor? _executor;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                public event PropertyChangedEventHandler? PropertyChanged;

                public void RaisePropertyChanged(string propertyName)
                    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
                public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => GetInstanceProperties() ?? DefaultProperties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
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

    /// <summary>
    /// <see cref="PublicMemberBase"/> with a virtual Executor, which is what an intermediate class
    /// needs in order to override it rather than hide it.
    /// </summary>
    private static readonly string VirtualExecutorBase = PublicMemberBase.Replace(
        "public IInterceptorExecutor Executor",
        "public virtual IInterceptorExecutor Executor");

    private const string GeneratedDerived = """

        namespace Repro
        {
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class GenDerived : HandBase
            {
                public partial string Name { get; set; }
            }
        }
        """;

    private const string ExecutorWrapperDerived = """

        namespace Repro
        {
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class GenDerived : HandBase
            {
                public partial string Name { get; set; }

                public string ExecutorWithoutInterceptor(string tag) => tag;
            }
        }
        """;

    private const string OverridingIntermediateDerived = """

        namespace Repro
        {
            public class Middle : HandBase
            {
                public override IInterceptorExecutor Executor => base.Executor;
            }

            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class GenDerived : Middle
            {
                public partial string Name { get; set; }
            }
        }
        """;

    [Fact]
    public void WhenBaseImplementsTheInterfaceWithoutTheContract_ThenNI0011IsReported()
    {
        // Arrange: no DefaultProperties, no helpers. Today this shape dies on CS0117 inside
        // generated code, which the user cannot edit.
        var source = NonConformingBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS0117");
    }

    [Fact]
    public void WhenBaseHasOnlyDefaultProperties_ThenNI0012IsReportedAndItStillCompiles()
    {
        // Arrange: this shape compiles and works today, so it must not become an error.
        var source = DefaultPropertiesOnlyBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: warning, root-mode fallback, and no modifier mistake in either direction. Both
        // CS0108 and CS0109 are warnings, so naming one of them lets the other through, and a
        // consumer's TreatWarningsAsErrors fails on either.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseDeclaresADifferentSignatureOverloadOfAnInterceptionMemberName_ThenNoStrayNewModifierIsEmitted()
    {
        // Arrange: the base takes the NI0012 root-mode fallback and declares GetInstanceProperties(int),
        // which hides nothing because C# hides methods by signature.
        var source = DifferentSignatureOverloadBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: no stray 'new' on the emitted members, and no missing one either. CS0108 is a
        // warning like CS0109, so asserting the absence of one alone would pass with the other.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationWarnings);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenBaseDeclaresAMatchingSignatureOfAnInterceptionMemberName_ThenTheNewModifierIsStillEmitted()
    {
        // Arrange: the counterpart of the overload case. Narrowing the hiding check to a signature
        // match must not stop the modifier being emitted where C# does require it (CS0108).
        var source = MatchingSignatureMemberBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Contains("new protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()", result.AllSources());
        Assert.DoesNotContain(result.CompilationWarnings, d => d.Id is "CS0108" or "CS0109");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenBaseDeclaresDefaultPropertiesOfAnUnusableType_ThenNI0011IsReported()
    {
        // Arrange: IReadOnlyList<SubjectPropertyMetadata> mentions the metadata type but the emitted
        // .Concat(...) cannot consume it, which is the CS1929 the contract check exists to replace.
        var source = WronglyTypedDefaultPropertiesBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS1929");
    }

    [Fact]
    public void WhenBaseDeclaresDefaultPropertiesAsAField_ThenItIsAcceptedAndNI0012IsReported()
    {
        // Arrange: a static readonly field of the right type is usable by the emitted .Concat(...)
        // exactly like a property, so it must not be rejected outright.
        var source = DefaultPropertiesFieldBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void WhenAttributedAncestorIsNotPartial_ThenTheDerivedSubjectDoesNotAssumeGeneratedInterceptionMembers()
    {
        // Arrange: the ancestor carries the attribute but NI0001 suppresses its generation, so none
        // of the members the derived class would inherit ever exists.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public class NonPartialBase
                {
                }

                [InterceptorSubject]
                public partial class GenDerived : NonPartialBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: one actionable diagnostic per class instead of a wall of raw errors in code the
        // user cannot edit.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0001");
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain("GetPropertyValue", result.AllSources());
        Assert.DoesNotContain("NonPartialBase.DefaultProperties", result.AllSources());
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id is "CS0535" or "CS0117" or "CS0103");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAGeneratedMemberName_ThenNI0013IsReported()
    {
        // Arrange: a 'new' annotated member of the same shape captures the generated call and
        // produces no compiler diagnostic at all, which is why the rule is name-only.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }
                }

                [InterceptorSubject]
                public partial class LeafSubject : RootSubject
                {
                    public partial string LeafName { get; set; }

                    protected new IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => null;
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: the capture is invisible to the compiler, so NI0013 is the only signal there is.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0013");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPublicData_ThenNI0014IsReported()
    {
        // Arrange: the derived class re-lists the interface, so its public member takes the Data
        // slot from the root's explicit implementation.
        const string source = """
            using System.Collections.Concurrent;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }
                }

                [InterceptorSubject]
                public partial class LeafSubject : RootSubject
                {
                    public partial string LeafName { get; set; }

                    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
    }

    [Fact]
    public void WhenRootSubjectDeclaresAPublicData_ThenNoDiagnosticIsReported()
    {
        // Arrange: interface mapping prefers a class's own explicit implementation over its own
        // public members, so the root is never hijacked by its own member.
        const string source = """
            using System.Collections.Concurrent;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }

                    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0013" or "NI0014");
    }

    [Fact]
    public void WhenAHandWrittenSubjectSitsBetweenTwoGeneratedSubjects_ThenTheLeafFallsBackToRootMode()
    {
        // Arrange: the middle re-implements IInterceptorSubject by hand, so its Context wins the
        // interface map while the root's helpers still read the root's never-populated field.
        // Selecting derived mode here would reproduce the bug this whole change fixes.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class GenRoot
                {
                    public partial string RootName { get; set; }
                }

                public class HandMiddle : GenRoot, IInterceptorSubject
                {
                    private IInterceptorExecutor? _executor;

                    // Present so the leaf reaches mode selection at all: without it the middle fails
                    // the contract outright and NI0011 suppresses the leaf's generation, which never
                    // exercises the choice between root and derived mode.
                    public static new IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
                        => FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }
                }

                [InterceptorSubject]
                public partial class GenLeaf : HandMiddle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.GenLeaf.g.cs")).SourceText.ToString();

        // Assert: root mode, so the leaf owns its own executor rather than reading one nothing fills.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id is "NI0011" or "NI0012");
        Assert.Contains("private IInterceptorExecutor? _executor;", leaf);

        // Everything the leaf re-emits hides a member of the generated root, and every one of those
        // is a CS0108 in a file the consumer cannot edit, which their TreatWarningsAsErrors turns
        // into a build failure. The two INPC members are asserted by name because they are gated by
        // BaseClassHasInpc rather than by the symbol lookup the four helpers go through.
        Assert.Contains("new public event PropertyChangedEventHandler? PropertyChanged;", leaf);
        Assert.Contains("new protected void RaisePropertyChanged(string propertyName)", leaf);
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAnIntermediateClassDeclaresAPrivateGeneratedMemberName_ThenNI0013IsNotReported()
    {
        // Arrange: a private member on an intermediate neither hides nor is found by member lookup,
        // so nothing is captured and firing an error would be a pure false positive.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }
                }

                public class PlainMiddle : RootSubject
                {
                    private string InvokeMethod = "";
                }

                [InterceptorSubject]
                public partial class LeafSubject : PlainMiddle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0013");
    }

    [Fact]
    public void WhenBaseDefaultPropertiesHasTheWrongType_ThenNI0011IsReportedRatherThanACompilerError()
    {
        // Arrange: goal 5. Accepting any static named DefaultProperties lets this through and the
        // generated .Concat(...) then fails with CS1929 inside code the user cannot edit.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                public class HandBase : IInterceptorSubject
                {
                    private IInterceptorExecutor? _executor;

                    public static int DefaultProperties { get; } = 0;

                    IInterceptorExecutor IInterceptorSubject.Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
                        => FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }
                }

                [InterceptorSubject]
                public partial class GenDerived : HandBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS1929");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAStaticGeneratedMemberName_ThenNI0013IsReportedAndTheCallIsCaptured()
    {
        // Arrange: C# hiding is not staticness sensitive, and calling a static by simple name from an
        // instance body is legal, so this captures the generated call with no compiler diagnostic.
        var source = LeafDeclaring(
            "private new static IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => null;");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");
        leaf.AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true));

        // Assert: the added property is swallowed, and NI0013 is the only signal there is.
        Assert.False(leaf.Properties.ContainsKey("Extra"));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0013");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresNothingUnusual_ThenNeitherRuleFiresAndAddedPropertiesSurvive()
    {
        // Arrange: the control for the capture and hijack cases below.
        var source = LeafDeclaring("");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");
        leaf.AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true));

        // Assert
        Assert.True(leaf.Properties.ContainsKey("Extra"));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0013" or "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPartialPropertyNamedLikeAnInterfaceMember_ThenNI0014IsNotReported()
    {
        // Arrange: a string Data cannot implement IInterceptorSubject.Data, so the root keeps the
        // slot. Every subject used to emit its own explicit implementation, so a property named Data
        // compiled and worked before, and Data is a plausible name on an industrial model.
        var source = LeafDeclaring("public partial string Data { get; set; }");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");

        // Assert: the interface slot is still the root's dictionary, so nothing was taken.
        Assert.NotNull(leaf.Data);
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPropertyOfTheWrongTypeNamedLikeAnInterfaceMember_ThenNI0014IsNotReported()
    {
        // Arrange: object is not ConcurrentDictionary<(string?, string), object?>, so this is not an
        // implicit implementation and the interface mapping falls back to the root's.
        var source = LeafDeclaring("public object Data { get; } = new object();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var instance = result.CreateInstance("Repro.LeafSubject");
        var ownData = instance.GetType().GetProperty("Data")!.GetValue(instance);

        // Assert: the two are different objects, which is the proof the slot was not taken.
        Assert.False(ReferenceEquals(((IInterceptorSubject)instance).Data, ownData));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAMethodWithTheWrongReturnTypeNamedLikeAnInterfaceMember_ThenNI0014IsNotReported()
    {
        // Arrange: the return type is part of what makes an implicit implementation, so a bool
        // returning AddProperties does not take the void returning interface member's slot.
        var source = LeafDeclaring(
            "public bool AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => true;");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");
        leaf.AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true));

        // Assert: the root's implementation still runs, so the property is really added.
        Assert.True(leaf.Properties.ContainsKey("Extra"));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPublicData_ThenTheInterfaceSlotIsReallyTaken()
    {
        // Arrange: the counterpart of the false positive cases. The property matches the interface
        // member exactly, so it is an implicit implementation and NI0014 is justified.
        var source = LeafDeclaring(
            "public System.Collections.Concurrent.ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var instance = result.CreateInstance("Repro.LeafSubject");
        var ownData = instance.GetType().GetProperty("Data")!.GetValue(instance);

        // Assert
        Assert.Same(ownData, ((IInterceptorSubject)instance).Data);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
    }

    [Fact]
    public void WhenDerivedSubjectHandWritesTheExecutorImplementation_ThenNI0014IsReported()
    {
        // Arrange: this compiles with no diagnostic and kills interception entirely, not only on
        // base declared properties, because writes still land in the backing fields.
        var source = LeafDeclaring(
            "Namotion.Interceptor.Interceptors.IInterceptorExecutor IInterceptorSubject.Executor => null!;");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
    }

    [Fact]
    public void WhenAWrapperWouldBeNamedLikeAnInheritedInterceptionMember_ThenNI0006IsReportedAndThePropertiesSurvive()
    {
        // Arrange: stripping the postfix yields "GetInstanceProperties", the inherited helper the
        // generated IInterceptorSubject.Properties calls. Emitting the wrapper captures that call,
        // so Properties reports whatever the wrapper returns and the registry sees nothing, while
        // writes keep working and hide the breakage.
        var source = LeafDeclaring(
            "public IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstancePropertiesWithoutInterceptor()" +
            " => new Dictionary<string, SubjectPropertyMetadata>();");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0006");
        Assert.Equal(["LeafName", "RootName"], leaf.Properties.Keys.OrderBy(name => name));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseDeclaresAnAccessorHelperWithTheWrongReturnType_ThenTheContractRejectsIt()
    {
        // Arrange: every accessor helper is present and only SetPropertyValue returns void. The
        // generated setter tests that return value in "!cancel && SetPropertyValue(...)", so
        // accepting this base means CS0019 inside a generated file, which is exactly the outcome
        // the contract check exists to replace.
        var source = WrongReturnTypeBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: rejected by the contract, so the subject falls back to its own interception members (NI0012)
        // instead of calling the base helper that does not fit. The message names the one member
        // that failed, which is what tells this base apart from the four other defects that reach
        // the same rule.
        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Contains("bool SetPropertyValue", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperWouldBeNamedAddProperties_ThenNI0006IsReportedAndAddedPropertiesSurvive()
    {
        // Arrange: the wrapper would be emitted as a public void AddProperties(IEnumerable<...>),
        // and 'params' is not part of a signature, so it is an implicit implementation of
        // IInterceptorSubject.AddProperties and takes the slot from the root's explicit one, because
        // a derived subject re-lists the interface. Neither the generator nor the compiler says
        // anything about that, which makes it quieter than the capture the guard was written for.
        var source = LeafDeclaring(
            "public void AddPropertiesWithoutInterceptor(params IEnumerable<SubjectPropertyMetadata> properties) { }");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = (IInterceptorSubject)result.CreateInstance("Repro.LeafSubject");
        leaf.AddProperties(new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true));

        // Assert: the root's implementation still runs, which is the only evidence that the wrapper
        // was really dropped. With the wrapper emitted, this call is a silent no-op.
        Assert.True(leaf.Properties.ContainsKey("Extra"));
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0006");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperWouldBeNamedInvokeMethodAtAnotherArity_ThenNI0006IsReportedAndTheRealBodyRuns()
    {
        // Arrange: the accessor helper InvokeMethod ends in "params object?[]", so the generated call site
        // for a parameterless method, InvokeMethod("Echo", lambda), passes two arguments. A
        // two-parameter overload is applicable in normal form and therefore beats the helper, which
        // is only applicable in expanded form, so the wrapper swallows the call. Nothing in the
        // compiler says a word about it and Echo() returns the wrapper's answer instead of "echo".
        var source = LeafDeclaring("""
                public string EchoWithoutInterceptor() => "echo";

                public object InvokeMethodWithoutInterceptor(string name, Action<IInterceptorSubject, object[]> callback)
                    => "HIJACKED:" + name;
            """);

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leafType = result.LoadAssembly().GetType("Repro.LeafSubject");
        Assert.NotNull(leafType);
        var instance = Activator.CreateInstance(leafType)!;

        // Assert: the real body runs, which is the only evidence there is. The value is what the
        // capture changes, and no diagnostic accompanies it.
        Assert.Equal("echo", leafType.GetMethod("Echo", Type.EmptyTypes)!.Invoke(instance, []));
        Assert.Null(leafType.GetMethod("InvokeMethod", [typeof(string), typeof(Action<IInterceptorSubject, object[]>)]));
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0006");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperSharesAnInterceptionMemberNameButNotItsArity_ThenNI0006IsReportedAndNoWrapperIsEmitted()
    {
        // Arrange: the deliberate inversion of a rule that used to compare the arity and let these
        // two through. Since InvokeMethod takes a parameter array, no arity is safe, and the guard no
        // longer reasons about signatures at all. Both wrappers are the accepted false positive: the
        // author is told to rename, which is loud and recoverable, unlike the capture above. Echo is
        // here to show that the helper still binds once they are gone.
        var source = LeafDeclaring("""
                public object InvokeMethodWithoutInterceptor(string name, object[] arguments) => name;

                public string GetPropertyValueWithoutInterceptor(string key) => key;

                public string EchoWithoutInterceptor(string value) => value;
            """);

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leafType = result.LoadAssembly().GetType("Repro.LeafSubject");
        Assert.NotNull(leafType);
        var instance = Activator.CreateInstance(leafType)!;

        // Assert
        Assert.Null(leafType.GetMethod("InvokeMethod", [typeof(string), typeof(object[])]));
        Assert.Null(leafType.GetMethod("GetPropertyValue", [typeof(string)]));
        Assert.Equal("v", leafType.GetMethod("Echo", [typeof(string)])!.Invoke(instance, ["v"]));

        var skipped = result.GeneratorDiagnostics
            .Where(d => d.Id == "NI0006")
            .Select(d => d.GetMessage())
            .ToList();

        Assert.Equal(2, skipped.Count);
        Assert.Contains(skipped, message => message.Contains("InvokeMethodWithoutInterceptor") && message.Contains("rename"));
        Assert.Contains(skipped, message => message.Contains("GetPropertyValueWithoutInterceptor") && message.Contains("rename"));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperIsNamedLikeAnExplicitlyImplementedInterfaceProperty_ThenNI0006IsReported()
    {
        // Arrange: the deliberate inversion of an exemption for the interface members. Those are
        // explicit interface properties in a generated root, where a method of the same name really
        // does collide with nothing, but the exemption was keyed on the name rather than on the base,
        // so it applied just as much to a hand-written base that exposes them publicly, where the
        // wrapper is a CS0108. The name is now enough on its own.
        var source = LeafDeclaring("public string DataWithoutInterceptor(string tag) => tag;");

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var leaf = result.CreateInstance("Repro.LeafSubject");

        // Assert: no wrapper, and the interface slot is still the root's dictionary.
        Assert.Null(leaf.GetType().GetMethod("Data", [typeof(string)]));
        Assert.NotNull(((IInterceptorSubject)leaf).Data);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0006");
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAWrapperWouldBeNamedExecutorOnABaseWithPublicMembers_ThenNI0006IsReportedAndNothingIsHidden()
    {
        // Arrange: the half of the exemption that was not merely unnecessary but unsound. This base
        // satisfies the contract with public members, which is the shape generator.md
        // documents, so an "Executor" wrapper hides the inherited public property. CS0108 lands in a
        // generated file the consumer cannot edit and fails any build with TreatWarningsAsErrors.
        var source = PublicMemberBase + ExecutorWrapperDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0006");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAnIntermediateOverridesAPublicExecutor_ThenNoDiagnosticIsReportedAndWritesAreIntercepted()
    {
        // Arrange: an override occupies the slot the overridden member already had, so it displaces
        // nothing, and virtual dispatch makes it the implementation rather than a replacement for
        // one. Reporting it was an NI0014, an error, on a hierarchy that builds and intercepts.
        var source = VirtualExecutorBase + OverridingIntermediateDerived;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var derivedType = result.LoadAssembly().GetType("Repro.GenDerived");
        Assert.NotNull(derivedType);
        var derived = Activator.CreateInstance(derivedType, context)!;
        derivedType.GetProperty("Name")!.SetValue(derived, "n");

        // Assert
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "Name" && Equals(w.Value, "n"));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseGetInstancePropertiesReturnsAnImplementingType_ThenTheContractAcceptsIt()
    {
        // Arrange: the base returns FrozenDictionary<string, SubjectPropertyMetadata>?, which the
        // emitted "GetInstanceProperties() ?? DefaultProperties" consumes exactly like the interface.
        var source = ImplementingInstancePropertiesBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: derived mode. Comparing the return type by identity sent this base to the NI0012
        // root-mode fallback, which costs the base's own properties their interception, the very
        // failure the shared interception members exist to fix.
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0011" or "NI0012");
        Assert.DoesNotContain("private IInterceptorExecutor? _executor;", result.AllSources());
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAReferencedSubjectIsSubclassedByHandWithAPublicExecutor_ThenNI0014IsReportedAndWritesAreNotIntercepted()
    {
        // Arrange: the hand-written class satisfies the contract by inheriting the referenced
        // subject's interception members, so it declares no explicit implementation of its own and its public
        // Executor wins the slot for every generated subclass. The root's helpers keep reading the
        // root's own field, which nothing populates through the hijacked slot, so interception is
        // silently lost, and it produces no compiler diagnostic at all.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Lib
            {
                [InterceptorSubject]
                public partial class LibRoot
                {
                    public partial string A { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Hand : Lib.LibRoot, IInterceptorSubject
                {
                    private Namotion.Interceptor.Interceptors.IInterceptorExecutor? _own;

                    public Namotion.Interceptor.Interceptors.IInterceptorExecutor Executor
                        => Namotion.Interceptor.Interceptors.InterceptorExecutor.GetOrCreate(ref _own, this);
                }

                [InterceptorSubject]
                public partial class HandLeaf : Hand
                {
                    public partial string B { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("Repro.HandLeaf");
        Assert.NotNull(leafType);
        var leaf = Activator.CreateInstance(leafType, context)!;
        leafType.GetProperty("B")!.SetValue(leaf, "b");

        // Assert: the write lands in the backing field and the executor never sees it, so the value
        // still looks right. NI0014 is the only signal there is.
        Assert.Equal("b", leafType.GetProperty("B")!.GetValue(leaf));
        Assert.Empty(writeInterceptor.Writes);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseImplementsTheContractWithPublicMembers_ThenNoDiagnosticIsReportedAndWritesAreIntercepted()
    {
        // Arrange: the counterpart of the case above and the shape that must not regress. The base
        // implements IInterceptorSubject with plain public members and derives from object, so there
        // is nothing above it whose slot those members could take.
        var source = PublicMemberBase + GeneratedDerived;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var derivedType = result.LoadAssembly().GetType("Repro.GenDerived");
        Assert.NotNull(derivedType);
        var derived = Activator.CreateInstance(derivedType, context)!;
        derivedType.GetProperty("Name")!.SetValue(derived, "n");

        // Assert
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "Name" && Equals(w.Value, "n"));
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0011" or "NI0012" or "NI0013" or "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAReferencedSubjectIsSubclassedByHandWithAnExplicitExecutor_ThenNI0014IsReportedAndWritesAreNotIntercepted()
    {
        // Arrange: the explicit form of the hijack, and the only one a hand-written ancestor can
        // express, because C# requires the class to list the interface itself (CS0540) and listing it
        // is what makes the class the contract provider. Exempting the explicit form therefore
        // exempted every base class there is, which left this shape reported by nothing at all.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Lib
            {
                [InterceptorSubject]
                public partial class LibRoot
                {
                    public partial string A { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Hand : Lib.LibRoot, IInterceptorSubject
                {
                    private Namotion.Interceptor.Interceptors.IInterceptorExecutor? _own;

                    Namotion.Interceptor.Interceptors.IInterceptorExecutor IInterceptorSubject.Executor
                        => Namotion.Interceptor.Interceptors.InterceptorExecutor.GetOrCreate(ref _own, this);
                }

                [InterceptorSubject]
                public partial class HandLeaf : Hand
                {
                    public partial string B { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("Repro.HandLeaf");
        Assert.NotNull(leafType);
        var leaf = Activator.CreateInstance(leafType, context)!;
        leafType.GetProperty("B")!.SetValue(leaf, "b");

        // Assert: the inherited helpers keep reading the root's field, which nothing populates, so
        // the write lands in the backing field and the value still looks right.
        Assert.Equal("b", leafType.GetProperty("B")!.GetValue(leaf));
        Assert.Empty(writeInterceptor.Writes);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenAHijackerSitsAboveTheContractProvider_ThenNI0014IsReportedAndWritesAreNotIntercepted()
    {
        // Arrange: Hand satisfies the contract by inheritance and declares nothing, so it is the
        // contract provider and the scan used to stop there. The public Executor that really takes
        // the slot sits one class further up, where nothing ever looked.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Lib
            {
                [InterceptorSubject]
                public partial class LibRoot
                {
                    public partial string A { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Middle : Lib.LibRoot, IInterceptorSubject
                {
                    private Namotion.Interceptor.Interceptors.IInterceptorExecutor? _own;

                    public Namotion.Interceptor.Interceptors.IInterceptorExecutor Executor
                        => Namotion.Interceptor.Interceptors.InterceptorExecutor.GetOrCreate(ref _own, this);
                }

                public class Hand : Middle, IInterceptorSubject
                {
                }

                [InterceptorSubject]
                public partial class HandLeaf : Hand
                {
                    public partial string B { get; set; }
                }
            }
            """;

        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var result = GeneratorTestHost.RunWithLibraryReferenceForExecution(librarySource, mainSource);
        var leafType = result.LoadAssembly().GetType("Repro.HandLeaf");
        Assert.NotNull(leafType);
        var leaf = Activator.CreateInstance(leafType, context)!;
        leafType.GetProperty("B")!.SetValue(leaf, "b");

        // Assert: the declarer named in the message is Middle, not the provider below it.
        Assert.Equal("b", leafType.GetProperty("B")!.GetValue(leaf));
        Assert.Empty(writeInterceptor.Writes);
        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "NI0014");
        Assert.Contains("Repro.Middle", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
    }

    [Fact]
    public void WhenBaseGetInstancePropertiesReturnsAValueType_ThenTheContractRejectsIt()
    {
        // Arrange: the counterpart of the FrozenDictionary case. Widening the return type to any
        // implementer of the dictionary interface also admitted a struct, and the emitted
        // "GetInstanceProperties() ?? DefaultProperties" then fails with CS0019 in a file the
        // consumer cannot edit, where the narrower rule gave a clean NI0012 fallback.
        var source = ValueTypeInstancePropertiesBase + GeneratedDerived;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: the compilation is checked first, because CS0019 in generated code is the damage
        // and the diagnostic is only the replacement for it.
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Contains("GetInstanceProperties", diagnostic.GetMessage());
    }
}
