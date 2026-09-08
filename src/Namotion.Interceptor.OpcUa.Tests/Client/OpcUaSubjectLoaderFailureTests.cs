using System.Reactive.Concurrency;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderFailureTests
{
    private static readonly NodeId RootId = new(1, 0);
    private static readonly NodeId SensorId = new(2001, 2);
    private static readonly NodeId StatusId = new(2002, 2);
    private static readonly NodeId TemperatureId = new(1001, 2);

    /// <summary>
    /// Upper bound for a failed load to unwind. The work itself is a handful of mocked calls with
    /// no real I/O, so anything approaching this bound means the rollback is blocked rather than
    /// slow. Generous enough not to fire on a loaded CI agent.
    /// </summary>
    private static readonly TimeSpan RollbackCompletionTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task WhenLoadFailsDuringDiscovery_ThenRootRemainsAtPreLoadState()
    {
        // Arrange: Sensor is a dynamic object on root with a child Status that fails on browse
        // during recursive type resolution. By this point root has Temperature claimed and
        // Sensor added as a property, so a partial-state bug will be observable.
        var (loader, source, subject) = CreateFixture();

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(
            mockSession,
            failOnNodeId: StatusId,
            browseTree: new Dictionary<NodeId, ReferenceDescription[]>
            {
                [RootId] =
                [
                    MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                    MakeReference("Sensor", SensorId, NodeClass.Object)
                ],
                [SensorId] =
                [
                    MakeReference("Status", StatusId, NodeClass.Object)
                ]
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId>
        {
            [TemperatureId] = DataTypeIds.Double
        });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: dynamic property slots may exist on root, but they have no values and no
        // source claims, because both are deferred to a Commit that was never reached. On
        // retry the same slots get filled cleanly. The orphan and retry tests cover registry
        // cleanliness.
        Assert.Empty(source.Ownership.Properties);
        foreach (var property in registeredSubject.Properties)
        {
            Assert.Null(property.GetValue());
        }
    }

    [Fact]
    public async Task WhenLoadFails_ThenRegistryKnownSubjectsContainsNoOrphans()
    {
        // Arrange
        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(
            mockSession,
            failOnNodeId: StatusId,
            browseTree: new Dictionary<NodeId, ReferenceDescription[]>
            {
                [RootId] =
                [
                    MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                    MakeReference("Sensor", SensorId, NodeClass.Object)
                ],
                [SensorId] =
                [
                    MakeReference("Status", StatusId, NodeClass.Object)
                ]
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId>
        {
            [TemperatureId] = DataTypeIds.Double
        });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: no staged subjects leaked into the registry
        var postFailureKeys = registry.KnownSubjects.Keys.ToHashSet();
        var orphans = postFailureKeys.Except(preLoadKeys).ToArray();
        Assert.Empty(orphans);

        // Assert: no source-ownership claims committed (Commit was never reached; rollback
        // discarded the pending claims).
        Assert.Empty(source.Ownership.Properties);
    }

    [Fact]
    public void WhenCommitFailsMidway_ThenOnlyItsOwnClaimsAndBindingsAreRolledBack()
    {
        // Arrange: simulate a reload. "PreOwned" is already owned by this source from a previous
        // successful load; "NewlyClaimed" is claimed for the first time by this Commit; "Bound" is
        // written before "Throwing" aborts the Commit. The rollback must release only the claim
        // this Commit established, because releasing pre-existing ownership would leave
        // application writes unrouted until the next successful retry, and restore only what it
        // wrote.
        var (_, source, subject) = CreateFixture();
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        var preOwned = registeredSubject.AddProperty("PreOwned", typeof(int), _ => 0, (_, _) => { });
        var newlyClaimed = registeredSubject.AddProperty("NewlyClaimed", typeof(int), _ => 0, (_, _) => { });
        object? boundValue = "before";
        var bound = registeredSubject.AddProperty("Bound", typeof(string), _ => boundValue, (_, value) => boundValue = value);
        var throwing = registeredSubject.AddProperty("Throwing", typeof(int), _ => 0,
            (_, _) => throw new InvalidOperationException("Setter failure aborts Commit."));

        Assert.True(source.Ownership.ClaimSource(preOwned.Reference));

        var mockSession = CreateMockSession();
        using var context = new OpcUaLoadContext(
            mockSession.Object,
            source.Ownership,
            source,
            maxReferencesPerNode: 1000,
            maxBrowseContinuations: 100,
            NullLogger<OpcUaSubjectClientSource>.Instance,
            CancellationToken.None);

        context.QueueClaim(preOwned.Reference, new NodeId(9001, 2), new MonitoredItem(NullTelemetryContext.Instance));
        context.QueueClaim(newlyClaimed.Reference, new NodeId(9002, 2), new MonitoredItem(NullTelemetryContext.Instance));
        context.QueueBinding(bound, "after");
        context.QueueBinding(throwing, 42);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.Commit());

        Assert.True(preOwned.Reference.TryGetSource(out var owner));
        Assert.Same(source, owner);
        Assert.False(newlyClaimed.Reference.TryGetSource(out _));
        Assert.Empty(context.MonitoredItems);
        Assert.Equal("before", bound.GetValue());
    }

    [Fact]
    public async Task WhenLoadFailsAndRetries_ThenSecondAttemptSucceedsCleanly()
    {
        // Arrange: first browse of Status fails transient, second browse succeeds
        var (loader, source, subject) = CreateFixture();

        var mockSession = CreateMockSession();
        var statusBrowseCount = 0;
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
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == StatusId)
                    {
                        if (++statusBrowseCount == 1)
                        {
                            results.Add(new BrowseResult
                            {
                                StatusCode = StatusCodes.BadServerHalted,
                                References = []
                            });
                            continue;
                        }
                        // Second attempt: status has no children, type resolves cleanly
                        results.Add(new BrowseResult { References = [] });
                    }
                    else if (desc.NodeId == RootId)
                    {
                        var collection = new ReferenceDescriptionCollection();
                        collection.AddRange(
                        [
                            MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                            MakeReference("Sensor", SensorId, NodeClass.Object)
                        ]);
                        results.Add(new BrowseResult { References = collection });
                    }
                    else if (desc.NodeId == SensorId)
                    {
                        var collection = new ReferenceDescriptionCollection();
                        collection.AddRange([MakeReference("Status", StatusId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = collection });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId>
        {
            [TemperatureId] = DataTypeIds.Double
        });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        var monitoredItems = await loader.LoadSubjectAsync(
            subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: full subject graph loaded on retry. The second attempt is independent
        // of the first because rollback discarded all staged state. After a clean retry,
        // the registry should reflect only the final graph (root + Sensor + Status),
        // with no orphan staged subjects from the failed attempt.
        Assert.Single(monitoredItems);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Contains(registeredSubject.Properties, p => p.Name == "Temperature");
        Assert.Contains(registeredSubject.Properties, p => p.Name == "Sensor");
        Assert.Single(source.Ownership.Properties);

        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        // Expected: root, Sensor, Status. Anything more is an orphan from the failed attempt.
        Assert.Equal(3, registry.KnownSubjects.Count);
    }

    [Fact]
    public async Task WhenLoadSucceeds_ThenRootSubjectAssignmentsHappenAfterAllBrowsesComplete()
    {
        // Arrange: subscribe to root's property change observable and capture the browse
        // count at the moment root.Sensor's assignment fires. If apply runs strictly after
        // discovery, the captured count equals the final count. Interleaved mutations
        // would capture a lower count.
        var (loader, _, subject) = CreateFixture();

        var browseCount = 0;
        var browseCountAtSensorAssignment = -1;

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
                Interlocked.Increment(ref browseCount);
                var results = new BrowseResultCollection();
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == RootId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([MakeReference("Sensor", SensorId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        using var subscription = subject.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, subject) && change.Property.Name == "Sensor")
                {
                    browseCountAtSensorAssignment = browseCount;
                }
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: subscription fired (browseCountAtSensorAssignment >= 0) and captured
        // the FINAL browse count. Equality proves no further browses happened between
        // the assignment and method return, which is what Commit-after-discovery guarantees.
        Assert.True(browseCount > 0);
        Assert.Equal(browseCount, browseCountAtSensorAssignment);
    }

    [Fact]
    public async Task WhenLoadSucceeds_ThenSourceClaimsHappenBeforeRootAssignmentInCommit()
    {
        // Arrange: include a variable property on root so a claim is queued, plus a
        // sub-subject so a root assignment is queued. Subscribe to root's property change
        // observable and capture ownership count synchronously at the moment Sensor
        // is assigned. If Commit ordering is correct (claims before bindings), the captured
        // count equals the final claim count. A regression that applies bindings first would
        // capture 0 here.
        var (loader, source, subject) = CreateFixture();

        var ownedCountAtSensorAssignment = -1;

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
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == RootId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([
                            MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                            MakeReference("Sensor", SensorId, NodeClass.Object)
                        ]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId> { [TemperatureId] = DataTypeIds.Double });

        using var subscription = subject.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, subject) && change.Property.Name == "Sensor")
                {
                    ownedCountAtSensorAssignment = source.Ownership.Properties.Count;
                }
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: observer fired AND saw Temperature already claimed at the moment Sensor
        // appeared. If Commit reversed its loops (bindings before claims), the observer
        // would have captured 0 here.
        Assert.Single(monitoredItems);
        Assert.Single(source.Ownership.Properties);
        Assert.Equal(1, ownedCountAtSensorAssignment);
    }

    [Fact]
    public async Task WhenLoadSucceeds_ThenRootBindingsAreAppliedAfterTheirSubtrees()
    {
        // Arrange: Root.Items is a collection whose two items each carry a subject reference, so
        // the load queues the root binding while discovering level 0 and the item bindings while
        // discovering level 1. Subscribe to the property change observable and count, at the
        // moment Root.Items is assigned, the items whose Child is already bound. Deepest first
        // means both are; applying in queue order would expose the items through the root while
        // their children are still null.
        var itemsId = new NodeId(4401, 2);
        var firstItemId = new NodeId(4402, 2);
        var secondItemId = new NodeId(4403, 2);
        var firstChildId = new NodeId(4404, 2);
        var secondChildId = new NodeId(4405, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [RootId] = [MakeReference("Items", itemsId, NodeClass.Object)],
            [itemsId] =
            [
                MakeReference("Items[0]", firstItemId, NodeClass.Object),
                MakeReference("Items[1]", secondItemId, NodeClass.Object)
            ],
            [firstItemId] = [MakeReference("Child", firstChildId, NodeClass.Object)],
            [secondItemId] = [MakeReference("Child", secondChildId, NodeClass.Object)]
        };

        var modelContext = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle()
            .WithPropertyChangeSubscriptions();
        var root = new BindingOrderRoot(modelContext);
        var (loader, _) = CreateSourceAndLoaderFor(root, shouldAddDynamicProperties: false);

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(mockSession, failOnNodeId: NodeId.Null, browseTree);

        var boundChildrenAtItemsAssignment = -1;
        using var subscription = modelContext
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, root) && change.Property.Name == nameof(BindingOrderRoot.Items))
                {
                    boundChildrenAtItemsAssignment = change
                        .GetNewValue<RollbackReferenceParent[]>()
                        .Count(item => item.Child is not null);
                }
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await loader.LoadSubjectAsync(root, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: the observer fired and saw every item's child already bound.
        var items = Assert.IsType<RollbackReferenceParent[]>(root.Items);
        Assert.Equal(2, items.Length);
        Assert.Equal(2, boundChildrenAtItemsAssignment);
    }

    [Fact]
    public async Task WhenLoadFailsAtNestedStagedLevel_ThenAllStagedSubjectsAreUnregistered()
    {
        // Arrange: 3-level tree Root → ParentA (staged) → ChildB (staged) → fail.
        // Both ParentA and ChildB are created during discovery as staged subjects.
        // If rollback only unregisters one level, the other becomes an orphan.
        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var parentAId = new NodeId(3001, 2);
        var childBId = new NodeId(3002, 2);
        var leafFailId = new NodeId(3003, 2);

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
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == leafFailId)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadServerHalted, References = [] });
                    }
                    else if (desc.NodeId == RootId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([MakeReference("ParentA", parentAId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else if (desc.NodeId == parentAId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([MakeReference("ChildB", childBId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else if (desc.NodeId == childBId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([MakeReference("LeafFail", leafFailId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: registry only contains pre-load subjects. Both ParentA and ChildB
        // were created during discovery; both must be unregistered on rollback.
        var postFailureKeys = registry.KnownSubjects.Keys.ToHashSet();
        var orphans = postFailureKeys.Except(preLoadKeys).ToArray();
        Assert.Empty(orphans);

        // Assert: no source-ownership claims committed across the multi-level rollback.
        Assert.Empty(source.Ownership.Properties);
    }

    [Fact]
    public async Task WhenChildBrowseReturnsPermanentBadStatus_ThenChildIsSkippedAndLoadCompletes()
    {
        // Arrange: a sibling Object child returns a permanent classifier code on its
        // own browse. The loader must log + continue (no exception), drop the bad child,
        // and complete the load for the well-formed siblings. Distinguishes the loader's
        // permanent-vs-transient path: transient surfaces as OpcUaTransientServiceException
        // (covered by sibling tests); permanent is silently skipped.
        var (loader, source, subject) = CreateFixture();

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(
            mockSession,
            failOnNodeId: SensorId,
            failStatusCode: StatusCodes.BadNodeIdUnknown,
            browseTree: new Dictionary<NodeId, ReferenceDescription[]>
            {
                [RootId] =
                [
                    MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                    MakeReference("Sensor", SensorId, NodeClass.Object)
                ]
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId>
        {
            [TemperatureId] = DataTypeIds.Double
        });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act: must not throw; permanent bad status on Sensor browse is logged + skipped.
        var monitoredItems = await loader.LoadSubjectAsync(
            subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: Temperature is loaded and owned, Sensor is silently dropped.
        Assert.Single(monitoredItems);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Contains(registeredSubject.Properties, p => p.Name == "Temperature");
        Assert.DoesNotContain(registeredSubject.Properties, p => p.Name == "Sensor");
        Assert.Single(source.Ownership.Properties);
    }

    [Fact]
    public async Task WhenACollectionChildLoadFailsUnderANonRootParent_ThenALaterLoadStillRegistersTheChild()
    {
        // Arrange: Root.Parent is assigned before the load, so the parent is reused rather than
        // staged and therefore survives a failed load. Parent.Items is a collection whose two
        // elements are created and staged during discovery, and the second element's browse fails
        // transiently on the first attempt. A container bound to Parent.Items before Commit would
        // still reference the staged elements after the rollback detached them, leaving them
        // referenced by the model but absent from the registry.
        var parentId = new NodeId(4001, 2);
        var itemsId = new NodeId(4002, 2);
        var firstItemId = new NodeId(4003, 2);
        var secondItemId = new NodeId(4004, 2);
        var firstValueId = new NodeId(4005, 2);
        var secondValueId = new NodeId(4006, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [RootId] = [MakeReference("Parent", parentId, NodeClass.Object)],
            [parentId] = [MakeReference("Items", itemsId, NodeClass.Object)],
            [itemsId] =
            [
                MakeReference("Items[0]", firstItemId, NodeClass.Object),
                MakeReference("Items[1]", secondItemId, NodeClass.Object)
            ],
            [firstItemId] = [MakeReference("Value", firstValueId, NodeClass.Variable)],
            [secondItemId] = [MakeReference("Value", secondValueId, NodeClass.Variable)]
        };

        var modelContext = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var root = new RollbackCollectionRoot(modelContext);
        root.Parent = new RollbackCollectionParent(modelContext);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var (loader, source) = CreateSourceAndLoaderFor(root, shouldAddDynamicProperties: false);

        var failSecondItemBrowse = true;
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
                    if (failSecondItemBrowse && description.NodeId == secondItemId)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadServerHalted, References = [] });
                        continue;
                    }

                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(description.NodeId, out var references))
                    {
                        children.AddRange(references);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act: the first load rolls back.
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(root, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: nothing of the failed load is visible.
        Assert.Null(root.Parent!.Items);
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());

        // Act: the second load runs against a healthy server.
        failSecondItemBrowse = false;
        var monitoredItems = await loader.LoadSubjectAsync(
            root, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: both collection elements are registered and monitored. A binding that survived
        // the rollback would be reused from property.Children without re-staging, never re-attached,
        // and show up here as a null registration and a missing monitored item.
        var items = Assert.IsType<RollbackCollectionItem[]>(root.Parent!.Items);
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.NotNull(item.TryGetRegisteredSubject()));

        var monitoredNodeIds = monitoredItems.Select(item => item.StartNodeId).ToHashSet();
        Assert.Equal(2, monitoredItems.Count);
        Assert.Contains(firstValueId, monitoredNodeIds);
        Assert.Contains(secondValueId, monitoredNodeIds);
        Assert.Equal(2, source.Ownership.Properties.Count);
    }

    [Fact]
    public async Task WhenADictionaryEntryLoadFailsUnderANonRootParent_ThenALaterLoadStillRegistersTheEntry()
    {
        // Arrange: identical in shape to the collection case above, but through the dictionary
        // path of LoadCollectionsAndDictionariesAsync, which keys and builds its container
        // differently and so needs its own regression pin. Bracketed browse names carry the
        // dictionary keys.
        var parentId = new NodeId(4101, 2);
        var entriesId = new NodeId(4102, 2);
        var firstEntryId = new NodeId(4103, 2);
        var secondEntryId = new NodeId(4104, 2);
        var firstValueId = new NodeId(4105, 2);
        var secondValueId = new NodeId(4106, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [RootId] = [MakeReference("Parent", parentId, NodeClass.Object)],
            [parentId] = [MakeReference("Entries", entriesId, NodeClass.Object)],
            [entriesId] =
            [
                MakeReference("Entries[KeyA]", firstEntryId, NodeClass.Object),
                MakeReference("Entries[KeyB]", secondEntryId, NodeClass.Object)
            ],
            [firstEntryId] = [MakeReference("Value", firstValueId, NodeClass.Variable)],
            [secondEntryId] = [MakeReference("Value", secondValueId, NodeClass.Variable)]
        };

        var modelContext = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var root = new RollbackDictionaryRoot(modelContext);
        root.Parent = new RollbackDictionaryParent(modelContext);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var (loader, source) = CreateSourceAndLoaderFor(root, shouldAddDynamicProperties: false);

        var failSecondEntryBrowse = true;
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
                    if (failSecondEntryBrowse && description.NodeId == secondEntryId)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadServerHalted, References = [] });
                        continue;
                    }

                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(description.NodeId, out var references))
                    {
                        children.AddRange(references);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act: the first load rolls back.
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(root, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: nothing of the failed load is visible.
        Assert.Null(root.Parent!.Entries);
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());

        // Act: the second load runs against a healthy server.
        failSecondEntryBrowse = false;
        var monitoredItems = await loader.LoadSubjectAsync(
            root, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: both entries are registered and monitored. A binding that survived the rollback
        // would be reused without re-staging and never re-attached.
        var entries = Assert.IsAssignableFrom<IReadOnlyDictionary<string, RollbackCollectionItem>>(root.Parent!.Entries);
        Assert.Equal(2, entries.Count);
        Assert.All(entries.Values, entry => Assert.NotNull(entry.TryGetRegisteredSubject()));

        var monitoredNodeIds = monitoredItems.Select(item => item.StartNodeId).ToHashSet();
        Assert.Equal(2, monitoredItems.Count);
        Assert.Contains(firstValueId, monitoredNodeIds);
        Assert.Contains(secondValueId, monitoredNodeIds);
        Assert.Equal(2, source.Ownership.Properties.Count);
    }

    [Fact]
    public async Task WhenASubjectReferenceLoadFailsUnderANonRootParent_ThenALaterLoadStillRegistersTheChild()
    {
        // Arrange: identical in shape to the collection and dictionary cases above, but through the
        // single subject reference branch, which LoadPendingSubjectReferencesAsync queues on its own
        // and so needs its own regression pin. Root.Parent is assigned before the load, so the
        // parent is reused rather than staged and survives a failed load. Parent.Child is staged
        // during discovery and its browse fails transiently on the first attempt. A reference bound
        // to Parent.Child before Commit would still point at the staged child after the rollback
        // detached it; the next load would then reuse that child from property.Children without
        // re-staging it, never re-attach it, and leave its subtree unregistered and unmonitored.
        var parentId = new NodeId(4201, 2);
        var childId = new NodeId(4202, 2);
        var valueId = new NodeId(4203, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [RootId] = [MakeReference("Parent", parentId, NodeClass.Object)],
            [parentId] = [MakeReference("Child", childId, NodeClass.Object)],
            [childId] = [MakeReference("Value", valueId, NodeClass.Variable)]
        };

        var modelContext = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var root = new RollbackReferenceRoot(modelContext);
        root.Parent = new RollbackReferenceParent(modelContext);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var (loader, source) = CreateSourceAndLoaderFor(root, shouldAddDynamicProperties: false);

        var failChildBrowse = true;
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
                    if (failChildBrowse && description.NodeId == childId)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadServerHalted, References = [] });
                        continue;
                    }

                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(description.NodeId, out var references))
                    {
                        children.AddRange(references);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act: the first load rolls back.
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(root, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: nothing of the failed load is visible.
        Assert.Null(root.Parent!.Child);
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());

        // Act: the second load runs against a healthy server.
        failChildBrowse = false;
        var monitoredItems = await loader.LoadSubjectAsync(
            root, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: the child is registered and monitored. A binding that survived the rollback would
        // show up here as a null registration and a missing monitored item.
        var child = root.Parent!.Child;
        Assert.NotNull(child);
        Assert.NotNull(child.TryGetRegisteredSubject());

        var monitoredItem = Assert.Single(monitoredItems);
        Assert.Equal(valueId, monitoredItem.StartNodeId);
        Assert.Single(source.Ownership.Properties);
    }

    [Fact]
    public async Task WhenALaterPhaseFailsAfterAContainerIsResolved_ThenNothingIsBoundUntilALaterLoadSucceeds()
    {
        // Arrange: the collection, dictionary and subject reference rollback tests above all fail
        // while the level below the container is still being loaded. Here the collection loads
        // cleanly and its binding is queued, and only then does a later phase throw. Parent.Status
        // is a plain value property, so LoadChildPropertiesAsync queues it for LoadAttributesAsync,
        // which is the last phase of the level and therefore runs after the container has been
        // resolved. Failing its browse transiently on the first load rolls back a load whose
        // container binding is already queued for Commit.
        var parentId = new NodeId(4301, 2);
        var itemsId = new NodeId(4302, 2);
        var firstItemId = new NodeId(4303, 2);
        var secondItemId = new NodeId(4304, 2);
        var firstValueId = new NodeId(4305, 2);
        var secondValueId = new NodeId(4306, 2);
        var statusId = new NodeId(4307, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [RootId] = [MakeReference("Parent", parentId, NodeClass.Object)],
            [parentId] =
            [
                MakeReference("Items", itemsId, NodeClass.Object),
                MakeReference("Status", statusId, NodeClass.Variable)
            ],
            [itemsId] =
            [
                MakeReference("Items[0]", firstItemId, NodeClass.Object),
                MakeReference("Items[1]", secondItemId, NodeClass.Object)
            ],
            [firstItemId] = [MakeReference("Value", firstValueId, NodeClass.Variable)],
            [secondItemId] = [MakeReference("Value", secondValueId, NodeClass.Variable)]
        };

        var modelContext = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var root = new RollbackLatePhaseRoot(modelContext);
        root.Parent = new RollbackLatePhaseParent(modelContext);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var (loader, source) = CreateSourceAndLoaderFor(root, shouldAddDynamicProperties: false);

        var failStatusAttributeBrowse = true;
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
                    if (failStatusAttributeBrowse && description.NodeId == statusId)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadServerHalted, References = [] });
                        continue;
                    }

                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(description.NodeId, out var references))
                    {
                        children.AddRange(references);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act: the first load rolls back.
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(root, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: nothing of the failed load is visible.
        Assert.Null(root.Parent!.Items);
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());

        // Act: the second load runs against a healthy server.
        failStatusAttributeBrowse = false;
        var monitoredItems = await loader.LoadSubjectAsync(
            root, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: both collection elements are registered and monitored. A container binding that
        // survived the rollback would be reused from property.Children without re-staging, never
        // re-attached, and show up here as a null registration and two missing monitored items.
        var items = Assert.IsType<RollbackCollectionItem[]>(root.Parent!.Items);
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.NotNull(item.TryGetRegisteredSubject()));

        var monitoredNodeIds = monitoredItems.Select(item => item.StartNodeId).ToHashSet();
        Assert.Equal(3, monitoredItems.Count);
        Assert.Contains(firstValueId, monitoredNodeIds);
        Assert.Contains(secondValueId, monitoredNodeIds);
        Assert.Contains(statusId, monitoredNodeIds);
        Assert.Equal(3, source.Ownership.Properties.Count);
    }

    [Fact]
    public async Task WhenAGraphSharedSubjectIsRolledBack_ThenItsStagingContextIsReleased()
    {
        // Arrange: a graph-shaped address space rather than a tree. Root has two Object children,
        // Holder and Shared, browsed in that order, so both are staged under the root subject's
        // context in that order. Holder's only child reference resolves to Shared's NodeId, which
        // hits the per-load SubjectsByNodeId cache and queues a binding of the already-staged
        // Shared under Holder. Shared's value property then fails its attribute-phase browse, which
        // is the last phase of the level, so the load rolls back with that binding queued but a
        // staging link that is keyed to the root context, not to Holder's.
        var holderId = new NodeId(4401, 2);
        var sharedId = new NodeId(4402, 2);
        var sharedValueId = new NodeId(4403, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            // Holder first: staging order is browse order, so Shared is rolled back first.
            [RootId] =
            [
                MakeReference("Holder", holderId, NodeClass.Object),
                MakeReference("Shared", sharedId, NodeClass.Object)
            ],
            [holderId] = [MakeReference("Child", sharedId, NodeClass.Object)],
            [sharedId] = [MakeReference("Value", sharedValueId, NodeClass.Variable)]
        };

        var modelContext = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var root = new RollbackGraphRoot(modelContext);

        // The staging link points at the root subject's own context, not at the shared model
        // context, because RegisterStagedSubject keys the fallback to the parent subject.
        var rootContext = ((IInterceptorSubject)root).Context;

        // Neither staged subject is ever bound to root, so the model offers no handle on them after
        // the rollback. The recording factory is how the assertions reach the shared subject.
        var subjectFactory = new RecordingOpcUaSubjectFactory();
        var (loader, _) = CreateSourceAndLoaderFor(root, shouldAddDynamicProperties: false, subjectFactory);

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
                    if (description.NodeId == sharedValueId)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadServerHalted, References = [] });
                        continue;
                    }

                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(description.NodeId, out var references))
                    {
                        children.AddRange(references);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(root, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: the cascade ran, so the shared subject is out of the graph and out of the
        // registry. Both are preconditions for the leak rather than the leak itself; without them
        // the fallback check below would pass for the wrong reason.
        var sharedSubject = subjectFactory.GetCreatedSubject("Shared");
        Assert.Equal(0, sharedSubject.GetReferenceCount());
        Assert.Null(sharedSubject.TryGetRegisteredSubject());

        // RemoveFallbackContext reports whether there was a link to remove, so a `true` return here
        // is the leak: the staging link outlived the subject's presence in the graph. A surviving
        // link keeps the shared subject's context in the root context's used-by set, which holds a
        // strong reference to the orphan's executor, and therefore to the orphan, for the process
        // lifetime. Every failed load of such a graph adds one more across a reconnect or retry loop.
        Assert.False(sharedSubject.Context.RemoveFallbackContext(rootContext),
            "The rolled-back shared subject still had the root subject's context as a fallback, so its staging link " +
            "outlived its presence in the graph. The root context keeps a strong reference to every context that uses " +
            "it, so the orphan is retained for the process lifetime and every failed load of this address space leaks " +
            "one more.");
    }

    [Fact]
    public async Task WhenLoadFailsAfterACycleIsDiscovered_ThenNoStagedSubjectSurvives()
    {
        // Arrange: Root -> ChildA -> ChildB -> BackToRoot, where BackToRoot resolves to ChildA's
        // NodeId, so the two subjects created for this load reference each other. Root's Status
        // variable is browsed by the attribute phase, which runs after the subject-reference
        // recursion has discovered the cycle, and that browse fails transiently. Were the two
        // subjects bound to each other before the failure, each would hold the other at reference
        // count one and a rollback that only sheds unreferenced subjects would reach neither.
        var childAId = new NodeId(4501, 2);
        var childBId = new NodeId(4502, 2);
        var statusId = new NodeId(4503, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [RootId] =
            [
                MakeReference("ChildA", childAId, NodeClass.Object),
                MakeReference("Status", statusId, NodeClass.Variable)
            ],
            [childAId] = [MakeReference("ChildB", childBId, NodeClass.Object)],
            [childBId] = [MakeReference("BackToRoot", childAId, NodeClass.Object)]
        };

        var subjectContext = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        IInterceptorSubject root = new DynamicSubject(subjectContext);
        var registry = subjectContext.TryGetService<ISubjectRegistry>()!;

        var subjectFactory = new RecordingOpcUaSubjectFactory();
        var (loader, _) = CreateSourceAndLoaderFor(root, shouldAddDynamicProperties: true, subjectFactory);

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(mockSession, failOnNodeId: statusId, browseTree);
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId> { [statusId] = DataTypeIds.Double });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(root, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: the registry is back at its pre-load state and no staging link outlived the
        // rollback. RemoveFallbackContext reports whether there was a link to remove, so `true`
        // would mean the root context still retains the orphan's context, and with it the orphan.
        var knownSubject = Assert.Single(registry.KnownSubjects.Keys);
        Assert.Same(root, knownSubject);

        Assert.Equal(2, subjectFactory.CreatedSubjects.Count);
        Assert.All(subjectFactory.CreatedSubjects, created =>
            Assert.False(created.Context.RemoveFallbackContext(root.Context),
                $"The rolled-back subject '{created.GetType().Name}' still had the root subject's context as a fallback."));
    }

    [Fact]
    public async Task WhenALoadFailsWhileHoldingTheStructureLock_ThenRollbackDoesNotDeadlock()
    {
        // Arrange: Root has a Sensor child whose own child fails with a transient browse status,
        // so Sensor is staged during discovery and then detached again by the rollback in
        // OpcUaLoadContext.Dispose. That detach runs inline and reaches the source's
        // SubjectDetaching callback on the very thread that is running the load.
        var (loader, source, subject) = CreateFixture();

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(
            mockSession,
            failOnNodeId: StatusId,
            browseTree: new Dictionary<NodeId, ReferenceDescription[]>
            {
                [RootId] =
                [
                    MakeReference("Sensor", SensorId, NodeClass.Object)
                ],
                [SensorId] =
                [
                    MakeReference("Status", StatusId, NodeClass.Object)
                ]
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        var structureLock = GetStructureLock(source);
        var completedInTime = true;
        Exception? loadException = null;

        // Act: hold the structure lock across the whole load, exactly as StartListeningAsync does
        // (OpcUaSubjectClientSource line ~122 takes _structureLock and releases it only after
        // LoadSubjectAsync returned). The load runs on a worker thread because the mocked session
        // completes synchronously, so a re-entrant acquisition on the rollback path would block
        // before LoadSubjectAsync ever handed back a task to await.
        await structureLock.WaitAsync(CancellationToken.None);
        Task<IReadOnlyList<MonitoredItem>>? loadTask = null;
        try
        {
            loadTask = Task.Run(
                () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

            try
            {
                await loadTask.WaitAsync(RollbackCompletionTimeout);
            }
            catch (TimeoutException)
            {
                completedInTime = false;
            }
            catch (Exception exception)
            {
                loadException = exception;
            }
        }
        finally
        {
            // Released before asserting so a deadlocked worker thread is freed instead of being
            // stranded for the rest of the test run.
            structureLock.Release();
            ObserveFaultedTask(loadTask);
        }

        // Assert
        Assert.True(completedInTime,
            $"The failed load did not finish within {RollbackCompletionTimeout.TotalSeconds} seconds while the structure lock was held. " +
            "Its rollback deadlocked: the inline subject detach re-entered the non-reentrant _structureLock on the thread that already owns it.");
        Assert.IsType<OpcUaTransientServiceException>(loadException);
    }

    /// <summary>
    /// Reads the source's private structure lock. <c>StartListeningAsync</c> (around line 122 of
    /// <c>OpcUaSubjectClientSource</c>) holds this semaphore across the whole
    /// <c>LoadSubjectAsync</c> call, and reproducing that hold is the only way to exercise the
    /// re-entrancy hazard on the rollback path from a unit test.
    /// </summary>
    private static SemaphoreSlim GetStructureLock(OpcUaSubjectClientSource source)
    {
        var field = typeof(OpcUaSubjectClientSource)
            .GetField("_structureLock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "OpcUaSubjectClientSource no longer has a '_structureLock' field. Update this test to hold whatever lock StartListeningAsync now holds across LoadSubjectAsync.");

        return (SemaphoreSlim)field.GetValue(source)!;
    }

    /// <summary>
    /// Marks a task's exception as observed. After a timeout the load task is abandoned but keeps
    /// running once the lock is released, and its eventual failure would otherwise surface as an
    /// unobserved task exception in an unrelated test.
    /// </summary>
    private static void ObserveFaultedTask(Task? task)
    {
        task?.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static (OpcUaSubjectLoader Loader, OpcUaSubjectClientSource Source, IInterceptorSubject Subject) CreateFixture()
    {
        var subjectContext = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle()
            .WithPropertyChangeSubscriptions();
        var subject = new DynamicSubject(subjectContext);

        var (loader, source) = CreateSourceAndLoaderFor(subject, shouldAddDynamicProperties: true);
        return (loader, source, subject);
    }

    /// <summary>
    /// Builds the source and its loader over the subject that the test then loads, which is the
    /// shape production uses: <c>OpcUaSubjectClientSource</c> constructs its loader over its own
    /// root subject. That matters beyond tidiness. <c>SourceOwnershipManager</c> subscribes to the
    /// <c>LifecycleInterceptor</c> reachable from the source's root subject, so a fixture that
    /// loaded a different subject would wire the detach callback to a lifecycle interceptor the
    /// loaded graph never touches and rollback-time detach behaviour would go untested.
    /// </summary>
    private static (OpcUaSubjectLoader Loader, OpcUaSubjectClientSource Source) CreateSourceAndLoaderFor(
        IInterceptorSubject subject,
        bool shouldAddDynamicProperties,
        OpcUaSubjectFactory? subjectFactory = null)
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = subjectFactory ?? new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = (_, _) => Task.FromResult(shouldAddDynamicProperties)
        };

        var source = new OpcUaSubjectClientSource(subject, config, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(
            subject,
            config,
            source.Ownership,
            source,
            NullLogger<OpcUaSubjectClientSource>.Instance);

        return (loader, source);
    }

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        var namespaceTable = new NamespaceTable();
        namespaceTable.Append("urn:test");
        mockSession.SetupGet(s => s.NamespaceUris).Returns(namespaceTable);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
        mockSession.SetupGet(s => s.TypeTree).Returns(new Mock<ITypeTable>().Object);
        return mockSession;
    }

    private static void ConfigureBrowseTree(
        Mock<ISession> mockSession,
        NodeId failOnNodeId,
        Dictionary<NodeId, ReferenceDescription[]> browseTree,
        uint failStatusCode = StatusCodes.BadServerHalted)
    {
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
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == failOnNodeId)
                    {
                        results.Add(new BrowseResult
                        {
                            StatusCode = failStatusCode,
                            References = []
                        });
                    }
                    else if (browseTree.TryGetValue(desc.NodeId, out var refs))
                    {
                        var collection = new ReferenceDescriptionCollection();
                        collection.AddRange(refs);
                        results.Add(new BrowseResult { References = collection });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
    }

    private static void ConfigureReadAsync(Mock<ISession> mockSession, Dictionary<NodeId, NodeId> dataTypes)
    {
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                var results = new DataValueCollection();
                for (var i = 0; i < nodesToRead.Count; i += 2)
                {
                    var nodeId = nodesToRead[i].NodeId;
                    if (dataTypes.TryGetValue(nodeId, out var dt))
                    {
                        results.Add(new DataValue { Value = dt, StatusCode = StatusCodes.Good });
                        results.Add(new DataValue { Value = -1, StatusCode = StatusCodes.Good });
                    }
                    else
                    {
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                    }
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });
    }

    private static ReferenceDescription MakeReference(string name, NodeId nodeId, NodeClass nodeClass)
    {
        return new ReferenceDescription
        {
            BrowseName = new QualifiedName(name),
            NodeId = new ExpandedNodeId(nodeId),
            NodeClass = nodeClass
        };
    }

    /// <summary>
    /// Records every subject the loader materializes for a single subject reference, keyed by the
    /// browse name it was created for. A staged subject whose binding is deferred to <c>Commit</c>
    /// is unreachable from the model after a failed load, so recording it at creation time is the
    /// only handle a rollback assertion has on it.
    /// </summary>
    private sealed class RecordingOpcUaSubjectFactory : OpcUaSubjectFactory
    {
        private readonly Dictionary<string, IInterceptorSubject> _createdSubjectsByBrowseName = new();

        public RecordingOpcUaSubjectFactory()
            : base(new DefaultSubjectFactory())
        {
        }

        public override async Task<IInterceptorSubject> CreateSubjectAsync(
            RegisteredSubjectProperty property,
            ReferenceDescription node,
            ISession session,
            CancellationToken cancellationToken)
        {
            var subject = await base.CreateSubjectAsync(property, node, session, cancellationToken);
            _createdSubjectsByBrowseName[node.BrowseName.Name] = subject;
            return subject;
        }

        public IReadOnlyCollection<IInterceptorSubject> CreatedSubjects => _createdSubjectsByBrowseName.Values;

        public IInterceptorSubject GetCreatedSubject(string browseName)
        {
            return _createdSubjectsByBrowseName.TryGetValue(browseName, out var subject)
                ? subject
                : throw new InvalidOperationException(
                    $"The loader never created a subject for the browse name '{browseName}', so the scenario did not stage what the test expects.");
        }
    }
}

[InterceptorSubject]
public partial class RollbackCollectionRoot
{
    [OpcUaNode("Parent")]
    public partial RollbackCollectionParent? Parent { get; set; }
}

[InterceptorSubject]
public partial class RollbackCollectionParent
{
    [OpcUaNode("Items")]
    public partial RollbackCollectionItem[]? Items { get; set; }
}

[InterceptorSubject]
public partial class RollbackCollectionItem
{
    [OpcUaNode("Value")]
    public partial double Value { get; set; }
}

[InterceptorSubject]
public partial class RollbackReferenceRoot
{
    [OpcUaNode("Parent")]
    public partial RollbackReferenceParent? Parent { get; set; }
}

[InterceptorSubject]
public partial class RollbackReferenceParent
{
    [OpcUaNode("Child")]
    public partial RollbackCollectionItem? Child { get; set; }
}

[InterceptorSubject]
public partial class RollbackLatePhaseRoot
{
    [OpcUaNode("Parent")]
    public partial RollbackLatePhaseParent? Parent { get; set; }
}

[InterceptorSubject]
public partial class RollbackLatePhaseParent
{
    [OpcUaNode("Items")]
    public partial RollbackCollectionItem[]? Items { get; set; }

    /// <summary>
    /// A plain value property alongside the collection. Its node is browsed by the attribute
    /// phase, which is the last phase of the level and so runs after the collection has bound.
    /// </summary>
    [OpcUaNode("Status")]
    public partial double Status { get; set; }
}

[InterceptorSubject]
public partial class RollbackGraphRoot
{
    [OpcUaNode("Holder")]
    public partial RollbackGraphHolder? Holder { get; set; }

    /// <summary>
    /// Reached both from here and from <see cref="RollbackGraphHolder.Child"/>, which is what makes
    /// the address space a graph instead of a tree: the subject is staged under the root subject
    /// but ends up bound under a different parent.
    /// </summary>
    [OpcUaNode("Shared")]
    public partial RollbackGraphShared? Shared { get; set; }
}

[InterceptorSubject]
public partial class RollbackGraphHolder
{
    [OpcUaNode("Child")]
    public partial RollbackGraphShared? Child { get; set; }
}

[InterceptorSubject]
public partial class RollbackGraphShared
{
    [OpcUaNode("Value")]
    public partial double Value { get; set; }
}

[InterceptorSubject]
public partial class RollbackDictionaryRoot
{
    [OpcUaNode("Parent")]
    public partial RollbackDictionaryParent? Parent { get; set; }
}

[InterceptorSubject]
public partial class RollbackDictionaryParent
{
    [OpcUaNode("Entries")]
    public partial IReadOnlyDictionary<string, RollbackCollectionItem>? Entries { get; set; }
}

[InterceptorSubject]
public partial class BindingOrderRoot
{
    [OpcUaNode("Items")]
    public partial RollbackReferenceParent[]? Items { get; set; }
}
