using System.Text.Json.Nodes;
using Xunit;
using Namotion.Interceptor.ConnectorTester.Snapshot;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Snapshot;

public class SnapshotComparerTests
{
    // Tests use a fixed timestamp so Value-property write timestamps converge across
    // independently-constructed contexts. SnapshotComparer.Capture preserves Value
    // timestamps by design (they propagate via OPC UA source timestamps in production
    // and must match for convergence). Without the fixed timestamp, two contexts
    // created at different wall-clock times would produce snapshots that differ on
    // every Value timestamp field.
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

    private static TestNode CreateLeaf(IInterceptorSubjectContext context, string stringValue, int intValue)
    {
        return new TestNode(context)
        {
            StringValue = stringValue,
            IntValue = intValue,
            DecimalValue = intValue,
            LongValue = intValue
        };
    }

    [Fact]
    public void WhenSnapshotsAreIdentical_ThenCapturesMatch()
    {
        // Arrange
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "x", 1);

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenRootIdDiffersAcrossParticipants_ThenCapturesMatch()
    {
        // Arrange (each participant has its own context, so root subject IDs differ).
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "x", 1);

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert that per-participant root IDs are normalized to "ROOT".
        Assert.Equal(snapshotA, snapshotB);
        Assert.Contains("\"root\":\"ROOT\"", snapshotA);
        Assert.DoesNotContain("\"root\":null", snapshotA);
    }

    [Fact]
    public void WhenValueDiffers_ThenCapturesDiffer()
    {
        // Arrange
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "y", 1); // StringValue differs

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenGraphHasStructuralProperties_ThenStructuralTimestampsAreStrippedFromCapture()
    {
        // Arrange: Collection, Dictionary, and Object kinds are local creation moments
        // and are stripped during normalization. Verify their absence by inspecting JSON.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var context = CreateContext();
        var root = new TestNode(context)
        {
            StringValue = "x",
            IntValue = 1,
            DecimalValue = 1,
            LongValue = 1,
            ObjectRef = CreateLeaf(context, "ref", 0),
            Collection = [CreateLeaf(context, "child", 1)],
            Items = new Dictionary<string, TestNode> { ["a"] = CreateLeaf(context, "a", 1) }
        };

        // Act
        var snapshot = SnapshotComparer.Capture(root);
        var subjects = JsonNode.Parse(snapshot)!["subjects"]!.AsObject();

        // Assert: Collection, Dictionary, and Object kinds carry no "timestamp" field.
        // Value kinds may carry a timestamp (intentionally preserved for convergence checks).
        var sawStructural = false;
        foreach (var (_, subjectNode) in subjects)
        {
            foreach (var (propertyName, propertyNode) in subjectNode!.AsObject())
            {
                var property = propertyNode!.AsObject();
                var kind = property["kind"]?.GetValue<string>();
                if (kind is "Collection" or "Dictionary" or "Object")
                {
                    sawStructural = true;
                    Assert.False(
                        property.ContainsKey("timestamp"),
                        $"Structural property '{propertyName}' (kind={kind}) must not include a timestamp.");
                }
            }
        }
        Assert.True(sawStructural, "Test graph must include at least one structural property.");
    }

    [Fact]
    public void WhenDictionaryItemOrderDiffers_ThenCapturesMatch()
    {
        // Arrange: same key/value pairs, different insertion order.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Items = new Dictionary<string, TestNode>
            {
                ["alpha"] = CreateLeaf(contextA, "a", 1),
                ["bravo"] = CreateLeaf(contextA, "b", 2)
            }
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Items = new Dictionary<string, TestNode>
            {
                ["bravo"] = CreateLeaf(contextB, "b", 2),
                ["alpha"] = CreateLeaf(contextB, "a", 1)
            }
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenCollectionItemOrderDiffers_ThenCapturesDiffer()
    {
        // Arrange: same items, different order.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Collection =
            [
                CreateLeaf(contextA, "a", 1),
                CreateLeaf(contextA, "b", 2)
            ]
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Collection =
            [
                CreateLeaf(contextB, "b", 2),
                CreateLeaf(contextB, "a", 1)
            ]
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenGraphHasCycle_ThenCaptureIsStable()
    {
        // Arrange: a -> b -> a cycle via ObjectRef.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var context = CreateContext();
        var leafB = CreateLeaf(context, "b", 2);
        var rootA = new TestNode(context)
        {
            StringValue = "a", IntValue = 1, DecimalValue = 1, LongValue = 1,
            ObjectRef = leafB
        };
        leafB.ObjectRef = rootA;

        // Act: capture twice; deterministic output should match.
        var snapshot1 = SnapshotComparer.Capture(rootA);
        var snapshot2 = SnapshotComparer.Capture(rootA);

        // Assert
        Assert.Equal(snapshot1, snapshot2);
    }

    [Fact]
    public void WhenNestedValueDiffers_ThenCapturesDiffer()
    {
        // Arrange: divergence one level deep (under ObjectRef). Root-level Value divergence
        // is already covered; this exercises nested subjects after ID remap.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            ObjectRef = CreateLeaf(contextA, "nested-a", 1)
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            ObjectRef = CreateLeaf(contextB, "nested-b", 1) // StringValue differs on nested node
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenDeepGraphHasIdenticalStateAndDifferentRawIds_ThenCapturesMatch()
    {
        // Arrange: multi-level graph (root -> ObjectRef -> {Collection, Items}) so the BFS
        // ID remap must assign SUBJ_N stably across many subjects. Each participant has its
        // own context so raw subject IDs differ throughout.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        static TestNode BuildGraph(IInterceptorSubjectContext context) =>
            new(context)
            {
                StringValue = "root", IntValue = 1, DecimalValue = 1, LongValue = 1,
                ObjectRef = new TestNode(context)
                {
                    StringValue = "nested", IntValue = 2, DecimalValue = 2, LongValue = 2,
                    Collection =
                    [
                        CreateLeaf(context, "c0", 10),
                        CreateLeaf(context, "c1", 11)
                    ],
                    Items = new Dictionary<string, TestNode>
                    {
                        ["alpha"] = CreateLeaf(context, "a", 100),
                        ["bravo"] = CreateLeaf(context, "b", 101)
                    }
                }
            };

        // Act
        var snapshotA = SnapshotComparer.Capture(BuildGraph(CreateContext()));
        var snapshotB = SnapshotComparer.Capture(BuildGraph(CreateContext()));

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenDictionaryKeysDiffer_ThenCapturesDiffer()
    {
        // Arrange: same item count, same item values, different keys. Must not match.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Items = new Dictionary<string, TestNode> { ["alpha"] = CreateLeaf(contextA, "v", 1) }
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Items = new Dictionary<string, TestNode> { ["bravo"] = CreateLeaf(contextB, "v", 1) }
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenCollectionLengthDiffers_ThenCapturesDiffer()
    {
        // Arrange: one side has more items. Must not match.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Collection = [CreateLeaf(contextA, "a", 1), CreateLeaf(contextA, "b", 2)]
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Collection = [CreateLeaf(contextB, "a", 1)]
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenSubjectHasMixedKindsAndOnlyValueDiverges_ThenCapturesDiffer()
    {
        // Arrange: subject carries Value + Collection + Dictionary + Object properties.
        // Only the Value (StringValue) differs. Structural-timestamp stripping must not
        // accidentally mask Value-property divergence in the same subject.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            ObjectRef = CreateLeaf(contextA, "ref", 0),
            Collection = [CreateLeaf(contextA, "c", 1)],
            Items = new Dictionary<string, TestNode> { ["k"] = CreateLeaf(contextA, "i", 1) }
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "y", IntValue = 1, DecimalValue = 1, LongValue = 1, // StringValue differs
            ObjectRef = CreateLeaf(contextB, "ref", 0),
            Collection = [CreateLeaf(contextB, "c", 1)],
            Items = new Dictionary<string, TestNode> { ["k"] = CreateLeaf(contextB, "i", 1) }
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }

    // Raw-JSON tests for SnapshotComparer.SnapshotsMatch — no Capture or SubjectChangeContext needed.

    private const string SampleSnapshotPropertiesAB = """
        {"root":"ROOT","subjects":{"ROOT":{"A":{"kind":"Value","value":1},"B":{"kind":"Value","value":2}}}}
        """;

    private const string SampleSnapshotPropertiesBA = """
        {"root":"ROOT","subjects":{"ROOT":{"B":{"kind":"Value","value":2},"A":{"kind":"Value","value":1}}}}
        """;

    [Fact]
    public void WhenPropertyOrderInJsonDiffers_ThenSnapshotsMatch()
    {
        // Arrange / Act
        var match = SnapshotComparer.SnapshotsMatch(SampleSnapshotPropertiesAB, SampleSnapshotPropertiesBA);

        // Assert: the JSON walk treats objects as unordered, so reordered keys still match.
        Assert.True(match);
    }

    [Fact]
    public void WhenSubjectOrderInJsonDiffers_ThenSnapshotsMatch()
    {
        // Arrange: same content, subjects in different JSON order.
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Object","id":"OTHER"}},"OTHER":{"Q":{"kind":"Value","value":1}}}}""";
        const string b = """{"root":"ROOT","subjects":{"OTHER":{"Q":{"kind":"Value","value":1}},"ROOT":{"P":{"kind":"Object","id":"OTHER"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert: JSON walk treats the subjects map as unordered.
        Assert.True(match);
    }

    [Fact]
    public void WhenSubjectKeysDiffer_ThenSnapshotsDoNotMatch()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{},"OTHER":{}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }

    [Fact]
    public void WhenPropertyKeysDiffer_ThenSnapshotsDoNotMatch()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"A":{"kind":"Value","value":1}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"A":{"kind":"Value","value":1},"B":{"kind":"Value","value":2}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }

    [Fact]
    public void WhenRootFieldDiffers_ThenSnapshotsDoNotMatch()
    {
        // Arrange: Capture always normalizes root to "ROOT", but if that invariant
        // broke the comparison should still catch the divergence via subject keys.
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";
        const string b = """{"root":"OTHER","subjects":{"OTHER":{"P":{"kind":"Value","value":1}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }

    [Fact]
    public void WhenBothValueTimestampsAreNonNullAndEqual_ThenSnapshotsMatch()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.True(match);
    }

    [Fact]
    public void WhenBothValueTimestampsAreNonNullAndDiffer_ThenSnapshotsDoNotMatch()
    {
        // Arrange: same value, different non-null timestamps. Real divergence.
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-02T00:00:00+00:00"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }

    [Fact]
    public void WhenOneValueTimestampIsNull_ThenSnapshotsMatch()
    {
        // Arrange: same value, one side has explicit null (NullTimestampSentinel contract).
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":null}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert: null-timestamp rule applies.
        Assert.True(match);
    }

    [Fact]
    public void WhenOneValueTimestampIsOmitted_ThenSnapshotsMatch()
    {
        // Arrange: production JSON omits the "timestamp" key entirely when null
        // ([JsonIgnore(WhenWritingNull)] on SubjectPropertyUpdate.Timestamp).
        // This is the actual wire shape; the JSON-walk null-timestamp rule must treat
        // a missing key the same as an explicit null node.
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.True(match);
    }

    [Fact]
    public void WhenBothValueTimestampsAreNull_ThenSnapshotsMatch()
    {
        // Arrange: both sides preserve the explicit-null state.
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":null}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":null}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.True(match);
    }

    [Fact]
    public void WhenValueDiffersAndTimestampsAreNull_ThenSnapshotsDoNotMatch()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":null}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":2,"timestamp":null}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }

}
