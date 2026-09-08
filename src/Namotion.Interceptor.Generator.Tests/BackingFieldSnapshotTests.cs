using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class BackingFieldSnapshotTests
{
    [Fact]
    public void WhenTheGeneratedHierarchyIsDeep_ThenTheRootSnapshotHelperIsShared()
    {
        // Arrange
        var source = new System.Text.StringBuilder("using Namotion.Interceptor.Attributes; [InterceptorSubject] public partial class Layer0 {} ");
        for (var level = 1; level <= 20; level++)
            source.Append($"[InterceptorSubject] public partial class Layer{level} : Layer{level - 1} {{ public partial decimal Value{level} {{ get; set; }} }} ");

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source.ToString());

        // Assert
        Assert.Equal(21, result.Sources.Count);
        Assert.Single(result.Sources, generated => generated.SourceText.ToString().Contains(
            "protected TProperty GetPropertyValue<TProperty>(Func<IInterceptorSubject, TProperty> readValue)"));
    }

    [Fact]
    public void WhenPropertyTypesHaveDifferentAtomicity_ThenOnlyPotentiallyNonAtomicValuesUseSnapshots()
    {
        // Arrange
        const string source = """
            using System;
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Values
            {
                public partial int Integer { get; set; }
                public partial float Single { get; set; }
                public partial nint NativeInteger { get; set; }
                public partial string Text { get; set; }
                public partial object Boxed { get; set; }
                public partial IComparable Interface { get; set; }
                public partial long Long { get; set; }
                public partial ulong UnsignedLong { get; set; }
                public partial double Double { get; set; }
                public partial decimal Decimal { get; set; }
                public partial int? Nullable { get; set; }
                public partial DayOfWeek Enum { get; set; }
            }
            """;

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source).SingleSource();

        // Assert
        foreach (var property in new[] { "Integer", "Single", "NativeInteger", "Text", "Boxed", "Interface" })
            Assert.Contains($"newValue, _{property}, static", generated);
        foreach (var property in new[] { "Long", "UnsignedLong", "Double", "Decimal", "Nullable", "Enum" })
        {
            Assert.Contains($"newValue, GetPropertyValue(static o => ((Values)o)._{property}), static", generated);
            Assert.Contains($"On{property}Changed(GetPropertyValue(static o => ((Values)o)._{property}));", generated);
        }
    }

    [Theory]
    [InlineData("protected int GetPropertyValue;", true, true)]
    [InlineData("protected T GetPropertyValue<T>(System.Func<Namotion.Interceptor.IInterceptorSubject,T> read) => default!;", true, false)]
    [InlineData("protected T GetPropertyValue<T>(int number) => default!;", false, false)]
    public void WhenABaseMemberSharesTheReaderName_ThenEachOverloadUsesItsOwnHidingModifier(string member, bool snapshotHides, bool interceptedHides)
    {
        // Arrange
        var source = "using Namotion.Interceptor.Attributes; public class Parent { " + member +
            " } [InterceptorSubject] public partial class Child : Parent { public partial decimal Amount { get; set; } }";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source).SingleSource();

        // Assert
        Assert.Equal(snapshotHides, generated.Contains("new protected TProperty GetPropertyValue<TProperty>(Func"));
        Assert.Equal(interceptedHides, generated.Contains("new protected TProperty GetPropertyValue<TProperty>(string"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAWidePropertyIsDeclaredOnADerivedSubject_ThenTheSharedSnapshotHelperPreservesLazyInitialization(bool compiledBase)
    {
        // Arrange
        const string baseSource = """
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Root { }
            """;
        const string derivedSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Derived : Root
            {
                public partial decimal Amount { get; set; }
                public Derived() => Amount = 5;
            }
            """;

        // Act
        var result = compiledBase
            ? GeneratorTestHost.RunWithLibraryReferenceForExecution(baseSource, derivedSource)
            : GeneratorTestHost.RunForExecution(baseSource + derivedSource.Replace("using Namotion.Interceptor;", "")
                .Replace("using Namotion.Interceptor.Attributes;", ""));
        var subject = (IInterceptorSubject)result.CreateInstance("Derived");

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Empty(subject.Data);
        Assert.Equal(5m, subject.Properties["Amount"].GetValue!(subject));
        var derived = Assert.Single(result.Sources, generated => generated.HintName.Contains("Derived.g.cs")).SourceText.ToString();
        Assert.Contains("GetPropertyValue(static o => ((Derived)o)._Amount)", derived);
        Assert.DoesNotContain("InterceptorExecutor.ReadBackingField(this", derived);
    }
}
