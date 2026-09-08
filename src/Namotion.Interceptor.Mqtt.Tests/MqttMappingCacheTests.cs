using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Mqtt.Tests;

/// <summary>
/// A connector's own root subject is anchored to the context rather than held by a parent property,
/// so nothing increments its reference count and it permanently reports zero. These pin that its
/// properties still reach the topic and path caches, that a subject which has left the graph does
/// not, and that a lookup landing in the middle of a detach evicts its own entry.
/// </summary>
public class MqttMappingCacheTests
{
    [Fact]
    public async Task WhenRootSubjectPropertyIsMappedTwice_ThenTheServerTopicCacheServesTheSecondLookup()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;

        // Act
        var first = server.TryGetTopicForProperty(property.Reference, property);
        var second = server.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal("name", first.Topic);
        Assert.Equal("name", second.Topic);
        Assert.Equal(1, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenRootSubjectPathIsResolvedTwice_ThenTheServerPathCacheServesTheSecondLookup()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);

        // Act
        var first = await server.TryGetPropertyForTopicAsync("name", CancellationToken.None);
        var second = await server.TryGetPropertyForTopicAsync("name", CancellationToken.None);

        // Assert
        Assert.Equal(nameof(MqttCacheTestRoot.Name), first?.Name);
        Assert.Equal(nameof(MqttCacheTestRoot.Name), second?.Name);
        Assert.Equal(1, mapper.PropertyLookupCount);
    }

    [Fact]
    public async Task WhenRootSubjectPropertyIsMappedTwice_ThenTheClientTopicCacheServesTheSecondLookup()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;

        // Act
        var first = client.TryGetTopicForProperty(property.Reference, property);
        var second = client.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal("name", first.Topic);
        Assert.Equal("name", second.Topic);
        Assert.Equal(1, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenRootSubjectTopicIsResolvedTwice_ThenTheClientTopicCacheServesTheSecondLookup()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);

        // Act
        var first = await client.TryGetPropertyForTopicAsync("name");
        var second = await client.TryGetPropertyForTopicAsync("name");

        // Assert
        Assert.Equal(nameof(MqttCacheTestRoot.Name), first?.Name);
        Assert.Equal(nameof(MqttCacheTestRoot.Name), second?.Name);
        Assert.Equal(1, mapper.PropertyLookupCount);
    }

    [Fact]
    public async Task WhenSubjectHasLeftTheRegistry_ThenTheServerTopicCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);
        var detachedProperty = DetachChildAndReturnStaleProperty(subject);

        // Act
        server.TryGetTopicForProperty(detachedProperty.Reference, detachedProperty);
        server.TryGetTopicForProperty(detachedProperty.Reference, detachedProperty);

        // Assert
        Assert.Equal(2, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenSubjectHasLeftTheRegistry_ThenTheClientTopicCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);
        var detachedProperty = DetachChildAndReturnStaleProperty(subject);

        // Act
        client.TryGetTopicForProperty(detachedProperty.Reference, detachedProperty);
        client.TryGetTopicForProperty(detachedProperty.Reference, detachedProperty);

        // Assert
        Assert.Equal(2, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenTheConnectorRootHasLeftTheContext_ThenTheClientTopicCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestRoot.Name))!;
        ((IInterceptorSubject)subject).Context.TryGetLifecycleInterceptor()!.DetachSubjectFromContext(subject);

        // Act
        client.TryGetTopicForProperty(property.Reference, property);
        client.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal(2, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenLookupInsertsWhileSubjectIsDetaching_ThenTheClientTopicCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);

        // Act
        var (property, topicSeenDuringDetach) = DetachChildWhileInserting(
            subject, p => client.TryGetTopicForProperty(p.Reference, p).Topic);
        var after = client.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal("child/value", topicSeenDuringDetach);
        Assert.Null(after.Topic);
        Assert.Equal(2, mapper.MappingLookupCount);
    }

    [Fact]
    public async Task WhenLookupInsertsWhileSubjectIsDetaching_ThenTheClientPropertyCacheDoesNotRetainIt()
    {
        // Arrange
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var client = CreateClient(subject, mapper);

        // Act
        var (_, resolvedDuringDetach) = DetachChildWhileInserting(
            subject, _ => SyncResult(client.TryGetPropertyForTopicAsync("child/value"))?.Name);
        var after = await client.TryGetPropertyForTopicAsync("child/value");

        // Assert
        Assert.Equal(nameof(MqttCacheTestChild.Value), resolvedDuringDetach);
        Assert.Null(after);
        Assert.Equal(2, mapper.PropertyLookupCount);
    }

    [Fact]
    public async Task WhenLookupInsertsWhileSubjectIsDetaching_ThenTheServerTopicCacheDoesNotRetainIt()
    {
        // Arrange: the server is never started, so its own eviction scan is not subscribed and
        // the insert-time guard is the only thing that can drop the entry again.
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);

        // Act
        var (property, topicSeenDuringDetach) = DetachChildWhileInserting(
            subject, p => server.TryGetTopicForProperty(p.Reference, p).Topic);
        var after = server.TryGetTopicForProperty(property.Reference, property);

        // Assert
        Assert.Equal("child/value", topicSeenDuringDetach);
        Assert.Null(after.Topic);
    }

    [Fact]
    public async Task WhenLookupInsertsWhileSubjectIsDetaching_ThenTheServerPathCacheDoesNotRetainIt()
    {
        // Arrange: the server is never started, so its own eviction scan is not subscribed and
        // the insert-time guard is the only thing that can drop the entry again.
        var mapper = new CountingMapper(CreateMapper());
        var subject = CreateRootSubject();
        await using var server = CreateServer(subject, mapper);

        // Act
        var (_, resolvedDuringDetach) = DetachChildWhileInserting(
            subject, _ => SyncResult(server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None))?.Name);
        var after = await server.TryGetPropertyForTopicAsync("child/value", CancellationToken.None);

        // Assert
        Assert.Equal(nameof(MqttCacheTestChild.Value), resolvedDuringDetach);
        Assert.Null(after);
    }

    private static MqttCacheTestRoot CreateRootSubject()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new MqttCacheTestRoot(context);
    }

    /// <summary>
    /// Attaches a child, captures the registration of one of its properties, then removes the child
    /// again. The returned registration is deliberately stale: the caches must refuse it.
    /// </summary>
    private static RegisteredSubjectProperty DetachChildAndReturnStaleProperty(MqttCacheTestRoot root)
    {
        root.Child = new MqttCacheTestChild();
        var property = root.Child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        root.Child = null;

        Assert.Null(property.Subject.TryGetRegisteredSubject());
        return property;
    }

    /// <summary>
    /// Attaches a child, then detaches it while <paramref name="insert"/> runs from a SubjectDetaching
    /// subscriber added after the connector's own. That is the deterministic stand-in for a publish or
    /// an inbound message whose lookup lands between the connector's eviction scan and the registry
    /// deregistration that follows it. Returns the child's now stale registration together with what
    /// the insert observed, which proves the registry was still intact at that point.
    /// </summary>
    private static (RegisteredSubjectProperty Property, string? Observed) DetachChildWhileInserting(
        MqttCacheTestRoot root,
        Func<RegisteredSubjectProperty, string?> insert)
    {
        var child = new MqttCacheTestChild();
        root.Child = child;

        var property = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(MqttCacheTestChild.Value))!;
        var lifecycle = ((IInterceptorSubject)root).Context.TryGetLifecycleInterceptor()!;

        string? observed = null;

        void Handler(SubjectLifecycleChange change)
        {
            if (ReferenceEquals(change.Subject, child))
            {
                observed = insert(property);
            }
        }

        lifecycle.SubjectDetaching += Handler;
        try
        {
            root.Child = null;
        }
        finally
        {
            lifecycle.SubjectDetaching -= Handler;
        }

        Assert.Null(child.TryGetRegisteredSubject());
        return (property, observed);
    }

    private static T SyncResult<T>(ValueTask<T> task)
    {
        Assert.True(task.IsCompleted, "The mapper is expected to resolve synchronously.");
        return task.Result;
    }

    private static MqttSubjectServer CreateServer(
        IInterceptorSubject subject,
        IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> mapper)
    {
        return new MqttSubjectServer(
            subject,
            new MqttServerConfiguration { Mapper = mapper },
            NullLogger<MqttSubjectServer>.Instance);
    }

    private static MqttSubjectClientSource CreateClient(
        IInterceptorSubject subject,
        IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> mapper)
    {
        return new MqttSubjectClientSource(
            subject,
            new MqttClientConfiguration { BrokerHost = "127.0.0.1", Mapper = mapper },
            NullLogger<MqttSubjectClientSource>.Instance);
    }

    private static MqttCompositeMapper CreateMapper() => new(
        new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
        new MqttAttributeMapper("mqtt"));

    /// <summary>
    /// Counts how often the caches fall through to the mapper, which is the only externally visible
    /// signal that a lookup was not served from cache.
    /// </summary>
    private sealed class CountingMapper : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>
    {
        private readonly IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> _inner;

        private int _mappingLookupCount;
        private int _propertyLookupCount;

        public CountingMapper(IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> inner)
        {
            _inner = inner;
        }

        public int MappingLookupCount => Volatile.Read(ref _mappingLookupCount);

        public int PropertyLookupCount => Volatile.Read(ref _propertyLookupCount);

        public bool TryGetMapping(
            RegisteredSubjectProperty property,
            IInterceptorSubject rootSubject,
            [NotNullWhen(true)] out MqttPropertyMapping? mapping)
        {
            Interlocked.Increment(ref _mappingLookupCount);
            return _inner.TryGetMapping(property, rootSubject, out mapping);
        }

        public ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
            MqttLookupKey key,
            RegisteredSubject subject,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _propertyLookupCount);
            return _inner.TryGetPropertyAsync(key, subject, cancellationToken);
        }
    }
}

[InterceptorSubject]
public partial class MqttCacheTestRoot
{
    [Path("mqtt", "name")]
    public partial string Name { get; set; }

    [Path("mqtt", "child")]
    public partial MqttCacheTestChild? Child { get; set; }

    public MqttCacheTestRoot()
    {
        Name = string.Empty;
    }
}

[InterceptorSubject]
public partial class MqttCacheTestChild
{
    [Path("mqtt", "value")]
    public partial string Value { get; set; }

    public MqttCacheTestChild()
    {
        Value = string.Empty;
    }
}
