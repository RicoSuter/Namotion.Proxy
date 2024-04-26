﻿using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System.Reactive.Linq;

using Namotion.Proxy.Sources.Abstractions;
using Namotion.Proxy.Registry.Abstractions;

using Opc.Ua.Server;
using Opc.Ua;
using Opc.Ua.Configuration;

using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Proxy.OpcUa.Server;

internal class OpcUaServerTrackableSource<TProxy> : BackgroundService, IProxySource, IDisposable
    where TProxy : IProxy
{
    internal const string OpcVariableKey = "OpcVariable";

    private readonly IProxyContext _context;
    private readonly TProxy _proxy;
    private readonly ILogger _logger;
    private readonly string? _rootName;
    private ProxyOpcUaServer<TProxy>? _server;

    internal ISourcePathProvider SourcePathProvider { get; }

    public OpcUaServerTrackableSource(
        TProxy proxy,
        ISourcePathProvider sourcePathProvider,
        ILogger<OpcUaServerTrackableSource<TProxy>> logger,
        string? rootName)
    {
        _context = proxy.Context ??
            throw new InvalidOperationException($"Context is not set on {nameof(TProxy)}.");

        _proxy = proxy;
        _logger = logger;
        _rootName = rootName;

        SourcePathProvider = sourcePathProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var stream = typeof(OpcUaProxyExtensions).Assembly
                .GetManifestResourceStream("Namotion.Proxy.OpcUa.MyOpcUaServer.Config.xml");

            var application = new ApplicationInstance
            {
                ApplicationName = "MyOpcUaServer",
                ApplicationType = ApplicationType.Server,
                ApplicationConfiguration = await ApplicationConfiguration.Load(
                    stream, ApplicationType.Server, typeof(ApplicationConfiguration), false)
            };

            try
            {
                _server = new ProxyOpcUaServer<TProxy>(_proxy, this, _rootName);

                await application.CheckApplicationInstanceCertificate(true, CertificateFactory.DefaultKeySize);
                await application.Start(_server);

                await Task.Delay(-1, stoppingToken);
            }
            catch (Exception ex)
            {
                application.Stop();

                if (ex is not TaskCanceledException)
                {
                    _logger.LogError(ex, "Failed to start OPC UA server.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }
    }

    public Task<IDisposable?> InitializeAsync(IEnumerable<ProxyPropertyPathReference> properties, Action<ProxyPropertyPathReference> propertyUpdateAction, CancellationToken cancellationToken)
    {
        return Task.FromResult<IDisposable?>(null);
    }

    public Task<IEnumerable<ProxyPropertyPathReference>> ReadAsync(IEnumerable<ProxyPropertyPathReference> properties, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<ProxyPropertyPathReference>>(properties
            .Where(p => p.Property.TryGetPropertyData(OpcUaServerTrackableSource<TProxy>.OpcVariableKey, out var _))
            .Select(property => (property, node: property.Property.GetPropertyData(OpcUaServerTrackableSource<TProxy>.OpcVariableKey) as BaseDataVariableState))
            .Where(p => p.node is not null)
            .Select(p => new ProxyPropertyPathReference(p.property.Property, p.property.Path,
                p.property.Property.Metadata.Type == typeof(decimal) ? Convert.ToDecimal(p.node!.Value) : p.node!.Value))
            .ToList());
    }

    public Task WriteAsync(IEnumerable<ProxyPropertyPathReference> propertyChanges, CancellationToken cancellationToken)
    {
        foreach (var property in propertyChanges
            .Where(p => p.Property.TryGetPropertyData(OpcUaServerTrackableSource<TProxy>.OpcVariableKey, out var _)))
        {
            var node = property.Property.GetPropertyData(OpcUaServerTrackableSource<TProxy>.OpcVariableKey) as BaseDataVariableState;
            if (node is not null)
            {
                var actualValue = property.Value;
                if (actualValue is decimal)
                {
                    actualValue = Convert.ToDouble(actualValue);
                }

                node.Value = actualValue;
                node.ClearChangeMasks(_server?.CurrentInstance.DefaultSystemContext, false);
            }
        }

        return Task.CompletedTask;
    }

    public string? TryGetSourcePath(ProxyPropertyReference property)
    {
        return SourcePathProvider.TryGetSourcePath(property);
    }
}

internal class ProxyOpcUaServer<TProxy> : StandardServer
    where TProxy : IProxy
{
    public ProxyOpcUaServer(TProxy proxy, OpcUaServerTrackableSource<TProxy> source, string? rootName)
    {
        AddNodeManager(new CustomNodeManagerFactory<TProxy>(proxy, source, rootName));
    }
}

internal class CustomNodeManagerFactory<TProxy> : INodeManagerFactory
    where TProxy : IProxy
{
    private readonly TProxy _proxy;
    private readonly OpcUaServerTrackableSource<TProxy> _source;
    private readonly string? _rootName;

    public StringCollection NamespacesUris => new StringCollection(new[] { "https://foobar/" });

    public CustomNodeManagerFactory(TProxy proxy, OpcUaServerTrackableSource<TProxy> source, string? rootName)
    {
        _proxy = proxy;
        _source = source;
        _rootName = rootName;
    }

    public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
    {
        return new CustomNodeManager<TProxy>(_proxy, _source, server, configuration, _rootName);
    }
}

internal class CustomNodeManager<TProxy> : CustomNodeManager2
    where TProxy : IProxy
{
    private const string PathDelimiter = ".";

    private readonly TProxy _proxy;
    private readonly IProxyRegistry _registry;
    private readonly OpcUaServerTrackableSource<TProxy> _source;
    private readonly string? _rootName;

    public CustomNodeManager(
        TProxy proxy, 
        OpcUaServerTrackableSource<TProxy> source, 
        IServerInternal server, 
        ApplicationConfiguration configuration, 
        string? rootName) :
       base(server, configuration, new string[] { "https://foobar/" })
    {
        _proxy = proxy;
        _registry = proxy.Context?.GetHandler<IProxyRegistry>() ?? throw new ArgumentException($"Registry could not be found.");
        _source = source;
        _rootName = rootName;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        var metadata = _registry.KnownProxies[_proxy];
        if (metadata is not null)
        {
            if (_rootName is not null)
            {
                var node = CreateFolder(ObjectIds.ObjectsFolder, _rootName, _rootName, NamespaceIndex);
                CreateObjectNode(metadata, node.NodeId, _rootName + PathDelimiter);
            }
            else
            {
                CreateObjectNode(metadata, ObjectIds.ObjectsFolder, string.Empty);
            }
        }
    }

    private void CreateObjectNode(RegisteredProxy metadata, NodeId parentNode, string prefix)
    {
        foreach (var property in metadata.Properties)
        {
            var propertyName = _source.SourcePathProvider.TryGetSourcePropertyName(property.Value.Property)!;
            if (propertyName is null)
            {
                continue;
            }

            var children = property.Value.Children;
            if (children.Count > 1)
            {
                var innerPrefix = prefix + propertyName;
                var propertyNode = CreateFolder(parentNode, prefix + propertyName, propertyName, NamespaceIndex);

                foreach (var child in property.Value.Children)
                {
                    CreateChildObject(propertyNode.NodeId, propertyName, child, innerPrefix);
                }
            }
            else if (children.Count == 1)
            {
                var child = children.Single();
                CreateChildObject(parentNode, propertyName, child, prefix + propertyName);
            }
            else
            {
                var sourcePath = _source.TryGetSourcePath(property.Value.Property);
                if (sourcePath is not null)
                {
                    var value = property.Value.GetValue();
                    var type = property.Value.Type;

                    if (type == typeof(decimal))
                    {
                        type = typeof(double);
                        value = Convert.ToDouble(value);
                    }

                    var path = prefix + propertyName;
                    var variable = CreateVariable(parentNode, path, propertyName, NamespaceIndex, TypeInfo.Construct(type), 1);

                    variable.Value = value;
                    property.Value.Property.SetPropertyData(OpcUaServerTrackableSource<TProxy>.OpcVariableKey, variable);
                }
            }
        }
    }

    private void CreateChildObject(NodeId parentNode, string propertyName, ProxyPropertyChild child, string prefix)
    {
        var index = child.Index is not null ? $"[{child.Index}]" : string.Empty;
        var path = prefix + PathDelimiter + propertyName + index;

        var objectNode = CreateFolder(parentNode, path, propertyName + index, NamespaceIndex);
        CreateObjectNode(_registry.KnownProxies[child.Proxy], objectNode.NodeId, path + PathDelimiter);
    }

    private FolderState CreateFolder(NodeId parentNodeId, string path, string name, ushort namespaceIndex)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var folder = new FolderState(parentNode)
        {
            NodeId = new NodeId(path, namespaceIndex),
            BrowseName = new QualifiedName(name, namespaceIndex),
            DisplayName = new LocalizedText(name),
            TypeDefinitionId = ObjectTypeIds.FolderType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None
        };

        if (parentNode != null)
        {
            parentNode.AddChild(folder);
        }

        AddPredefinedNode(SystemContext, folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(NodeId parentNodeId, string path, string name, ushort namespaceIndex, TypeInfo dataType, int valueRank)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var variable = new BaseDataVariableState(parentNode)
        {
            NodeId = new NodeId(path, namespaceIndex),

            SymbolicName = name,
            BrowseName = new QualifiedName(name, namespaceIndex),
            DisplayName = new LocalizedText(name),

            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            DataType = TypeInfo.GetDataTypeId(dataType),
            ValueRank = valueRank,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };

        if (parentNode != null)
        {
            parentNode.AddChild(variable);
        }

        AddPredefinedNode(SystemContext, variable);
        return variable;
    }
}