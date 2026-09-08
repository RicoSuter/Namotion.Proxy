using System;
using System.Collections.Generic;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Serialization;

public class SubjectUpdateFlowTests
{
    [Fact]
    public void WelcomePayload_SerializeAndApply_CollectionWithItems_ShouldSyncCollectionIndexesCorrectly()
    {
        // Arrange - Server with collection items
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var serverRoot = new TestRoot(serverContext)
        {
            Name = "RootWithItems",
            Items =
            [
                new TestItem(serverContext) { Label = "First", Value = 100 },
                new TestItem(serverContext) { Label = "Second", Value = 200 }
            ]
        };

        // Arrange - Client with null Items (to trigger "create new collection" code path)
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var clientRoot = new TestRoot(clientContext)
        {
            Items = null! // Force null to test create collection path which uses Index
        };

        // Act - Create Welcome, serialize, deserialize, apply (this is the path where Index becomes JsonElement)
        var serializer = new JsonWebSocketSerializer();
        var update = SubjectUpdate.CreateCompleteUpdate(serverRoot, []);

        var welcome = new WelcomePayload { Version = 1, Format = WebSocketFormat.Json, State = update };
        var bytes = serializer.SerializeMessage(MessageType.Welcome, welcome);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserializedWelcome = serializer.Deserialize<WelcomePayload>(bytes.AsSpan(payloadStart, payloadLength));

        // This should not throw an InvalidCastException for JsonElement -> int conversion
        clientRoot.ApplySubjectUpdate(deserializedWelcome.State!, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("RootWithItems", clientRoot.Name);
        Assert.Equal(2, clientRoot.Items.Length);
        Assert.Equal("First", clientRoot.Items[0].Label);
        Assert.Equal(100, clientRoot.Items[0].Value);
        Assert.Equal("Second", clientRoot.Items[1].Label);
        Assert.Equal(200, clientRoot.Items[1].Value);
    }

    [Fact]
    public void WelcomePayload_SerializeAndApply_CollectionGrows_ShouldReuseExistingAndAddNew()
    {
        // Arrange - Server with more items than client
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var serverRoot = new TestRoot(serverContext)
        {
            Name = "ServerWithMoreItems",
            Items =
            [
                new TestItem(serverContext) { Label = "UpdatedFirst", Value = 10 },
                new TestItem(serverContext) { Label = "UpdatedSecond", Value = 20 },
                new TestItem(serverContext) { Label = "NewThird", Value = 30 }
            ]
        };

        // Arrange - Client with existing items
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var existingItem0 = new TestItem(clientContext) { Label = "OldFirst", Value = 1 };
        var existingItem1 = new TestItem(clientContext) { Label = "OldSecond", Value = 2 };
        var clientRoot = new TestRoot(clientContext)
        {
            Items = [existingItem0, existingItem1]
        };

        // Act - Serialize and apply
        var serializer = new JsonWebSocketSerializer();
        var update = SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
        var welcome = new WelcomePayload { Version = 1, Format = WebSocketFormat.Json, State = update };
        var bytes = serializer.SerializeMessage(MessageType.Welcome, welcome);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserializedWelcome = serializer.Deserialize<WelcomePayload>(bytes.AsSpan(payloadStart, payloadLength));

        clientRoot.ApplySubjectUpdate(deserializedWelcome.State!, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - Collection should be replaced with new one containing 3 items
        Assert.Equal("ServerWithMoreItems", clientRoot.Name);
        Assert.Equal(3, clientRoot.Items.Length);

        // Existing items should be reused (same instance) with updated values
        Assert.Same(existingItem0, clientRoot.Items[0]);
        Assert.Equal("UpdatedFirst", clientRoot.Items[0].Label);
        Assert.Equal(10, clientRoot.Items[0].Value);

        Assert.Same(existingItem1, clientRoot.Items[1]);
        Assert.Equal("UpdatedSecond", clientRoot.Items[1].Label);
        Assert.Equal(20, clientRoot.Items[1].Value);

        // New item should be created
        Assert.NotSame(existingItem0, clientRoot.Items[2]);
        Assert.NotSame(existingItem1, clientRoot.Items[2]);
        Assert.Equal("NewThird", clientRoot.Items[2].Label);
        Assert.Equal(30, clientRoot.Items[2].Value);
    }

    [Fact]
    public void WelcomePayload_SerializeAndApply_ShouldSyncValue()
    {
        // Arrange - Server
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var serverRoot = new TestRoot(serverContext)
        {
            Name = "Initial"
        };

        // Arrange - Client
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var clientRoot = new TestRoot(clientContext);

        // Act - Create Welcome, serialize, deserialize, apply
        var serializer = new JsonWebSocketSerializer();
        var update = SubjectUpdate.CreateCompleteUpdate(serverRoot, []);

        // Verify the original update has the property
        var rootProps = update.Subjects[update.Root];
        Assert.True(rootProps.ContainsKey("Name"), "Original update should contain Name");
        Assert.Equal("Initial", rootProps["Name"].Value);

        var welcome = new WelcomePayload { Version = 1, Format = WebSocketFormat.Json, State = update };

        var bytes = serializer.SerializeMessage(MessageType.Welcome, welcome);
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("Initial", json); // Verify JSON contains the value

        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserializedWelcome = serializer.Deserialize<WelcomePayload>(bytes.AsSpan(payloadStart, payloadLength));

        // Verify deserialized update has the property
        Assert.NotNull(deserializedWelcome.State);
        var deserializedRootProps = deserializedWelcome.State.Subjects[deserializedWelcome.State.Root];
        Assert.True(deserializedRootProps.ContainsKey("Name"), "Deserialized update should contain Name");

        var nameUpdate = deserializedRootProps["Name"];
        Assert.Equal(SubjectPropertyUpdateKind.Value, nameUpdate.Kind);
        Assert.NotNull(nameUpdate.Value);

        clientRoot.ApplySubjectUpdate(deserializedWelcome.State!, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Initial", clientRoot.Name);
    }

    [Fact]
    public void PartialUpdate_CollectionInsert_ShouldRoundTripThroughJson()
    {
        // Arrange - Server with initial collection
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var existingItem = new TestItem(serverContext) { Label = "Existing", Value = 1 };
        var serverRoot = new TestRoot(serverContext) { Name = "Root", Items = [existingItem] };

        // Client with matching initial state
        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientRoot = new TestRoot(clientContext) { Name = "Root", Items = [new TestItem(clientContext) { Label = "Existing", Value = 1 }] };

        // Make change - add item
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            serverRoot.Items = [existingItem, new TestItem(serverContext) { Label = "New", Value = 2 }];
        }

        // Act - Create partial update, serialize through JSON, deserialize, apply
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(serverRoot, changes.ToArray(), []);
        var serializer = new JsonWebSocketSerializer();
        var bytes = serializer.SerializeMessage(MessageType.Update, update);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserialized = serializer.Deserialize<SubjectUpdate>(bytes.AsSpan(payloadStart, payloadLength));

        clientRoot.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, clientRoot.Items.Length);
        Assert.Equal("Existing", clientRoot.Items[0].Label);
        Assert.Equal("New", clientRoot.Items[1].Label);
        Assert.Equal(2, clientRoot.Items[1].Value);
    }

    [Fact]
    public void PartialUpdate_CollectionRemove_ShouldRoundTripThroughJson()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var item1 = new TestItem(serverContext) { Label = "First", Value = 1 };
        var item2 = new TestItem(serverContext) { Label = "Second", Value = 2 };
        var serverRoot = new TestRoot(serverContext) { Name = "Root", Items = [item1, item2] };

        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientRoot = new TestRoot(clientContext)
        {
            Name = "Root",
            Items = [new TestItem(clientContext) { Label = "First", Value = 1 }, new TestItem(clientContext) { Label = "Second", Value = 2 }]
        };

        // Make change - remove first item
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            serverRoot.Items = [item2];
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(serverRoot, changes.ToArray(), []);
        var serializer = new JsonWebSocketSerializer();
        var bytes = serializer.SerializeMessage(MessageType.Update, update);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserialized = serializer.Deserialize<SubjectUpdate>(bytes.AsSpan(payloadStart, payloadLength));

        clientRoot.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Single(clientRoot.Items);
        Assert.Equal("Second", clientRoot.Items[0].Label);
    }

    [Fact]
    public void PartialUpdate_DictionaryValueReplacedAtSameKey_ShouldRoundTripThroughJson()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var originalItem = new TestItem(serverContext) { Label = "Original", Value = 1 };
        var serverRoot = new TestRoot(serverContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, TestItem> { ["key1"] = originalItem }
        };

        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientRoot = new TestRoot(clientContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, TestItem> { ["key1"] = new TestItem(clientContext) { Label = "Original", Value = 1 } }
        };

        // Make change - replace the value at key1 with a different subject
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            serverRoot.Lookup = new Dictionary<string, TestItem>
            {
                ["key1"] = new TestItem(serverContext) { Label = "Replacement", Value = 2 }
            };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(serverRoot, changes.ToArray(), []);
        var serializer = new JsonWebSocketSerializer();
        var bytes = serializer.SerializeMessage(MessageType.Update, update);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserialized = serializer.Deserialize<SubjectUpdate>(bytes.AsSpan(payloadStart, payloadLength));

        clientRoot.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.True(clientRoot.Lookup.ContainsKey("key1"));
        Assert.Equal("Replacement", clientRoot.Lookup["key1"].Label);
        Assert.Equal(2, clientRoot.Lookup["key1"].Value);
        Assert.Single(clientRoot.Lookup);
    }

    [Fact]
    public void PartialUpdate_CollectionMove_ShouldRoundTripThroughJson()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var item1 = new TestItem(serverContext) { Label = "A", Value = 1 };
        var item2 = new TestItem(serverContext) { Label = "B", Value = 2 };
        var item3 = new TestItem(serverContext) { Label = "C", Value = 3 };
        var serverRoot = new TestRoot(serverContext) { Name = "Root", Items = [item1, item2, item3] };

        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientRoot = new TestRoot(clientContext)
        {
            Name = "Root",
            Items =
            [
                new TestItem(clientContext) { Label = "A", Value = 1 },
                new TestItem(clientContext) { Label = "B", Value = 2 },
                new TestItem(clientContext) { Label = "C", Value = 3 }
            ]
        };

        // Make change - move C to front: [C, A, B]
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            serverRoot.Items = [item3, item1, item2];
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(serverRoot, changes.ToArray(), []);
        var serializer = new JsonWebSocketSerializer();
        var bytes = serializer.SerializeMessage(MessageType.Update, update);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserialized = serializer.Deserialize<SubjectUpdate>(bytes.AsSpan(payloadStart, payloadLength));

        clientRoot.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(3, clientRoot.Items.Length);
        Assert.Equal("C", clientRoot.Items[0].Label);
        Assert.Equal("A", clientRoot.Items[1].Label);
        Assert.Equal("B", clientRoot.Items[2].Label);
    }

    [Fact]
    public void PartialUpdate_WithTimestamp_ShouldPreserveThroughJson()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var timestamp = new DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var serverRoot = new TestRoot(serverContext) { Name = "Initial" };

        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientRoot = new TestRoot(clientContext) { Name = "Initial" };

        // Make change with timestamp
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            using (SubjectChangeContext.WithChangedTimestamp(timestamp))
            {
                serverRoot.Name = "Updated";
            }
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(serverRoot, changes.ToArray(), []);
        var serializer = new JsonWebSocketSerializer();
        var bytes = serializer.SerializeMessage(MessageType.Update, update);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserialized = serializer.Deserialize<SubjectUpdate>(bytes.AsSpan(payloadStart, payloadLength));

        clientRoot.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Updated", clientRoot.Name);
        Assert.Equal(timestamp, clientRoot.GetPropertyReference("Name").TryGetWriteTimestamp());
    }

    [Fact]
    public void PartialUpdate_DictionaryInsertRemove_ShouldRoundTripThroughJson()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var existingItem = new TestItem(serverContext) { Label = "Existing", Value = 1 };
        var serverRoot = new TestRoot(serverContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, TestItem> { ["key1"] = existingItem }
        };

        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientRoot = new TestRoot(clientContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, TestItem>
            {
                ["key1"] = new TestItem(clientContext) { Label = "Existing", Value = 1 }
            }
        };

        // Make change - remove key1, add key2
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            serverRoot.Lookup = new Dictionary<string, TestItem>
            {
                ["key2"] = new TestItem(serverContext) { Label = "New", Value = 2 }
            };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(serverRoot, changes.ToArray(), []);
        var serializer = new JsonWebSocketSerializer();
        var bytes = serializer.SerializeMessage(MessageType.Update, update);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserialized = serializer.Deserialize<SubjectUpdate>(bytes.AsSpan(payloadStart, payloadLength));

        clientRoot.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Single(clientRoot.Lookup);
        Assert.False(clientRoot.Lookup.ContainsKey("key1"));
        Assert.True(clientRoot.Lookup.ContainsKey("key2"));
        Assert.Equal("New", clientRoot.Lookup["key2"].Label);
    }

    [Fact]
    public void PartialUpdate_ObjectReferenceSetToNull_ShouldRoundTripThroughJson()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var serverRoot = new TestRoot(serverContext) { Name = "Root", Child = new TestItem(serverContext) { Label = "Child", Value = 42 } };

        var clientContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var clientRoot = new TestRoot(clientContext) { Name = "Root", Child = new TestItem(clientContext) { Label = "Child", Value = 42 } };

        // Make change - set Child to null
        var changes = new List<SubjectPropertyChange>();
        using (serverContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            serverRoot.Child = null;
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(serverRoot, changes.ToArray(), []);
        var serializer = new JsonWebSocketSerializer();
        var bytes = serializer.SerializeMessage(MessageType.Update, update);
        var (_, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(bytes);
        var deserialized = serializer.Deserialize<SubjectUpdate>(bytes.AsSpan(payloadStart, payloadLength));

        clientRoot.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(clientRoot.Child);
    }
}
