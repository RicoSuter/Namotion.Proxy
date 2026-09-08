using Moq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderDictionaryReuseTests : OpcUaSubjectLoaderTestsBase
{
    [Fact]
    public async Task WhenDictionaryHasNonStringKeys_ThenExistingChildrenAreReusedDuringLoad()
    {
        // Arrange: a statically modelled IReadOnlyDictionary<int, ...> is pre-populated before the load.
        // The browse tree exposes the same two entries (Items[1], Items[2]), so the load must rebind the
        // existing child instances instead of recreating them. The reuse lookup keys children by the
        // dictionary's converted key (int), so it must convert the browse-name key ("1"/"2") the same way.
        var rootId = new NodeId(1, 0);
        var itemsId = new NodeId(100, 2);
        var item1Id = new NodeId(101, 2);
        var item2Id = new NodeId(102, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] = [CreateObjectReferenceDescription("Items", new ExpandedNodeId(itemsId))],
            [itemsId] =
            [
                CreateObjectReferenceDescription("Items[1]", new ExpandedNodeId(item1Id)),
                CreateObjectReferenceDescription("Items[2]", new ExpandedNodeId(item2Id))
            ],
            [item1Id] = [],
            [item2Id] = []
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);

        var modelContext = CreateSubjectContext();
        var container = new DictionaryReuseContainer(modelContext);
        var (loader, _, _) = CreateLoaderFor(container);

        var itemOne = new DictionaryReuseItem(modelContext) { Name = "one" };
        var itemTwo = new DictionaryReuseItem(modelContext) { Name = "two" };
        container.Items = new Dictionary<int, DictionaryReuseItem> { [1] = itemOne, [2] = itemTwo };

        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        // Act
        await loader.LoadSubjectAsync(container, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.NotNull(container.Items);
        Assert.Equal(2, container.Items.Count);
        Assert.Same(itemOne, container.Items[1]);
        Assert.Same(itemTwo, container.Items[2]);
    }

    [Fact]
    public async Task WhenDictionaryNodeBrowseFailsPermanently_ThenExistingEntriesArePreserved()
    {
        // Arrange: the dictionary node's browse returns a permanent bad status.
        // BrowseNodesAsync omits failed NodeIds from its result so callers can distinguish
        // "browsed successfully (possibly empty)" from "failed this round"; the loader must
        // keep the existing entries instead of overwriting them with an empty dictionary.
        var rootId = new NodeId(1, 0);
        var itemsId = new NodeId(100, 2);

        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var description in descriptions)
                {
                    if (description.NodeId == itemsId)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadUserAccessDenied, References = [] });
                    }
                    else if (description.NodeId == rootId)
                    {
                        results.Add(new BrowseResult
                        {
                            References = [CreateObjectReferenceDescription("Items", new ExpandedNodeId(itemsId))]
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        var modelContext = CreateSubjectContext();
        var container = new DictionaryReuseContainer(modelContext);
        var (loader, _, _) = CreateLoaderFor(container);

        var itemOne = new DictionaryReuseItem(modelContext) { Name = "one" };
        var itemTwo = new DictionaryReuseItem(modelContext) { Name = "two" };
        container.Items = new Dictionary<int, DictionaryReuseItem> { [1] = itemOne, [2] = itemTwo };

        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        // Act
        await loader.LoadSubjectAsync(container, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: the pre-existing entries survive; a failed browse must not be treated
        // as "node has no children".
        Assert.NotNull(container.Items);
        Assert.Equal(2, container.Items.Count);
        Assert.Same(itemOne, container.Items[1]);
        Assert.Same(itemTwo, container.Items[2]);
    }

    [Fact]
    public async Task WhenDictionaryChildrenExtractToSameKey_ThenDuplicateIsNotLoaded()
    {
        // Arrange: two children with distinct NodeIds extract to the same dictionary key
        // ("Items[a]" and a bracket-less sibling "a"). Only one may win the key; the loser
        // must not be created, staged, or loaded, otherwise it would be committed as an
        // unreachable subject with live claims and monitored items.
        var rootId = new NodeId(1, 0);
        var itemsId = new NodeId(100, 2);
        var winnerId = new NodeId(101, 2);
        var loserId = new NodeId(102, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] = [CreateObjectReferenceDescription("Items", new ExpandedNodeId(itemsId))],
            [itemsId] =
            [
                CreateObjectReferenceDescription("Items[a]", new ExpandedNodeId(winnerId)),
                CreateObjectReferenceDescription("a", new ExpandedNodeId(loserId))
            ],
            [winnerId] = [],
            [loserId] = []
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);

        var modelContext = CreateSubjectContext();
        var container = new StringKeyDictionaryContainer(modelContext);
        var (loader, _, _) = CreateLoaderFor(container);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        // Act
        await loader.LoadSubjectAsync(container, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: one entry under the key "a", and exactly one new subject in the registry
        // (the winner); a leaked loser would appear as a second new registry entry.
        Assert.NotNull(container.Items);
        var entry = Assert.Single(container.Items);
        Assert.Equal("a", entry.Key);
        var newSubjects = registry.KnownSubjects.Keys.Except(preLoadKeys).ToArray();
        var newItem = Assert.Single(newSubjects);
        Assert.IsType<DictionaryReuseItem>(newItem);
    }
}

[InterceptorSubject]
public partial class StringKeyDictionaryContainer
{
    [OpcUaNode("Items")]
    public partial IReadOnlyDictionary<string, DictionaryReuseItem>? Items { get; set; }
}

[InterceptorSubject]
public partial class DictionaryReuseContainer
{
    [OpcUaNode("Items")]
    public partial IReadOnlyDictionary<int, DictionaryReuseItem> Items { get; set; }
}

[InterceptorSubject]
public partial class DictionaryReuseItem
{
    public partial string Name { get; set; }
}
