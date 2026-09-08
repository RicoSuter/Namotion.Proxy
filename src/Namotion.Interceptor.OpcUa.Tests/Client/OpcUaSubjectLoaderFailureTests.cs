using System.Reactive.Concurrency;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderFailureTests : OpcUaSubjectLoaderTestsBase
{
    private static readonly NodeId RootId = new(1, 0);
    private static readonly NodeId SensorId = new(2001, 2);
    private static readonly NodeId StatusId = new(2002, 2);
    private static readonly NodeId TemperatureId = new(1001, 2);

    /// <summary>
    /// The address space most tests here load: Root carries the Temperature variable and the Sensor
    /// object, and Sensor carries the Status object. Sensor and Status are created and staged during
    /// the load, so a failure on Status's level leaves Sensor staged and Temperature's claim queued.
    /// </summary>
    private static readonly Dictionary<NodeId, ReferenceDescription[]> SensorTree = new()
    {
        [RootId] =
        [
            CreateTestReferenceDescription("Temperature", TemperatureId),
            CreateObjectReferenceDescription("Sensor", SensorId)
        ],
        [SensorId] = [CreateObjectReferenceDescription("Status", StatusId)]
    };

    private static readonly Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)> SensorTreeDataTypes = new()
    {
        [TemperatureId] = (DataTypeIds.Double, -1)
    };

    /// <summary>
    /// Upper bound for a failed load to unwind. The work itself is a handful of mocked calls with
    /// no real I/O, so anything approaching this bound means the rollback is blocked rather than
    /// slow. Generous enough not to fire on a loaded CI agent.
    /// </summary>
    private static readonly TimeSpan RollbackCompletionTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task WhenLoadFailsDuringDiscovery_ThenRootRemainsAtPreLoadState()
    {
        // Arrange: Status fails its browse transiently. By then Temperature's claim and Sensor's
        // binding are queued and Sensor is staged, so a load that applied either eagerly, or a
        // rollback that missed the staged subject, is visible.
        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, SensorTree, failWhile: nodeId => nodeId == StatusId);
        SetupReadAsync(mockSession, SensorTreeDataTypes);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: the dynamic property slots added during discovery may remain on the root, but they
        // hold no value, no claim reached the source, and the registry knows no subject it did not
        // know before the load.
        Assert.All(subject.TryGetRegisteredSubject()!.Properties, property => Assert.Null(property.GetValue()));
        Assert.Empty(source.Ownership.Properties);
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());
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
        // Arrange: the browse of Status fails transiently during the first load and succeeds
        // during the second.
        var (loader, source, subject) = CreateFixture();

        var failStatusBrowse = true;
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, SensorTree, failWhile: nodeId => failStatusBrowse && nodeId == StatusId);
        SetupReadAsync(mockSession, SensorTreeDataTypes);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        failStatusBrowse = false;
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: the retry is independent of the failed attempt because the rollback discarded
        // every staged subject: Temperature is monitored and claimed under its NodeId, Sensor is
        // bound with its Status child, and the registry holds exactly the final graph (Root,
        // Sensor, Status) with no orphan from the failed attempt.
        AssertMonitoredAndClaimed(source, monitoredItems, TemperatureId);
        AssertSensorIsBound(subject, "Status");
        Assert.Equal(3, subject.Context.TryGetService<ISubjectRegistry>()!.KnownSubjects.Count);
    }

    [Fact]
    public async Task WhenLoadIsCancelledWhileAChildIsStaged_ThenRollbackIsCompleteAndALaterLoadSucceeds()
    {
        // Arrange: the mock cancels the load's token from inside the browse of Status, the first
        // browse the loader issues after Sensor has been staged and Temperature's claim queued. The
        // mocked session ignores the token, so only the loader's own checks between phases can stop
        // the load, and they must surface the cancellation as OperationCanceledException with the
        // staged subject detached again.
        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        using var cancellation = new CancellationTokenSource();
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, nodeId =>
        {
            if (nodeId == StatusId)
            {
                cancellation.Cancel();
            }
            return CreateBrowseResult(SensorTree, nodeId);
        });
        SetupReadAsync(mockSession, SensorTreeDataTypes);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, cancellation.Token));

        // Assert
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());
        Assert.Empty(source.Ownership.Properties);

        // Act: the second load runs with a live token.
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        AssertMonitoredAndClaimed(source, monitoredItems, TemperatureId);
        AssertSensorIsBound(subject, "Status");
    }

    [Fact]
    public async Task WhenTheSubjectFactoryThrowsForACollectionElement_ThenRollbackIsCompleteAndALaterLoadSucceeds()
    {
        // Arrange: Root.Items is a dynamic collection of two elements. The factory rejects the
        // second, so the first has already been created and staged when the load fails, while the
        // container binding for Items has not been queued yet.
        var itemsId = new NodeId(4601, 2);
        var firstItemId = new NodeId(4602, 2);
        var secondItemId = new NodeId(4603, 2);
        var firstValueId = new NodeId(4604, 2);
        var secondValueId = new NodeId(4605, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [RootId] =
            [
                CreateTestReferenceDescription("Temperature", TemperatureId),
                CreateObjectReferenceDescription("Items", itemsId)
            ],
            [itemsId] =
            [
                CreateObjectReferenceDescription("Items[0]", firstItemId),
                CreateObjectReferenceDescription("Items[1]", secondItemId)
            ],
            [firstItemId] = [CreateTestReferenceDescription("Value", firstValueId)],
            [secondItemId] = [CreateTestReferenceDescription("Value", secondValueId)]
        };

        var subjectFactory = new RejectingCollectionSubjectFactory();
        var (loader, source, subject) = CreateFixture(subjectFactory);
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [TemperatureId] = (DataTypeIds.Double, -1),
            [firstValueId] = (DataTypeIds.Double, -1),
            [secondValueId] = (DataTypeIds.Double, -1)
        });

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());
        Assert.Empty(source.Ownership.Properties);

        // Act: the second load runs against a factory that accepts every element.
        subjectFactory.RejectSecondElement = false;
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        AssertMonitoredAndClaimed(source, monitoredItems, TemperatureId, firstValueId, secondValueId);
        var itemsProperty = subject.TryGetRegisteredSubject()!.Properties.Single(property => property.Name == "Items");
        var items = Assert.IsType<DynamicSubject[]>(itemsProperty.GetValue());
        Assert.Equal(2, items.Length);
    }

    [Fact]
    public async Task WhenAReadFailsTransientlyUnderAStagedChild_ThenRollbackIsCompleteAndALaterLoadSucceeds()
    {
        // Arrange: Sensor is staged while Root's level is loaded. On Sensor's own level its Reading
        // variable is typed by reading DataType and ValueRank, and that read returns
        // BadServerNotConnected, which the type resolver classifies as transient and rethrows.
        var readingId = new NodeId(2003, 2);
        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [RootId] = SensorTree[RootId],
            [SensorId] = [CreateTestReferenceDescription("Reading", readingId)]
        };

        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var failReadingRead = true;
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(
            mockSession,
            new Dictionary<NodeId, (NodeId, int)>
            {
                [TemperatureId] = (DataTypeIds.Double, -1),
                [readingId] = (DataTypeIds.Double, -1)
            },
            failWhile: nodeId => failReadingRead && nodeId == readingId,
            failStatusCode: StatusCodes.BadServerNotConnected);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());
        Assert.Empty(source.Ownership.Properties);

        // Act: the second load reads cleanly.
        failReadingRead = false;
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        AssertMonitoredAndClaimed(source, monitoredItems, TemperatureId, readingId);
        AssertSensorIsBound(subject, "Reading");
    }

    [Fact]
    public async Task WhenABindingThrowsInsideCommit_ThenRollbackIsCompleteAndALaterLoadSucceeds()
    {
        // Arrange: Sensor is declared on the root before the load, so the loader binds it through
        // the declared setter, and that setter throws on the first load. Discovery has completed by
        // then, and Commit has claimed Temperature and bound sensor.Status, so the failure exercises
        // the Commit rollback rather than the discovery one, and the load must surface the setter's
        // exception unchanged.
        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        object? sensorValue = null;
        var sensorSetterThrows = true;
        var sensorProperty = subject.TryGetRegisteredSubject()!.AddProperty(
            "Sensor",
            typeof(DynamicSubject),
            _ => sensorValue,
            (_, value) =>
            {
                if (sensorSetterThrows)
                {
                    throw new InvalidOperationException("The setter rejects the first binding.");
                }
                sensorValue = value;
            },
            new OpcUaNodeAttribute("Sensor"));

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, SensorTree);
        SetupReadAsync(mockSession, SensorTreeDataTypes);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert
        Assert.Null(sensorProperty.GetValue());
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());
        Assert.Empty(source.Ownership.Properties);

        // Act: the second load binds through a setter that accepts the value.
        sensorSetterThrows = false;
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        AssertMonitoredAndClaimed(source, monitoredItems, TemperatureId);
        AssertSensorIsBound(subject, "Status");
    }

    [Fact]
    public async Task WhenLoadSucceeds_ThenBindingsAreAppliedAfterAllBrowsesComplete()
    {
        // Arrange: the browse count is sampled at the moment root.Sensor and sensor.Status are
        // assigned. The load browses four times: Root; Sensor, for its type while Root's children
        // are classified; Status, likewise on the level below; and Temperature, for its attributes
        // after the recursion. Sensor and Status are served from the browse cache when their own
        // levels are loaded. A loader that bound root.Sensor where it prepares the reference would
        // sample 2 and one that bound sensor.Status there would sample 3; only bindings deferred to
        // Commit sample the final count for both. The earlier fixture, a childless Sensor alone
        // under Root, could not tell the two apart: its only browses (Root, then Sensor for its
        // type) were both over before the reference was prepared, so a live binding sampled the
        // final count as well.
        var (loader, _, subject) = CreateFixture();

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, SensorTree);
        SetupReadAsync(mockSession, SensorTreeDataTypes);

        int BrowseCount() => mockSession.Invocations.Count(invocation => invocation.Method.Name == nameof(ISession.BrowseAsync));

        var browseCountAtSensorAssignment = -1;
        var browseCountAtStatusAssignment = -1;
        using var subscription = subject.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, subject) && change.Property.Name == "Sensor")
                {
                    browseCountAtSensorAssignment = BrowseCount();
                }
                else if (change.Property.Name == "Status")
                {
                    browseCountAtStatusAssignment = BrowseCount();
                }
            });

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Equal(4, BrowseCount());
        Assert.Equal(4, browseCountAtSensorAssignment);
        Assert.Equal(4, browseCountAtStatusAssignment);
    }

    [Fact]
    public async Task WhenLoadSucceeds_ThenSourceClaimsHappenBeforeRootAssignmentInCommit()
    {
        // Arrange: Temperature queues a claim and Sensor queues a root binding. Subscribe to the
        // property change observable and capture the ownership count synchronously at the moment
        // Sensor is assigned. If Commit ordering is correct (claims before bindings), the captured
        // count equals the final claim count. A regression that applies bindings first would
        // capture 0 here.
        var (loader, source, subject) = CreateFixture();

        var ownedCountAtSensorAssignment = -1;

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, SensorTree);
        SetupReadAsync(mockSession, SensorTreeDataTypes);

        using var subscription = subject.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, subject) && change.Property.Name == "Sensor")
                {
                    ownedCountAtSensorAssignment = source.Ownership.Properties.Count;
                }
            });

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

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
            [RootId] = [CreateObjectReferenceDescription("Items", itemsId)],
            [itemsId] =
            [
                CreateObjectReferenceDescription("Items[0]", firstItemId),
                CreateObjectReferenceDescription("Items[1]", secondItemId)
            ],
            [firstItemId] = [CreateObjectReferenceDescription("Child", firstChildId)],
            [secondItemId] = [CreateObjectReferenceDescription("Child", secondChildId)]
        };

        var modelContext = CreateSubjectContext().WithPropertyChangeSubscriptions();
        var root = new BindingOrderRoot(modelContext);
        var (loader, _, _) = CreateLoaderFor(root);

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);

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

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

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
        // Arrange: 3-level tree Root -> ParentA (staged) -> ChildB (staged) -> fail.
        // Both ParentA and ChildB are created during discovery as staged subjects.
        // If rollback only unregisters one level, the other becomes an orphan.
        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var parentAId = new NodeId(3001, 2);
        var childBId = new NodeId(3002, 2);
        var leafFailId = new NodeId(3003, 2);

        var mockSession = CreateMockSession();
        SetupBrowseAsync(
            mockSession,
            new Dictionary<NodeId, ReferenceDescription[]>
            {
                [RootId] = [CreateObjectReferenceDescription("ParentA", parentAId)],
                [parentAId] = [CreateObjectReferenceDescription("ChildB", childBId)],
                [childBId] = [CreateObjectReferenceDescription("LeafFail", leafFailId)]
            },
            failWhile: nodeId => nodeId == leafFailId);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: registry only contains pre-load subjects. Both ParentA and ChildB
        // were created during discovery; both must be unregistered on rollback.
        Assert.Equal(preLoadKeys, registry.KnownSubjects.Keys.ToHashSet());

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
        SetupBrowseAsync(mockSession, SensorTree, failWhile: nodeId => nodeId == SensorId, failStatusCode: StatusCodes.BadNodeIdUnknown);
        SetupReadAsync(mockSession, SensorTreeDataTypes);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenACollectionNodeIsOmittedFromTheBrowse_ThenItsItemsAreKeptAndTheSiblingIsMonitored(bool browseFailsPermanently)
    {
        // Arrange: Parent.Items holds two items before the load and its node is left out of the
        // browse result, either because the browse returns BadUserAccessDenied, which the load
        // skips as permanent, or because the node keeps paging past MaxBrowseContinuations, after
        // which BrowseNodesAsync drops it rather than report a truncated child list. Either way the
        // loader must keep the items it has, and the sibling Status variable must still be loaded.
        var parentId = new NodeId(4701, 2);
        var itemsId = new NodeId(4702, 2);
        var statusId = new NodeId(4703, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [parentId] =
            [
                CreateObjectReferenceDescription("Items", itemsId),
                CreateTestReferenceDescription("Status", statusId)
            ],
            [itemsId] = [CreateObjectReferenceDescription("Items[0]", new NodeId(4704, 2))]
        };

        var modelContext = CreateSubjectContext();
        var parent = new RollbackLatePhaseParent(modelContext);
        var itemOne = new RollbackCollectionItem(modelContext);
        var itemTwo = new RollbackCollectionItem(modelContext);
        parent.Items = [itemOne, itemTwo];
        var (loader, _, source) = CreateLoaderFor(parent, maxBrowseContinuations: 1);

        var mockSession = CreateMockSession();
        if (browseFailsPermanently)
        {
            SetupBrowseAsync(mockSession, browseTree, failWhile: nodeId => nodeId == itemsId, failStatusCode: StatusCodes.BadUserAccessDenied);
        }
        else
        {
            SetupBrowseAsyncPagingForever(mockSession, browseTree, itemsId);
        }

        var parentNode = CreateObjectReferenceDescription("Parent", parentId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(parent, parentNode, mockSession.Object, CancellationToken.None);

        // Assert
        var items = Assert.IsType<RollbackCollectionItem[]>(parent.Items);
        Assert.Equal(2, items.Length);
        Assert.Same(itemOne, items[0]);
        Assert.Same(itemTwo, items[1]);
        AssertMonitoredAndClaimed(source, monitoredItems, statusId);
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
            [RootId] = [CreateObjectReferenceDescription("Parent", parentId)],
            [parentId] = [CreateObjectReferenceDescription("Items", itemsId)],
            [itemsId] =
            [
                CreateObjectReferenceDescription("Items[0]", firstItemId),
                CreateObjectReferenceDescription("Items[1]", secondItemId)
            ],
            [firstItemId] = [CreateTestReferenceDescription("Value", firstValueId)],
            [secondItemId] = [CreateTestReferenceDescription("Value", secondValueId)]
        };

        var modelContext = CreateSubjectContext();
        var root = new RollbackCollectionRoot(modelContext);
        root.Parent = new RollbackCollectionParent(modelContext);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var (loader, _, source) = CreateLoaderFor(root);

        var failSecondItemBrowse = true;
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree, failWhile: nodeId => failSecondItemBrowse && nodeId == secondItemId);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

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
        AssertMonitoredAndClaimed(source, monitoredItems, firstValueId, secondValueId);
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
            [RootId] = [CreateObjectReferenceDescription("Parent", parentId)],
            [parentId] = [CreateObjectReferenceDescription("Entries", entriesId)],
            [entriesId] =
            [
                CreateObjectReferenceDescription("Entries[KeyA]", firstEntryId),
                CreateObjectReferenceDescription("Entries[KeyB]", secondEntryId)
            ],
            [firstEntryId] = [CreateTestReferenceDescription("Value", firstValueId)],
            [secondEntryId] = [CreateTestReferenceDescription("Value", secondValueId)]
        };

        var modelContext = CreateSubjectContext();
        var root = new RollbackDictionaryRoot(modelContext);
        root.Parent = new RollbackDictionaryParent(modelContext);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var (loader, _, source) = CreateLoaderFor(root);

        var failSecondEntryBrowse = true;
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree, failWhile: nodeId => failSecondEntryBrowse && nodeId == secondEntryId);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

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
        AssertMonitoredAndClaimed(source, monitoredItems, firstValueId, secondValueId);
    }

    [Fact]
    public async Task WhenASubjectReferenceLoadFailsUnderANonRootParent_ThenALaterLoadStillRegistersTheChild()
    {
        // Arrange: identical in shape to the collection and dictionary cases above, but through the
        // single subject reference branch, whose binding PrepareSubjectReferenceAsync queues on its
        // own and so needs its own regression pin. Root.Parent is assigned before the load, so the
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
            [RootId] = [CreateObjectReferenceDescription("Parent", parentId)],
            [parentId] = [CreateObjectReferenceDescription("Child", childId)],
            [childId] = [CreateTestReferenceDescription("Value", valueId)]
        };

        var modelContext = CreateSubjectContext();
        var root = new RollbackReferenceRoot(modelContext);
        root.Parent = new RollbackReferenceParent(modelContext);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var (loader, _, source) = CreateLoaderFor(root);

        var failChildBrowse = true;
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree, failWhile: nodeId => failChildBrowse && nodeId == childId);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

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
        AssertMonitoredAndClaimed(source, monitoredItems, valueId);
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
            [RootId] = [CreateObjectReferenceDescription("Parent", parentId)],
            [parentId] =
            [
                CreateObjectReferenceDescription("Items", itemsId),
                CreateTestReferenceDescription("Status", statusId)
            ],
            [itemsId] =
            [
                CreateObjectReferenceDescription("Items[0]", firstItemId),
                CreateObjectReferenceDescription("Items[1]", secondItemId)
            ],
            [firstItemId] = [CreateTestReferenceDescription("Value", firstValueId)],
            [secondItemId] = [CreateTestReferenceDescription("Value", secondValueId)]
        };

        var modelContext = CreateSubjectContext();
        var root = new RollbackLatePhaseRoot(modelContext);
        root.Parent = new RollbackLatePhaseParent(modelContext);
        var registry = modelContext.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var (loader, _, source) = CreateLoaderFor(root);

        var failStatusAttributeBrowse = true;
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree, failWhile: nodeId => failStatusAttributeBrowse && nodeId == statusId);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

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
        AssertMonitoredAndClaimed(source, monitoredItems, firstValueId, secondValueId, statusId);
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
                CreateObjectReferenceDescription("ChildA", childAId),
                CreateTestReferenceDescription("Status", statusId)
            ],
            [childAId] = [CreateObjectReferenceDescription("ChildB", childBId)],
            [childBId] = [CreateObjectReferenceDescription("BackToRoot", childAId)]
        };

        var subjectFactory = new RecordingOpcUaSubjectFactory();
        var (loader, _, root) = CreateFixture(subjectFactory);
        var registry = root.Context.TryGetService<ISubjectRegistry>()!;

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree, failWhile: nodeId => nodeId == statusId);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)> { [statusId] = (DataTypeIds.Double, -1) });

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(root, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: the registry is back at its pre-load state and no staging link outlived the
        // rollback. Each subject was staged under the context of the parent that discovered it,
        // ChildA under the root and ChildB under ChildA, and RemoveFallbackContext reports whether
        // that link was still there to remove, so `true` would mean the parent context still
        // retains the orphan's context, and with it the orphan.
        var knownSubject = Assert.Single(registry.KnownSubjects.Keys);
        Assert.Same(root, knownSubject);

        Assert.Equal(2, subjectFactory.CreatedSubjects.Count);
        var childA = subjectFactory.GetCreatedSubject("ChildA");
        var childB = subjectFactory.GetCreatedSubject("ChildB");
        Assert.False(childA.Context.RemoveFallbackContext(root.Context),
            "ChildA still had the root subject's context as a fallback after the rollback.");
        Assert.False(childB.Context.RemoveFallbackContext(childA.Context),
            "ChildB still had ChildA's context as a fallback after the rollback.");
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
        SetupBrowseAsync(mockSession, SensorTree, failWhile: nodeId => nodeId == StatusId);
        SetupReadAsync(mockSession, SensorTreeDataTypes);

        var rootNode = CreateObjectReferenceDescription("Root", RootId);

        var structureLock = GetStructureLock(source);
        var completedInTime = true;
        Exception? loadException = null;

        // Act: hold the structure lock across the whole load, exactly as StartListeningAsync does
        // (it takes _structureLock before LoadSubjectAsync and releases it only after the call
        // returned). The load runs on a worker thread because the mocked session completes
        // synchronously, so a re-entrant acquisition on the rollback path would block before
        // LoadSubjectAsync ever handed back a task to await.
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
    /// Reads the source's private structure lock. <c>StartListeningAsync</c> holds this semaphore
    /// across the whole <c>LoadSubjectAsync</c> call, and reproducing that hold is the only way to
    /// exercise the re-entrancy hazard on the rollback path from a unit test.
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

    /// <summary>
    /// A dynamic root with dynamic properties enabled, on a context that also publishes property
    /// changes, and the source built over it. Hands back the source itself because the assertions
    /// here need <c>TryGetNodeId</c> and the structure lock as well as its ownership manager.
    /// </summary>
    private (OpcUaSubjectLoader Loader, OpcUaSubjectClientSource Source, IInterceptorSubject Subject) CreateFixture(
        OpcUaSubjectFactory? subjectFactory = null)
    {
        var subject = new DynamicSubject(CreateSubjectContext().WithPropertyChangeSubscriptions());
        var (loader, _, source) = CreateLoaderFor(
            subject,
            shouldAddDynamicProperties: static (_, _) => Task.FromResult(true),
            subjectFactory: subjectFactory);

        return (loader, source, subject);
    }

    /// <summary>
    /// Asserts that the root's Sensor property is bound to a registered subject carrying
    /// <paramref name="childPropertyName"/>, which only the load of Sensor's own level adds.
    /// </summary>
    private static void AssertSensorIsBound(IInterceptorSubject root, string childPropertyName)
    {
        var sensorProperty = root.TryGetRegisteredSubject()!.Properties.Single(property => property.Name == "Sensor");
        var sensor = Assert.IsAssignableFrom<IInterceptorSubject>(sensorProperty.GetValue());
        var registeredSensor = sensor.TryGetRegisteredSubject();
        Assert.NotNull(registeredSensor);
        Assert.Contains(registeredSensor.Properties, property => property.Name == childPropertyName);
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

    /// <summary>
    /// Rejects the second element of a collection while <see cref="RejectSecondElement"/> is set,
    /// after the loader has created and staged the first.
    /// </summary>
    private sealed class RejectingCollectionSubjectFactory : OpcUaSubjectFactory
    {
        public RejectingCollectionSubjectFactory()
            : base(new DefaultSubjectFactory())
        {
        }

        public bool RejectSecondElement { get; set; } = true;

        public override Task<IInterceptorSubject> CreateCollectionSubjectAsync(
            RegisteredSubjectProperty collectionProperty,
            ReferenceDescription node,
            object? index,
            ISession session,
            CancellationToken cancellationToken)
        {
            if (RejectSecondElement && index is 1)
            {
                throw new InvalidOperationException("The factory rejects the second element.");
            }

            return base.CreateCollectionSubjectAsync(collectionProperty, node, index, session, cancellationToken);
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
