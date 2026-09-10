using System;
using System.Linq.Expressions;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

// SubscribeToPath is a sensitive marker; this file must join the serialized collection even though
// the decomposer tests do not create subscriptions (the conventions test scans for the marker text).
[Collection(PerPropertySubscriptionCollection.Name)]
public class PathExpressionValidationTests
{
    private static void Decompose<TValue>(Expression<Func<Node, TValue>> path)
        => PathExpressionDecomposer.Decompose(path);

    [Fact]
    public void WhenIdentityPath_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose<Node>(x => x));
    }

    [Fact]
    public void WhenFieldSelector_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose(x => x.PlainField));
    }

    [Fact]
    public void WhenPathEndsInIndexedElement_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose(x => x.Children[3]));
    }

    [Fact]
    public void WhenIndexArgumentReferencesLambdaParameter_ThenThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose(x => x.Children[x.Index].Name));
    }

    [Fact]
    public void WhenNegativeCollectionIndex_ThenThrows()
    {
        // Arrange
        // A negative literal on an array (Node[]) is a compile error (CS0251), so the negative index
        // is supplied through a captured variable to exercise the runtime negative-index rejection.
        var negativeIndex = -1;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Decompose(x => x.Children[negativeIndex].Name));
    }

    [Fact]
    public void WhenValidChainedPath_ThenSegmentsAreOrderedWithLeafLast()
    {
        // Act
        var segments = PathExpressionDecomposer.Decompose<Node, string>(x => x.Child!.Children[2].Name);

        // Assert
        Assert.Equal(3, segments.Length);
        Assert.Equal("Child", segments[0].PropertyName);
        Assert.Equal(PathSegmentKind.Property, segments[0].Kind);
        Assert.Equal("Children", segments[1].PropertyName);
        Assert.Equal(PathSegmentKind.CollectionIndex, segments[1].Kind);
        Assert.Equal(2, segments[1].CollectionIndex);
        Assert.Equal("Name", segments[2].PropertyName);
        Assert.True(segments[2].IsLeaf);
    }

    [Fact]
    public void WhenIndexIsCapturedVariable_ThenEvaluatedOnceAtDecompose()
    {
        // Arrange
        var i = 1;

        // Act
        var segments = PathExpressionDecomposer.Decompose<Node, string>(x => x.Children[i].Name);
        i = 5; // must not change the already-decomposed index

        // Assert
        // Path x.Children[i].Name has no leading property, so the indexed Children segment is the root (index 0).
        Assert.Equal(1, segments[0].CollectionIndex);
        Assert.Equal(PathSegmentKind.CollectionIndex, segments[0].Kind);
    }

    [Fact]
    public void WhenLeafHasSanctionedBoxingConvert_ThenDecomposesToSingleLeaf()
    {
        // Act
        // Func<Node, object> makes the compiler insert a boxing Convert(int, object) on the leaf; it must unwrap.
        var segments = PathExpressionDecomposer.Decompose<Node, object>(x => x.Index);

        // Assert
        Assert.Single(segments);
        Assert.Equal("Index", segments[0].PropertyName);
        Assert.True(segments[0].IsLeaf);
    }

    [Fact]
    public void WhenLeafHasNonBoxingCast_ThenThrows()
    {
        // Act & Assert
        // A numeric narrowing (int -> short) is a user cast, not a sanctioned boxing/widening convert.
        Assert.Throws<ArgumentException>(() => Decompose(x => (short)x.Index));
    }

    [Fact]
    public void WhenMultiArgumentIndexer_ThenThrows()
    {
        // Act & Assert
        // x.Grid[1, 2] on a Node[,] compiles to a synthesized Get(int, int) call, not a single-argument indexer.
        var exception = Assert.Throws<ArgumentException>(() =>
            PathExpressionDecomposer.Decompose<GridHolder, string>(x => x.Grid[1, 2].Name));
        Assert.Contains("multi-dimensional", exception.Message);
    }

    [Fact]
    public void WhenNestedIndexerReceiverIsAnotherIndexer_ThenThrows()
    {
        // Act & Assert
        // The outer [2] is applied to x.Rows[1], whose receiver is another indexer rather than a property.
        Assert.Throws<ArgumentException>(() =>
            PathExpressionDecomposer.Decompose<GridHolder, string>(x => x.Rows[1][2].Name));
    }

    [Fact]
    public void WhenNonSubjectIntermediate_ThenThrows()
    {
        // Act & Assert: Number is an int, cannot be an intermediate.
        Assert.Throws<ArgumentException>(() =>
            PathExpressionDecomposer.Decompose<GridHolder, int>(x => x.Number.GetHashCode()));
    }

    [Fact]
    public void WhenCapturedObjectChain_ThenThrows()
    {
        // Arrange
        var other = new Node();

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            PathExpressionDecomposer.Decompose<Node, string>(x => other.Name));
        Assert.Contains("captured object", exception.Message);
    }

    [Fact]
    public void WhenNestedFieldSelector_ThenThrows()
    {
        // Act & Assert
        // PlainField is reached through the parameter (x.Child), so it must report a field selector,
        // not a captured-object chain. The null-forgiving x.Child! avoids CS8602 (warning-as-error).
        var exception = Assert.Throws<ArgumentException>(() => Decompose(x => x.Child!.PlainField));
        Assert.Contains("field", exception.Message);
    }
}
