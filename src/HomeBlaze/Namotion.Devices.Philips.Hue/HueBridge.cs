using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using HueApi;
using HueApi.BridgeLocator;
using HueApi.Models;
using HueApi.Models.Responses;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Philips Hue Bridge for controlling lights, sensors, and buttons.
/// Discovers and manages child devices via the Hue API.
/// </summary>
[Category("Devices")]
[Description("Philips Hue Bridge for controlling lights, sensors, and buttons")]
[InterceptorSubject]
public partial class HueBridge : BackgroundService,
    IConfigurable,
    IMonitoredService,
    IHubDevice,
    IConnectionState,
    IPowerSensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    /// <summary>
    /// Delay in milliseconds after setting brightness on an off light.
    /// The Hue bridge requires turning a light on before changing brightness.
    /// This delay gives the bridge time to process the change before turning the light off again,
    /// so the brightness is remembered for the next time the light is turned on.
    /// </summary>
    internal const int SetBrightnessWhileOffDelayMs = 3000;

    private const int MaxConsecutivePollFailures = 3;

    private readonly ILogger<HueBridge> _logger;
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);
    private readonly Lock _clientLock = new();

    // Set under _clientLock once the bridge is disposed, so no operation can build a client that
    // nothing is left to release.
    private bool _disposed;

    private LocatedBridge? _bridge;
    private HttpClient? _httpClient;
    private LocalHueApi? _client;

    internal HueDevice[] Devices { get; set; }

    [Configuration]
    public partial string? BridgeId { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? AppKey { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    [State]
    public partial bool IsConnected { get; internal set; }

    [State]
    public partial Dictionary<string, HueGroup> Rooms { get; internal set; }

    [State]
    public partial Dictionary<string, HueGroup> Zones { get; internal set; }

    [State]
    public partial Dictionary<string, HueLightbulb> Lights { get; internal set; }

    [State]
    public partial Dictionary<string, HueButtonDevice> ButtonDevices { get; internal set; }

    [State]
    public partial Dictionary<string, HueMotionDevice> MotionSensors { get; internal set; }

    [State]
    public partial Dictionary<string, HueDevice> OtherDevices { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [Derived]
    public string? Title => "Hue Bridge (" + (_bridge?.IpAddress ?? "?") + ")";

    [Derived]
    public string IconName => IsConnected ? "Hub" : "HubOutlined";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    [Derived]
    public decimal? Power => IsConnected ? 3.0m : null;

    [Derived]
    public decimal? EnergyConsumed => null;

    [Derived]
    [State(Unit = StateUnit.Watt)]
    public decimal? TotalPower =>
        IsConnected ? Power + Lights.Values.Sum(light => light.Power ?? 0m) : null;

    public HueBridge(ILogger<HueBridge> logger)
    {
        _logger = logger;

        // The event stream carries state changes; this poll only reconciles the device set, so it is
        // deliberately slow. This is the interval the loop hardcoded while the setting was ignored.
        PollingInterval = TimeSpan.FromSeconds(60);
        RetryInterval = TimeSpan.FromSeconds(30);

        Lights = new();
        MotionSensors = new();
        ButtonDevices = new();
        Rooms = new();
        Zones = new();
        Devices = [];
        OtherDevices = new();
    }

    /// <summary>
    /// Gets or creates the cached authenticated Hue API client.
    /// </summary>
    internal LocalHueApi GetOrCreateClient()
    {
        lock (_clientLock)
        {
            // Disposal is reported as such rather than as a disconnection, even though Dispose clears
            // IsConnected on its way past: "the bridge is gone" and "the bridge is not up yet" are
            // different answers to a caller deciding whether to retry.
            ObjectDisposedException.ThrowIf(_disposed, this);

            // An operation may only use a connection the loop is maintaining. Building its own would
            // reach the bridge and physically succeed, but nothing would be streaming or polling it,
            // so every derived value stayed at its pre-command reading for as long as the loop was
            // failing to connect. Refusing is honest, and the caller surfaces it.
            if (!IsConnected)
            {
                throw new InvalidOperationException(
                    "Bridge is not connected. " + (StatusMessage ?? "Waiting for the connection to be established."));
            }

            return ConnectClient();
        }
    }

    /// <summary>
    /// The connection loop's own entry point. It is what establishes the connection, so unlike an
    /// operation it is not subject to the connected check.
    /// </summary>
    private LocalHueApi ConnectClient()
    {
        lock (_clientLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_client is not null)
            {
                return _client;
            }

            if (AppKey == null || _bridge == null)
            {
                throw new InvalidOperationException("Bridge is not configured or not discovered.");
            }

            // The SDK passes no timeout, leaving HttpClient's 100s default. A poll is 11 sequential
            // requests, so an unresponsive bridge would otherwise stall one for nearly twenty minutes.
            var httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            try
            {
                var client = new LocalHueApi(_bridge.IpAddress, AppKey, httpClient);
                _httpClient = httpClient;
                _client = client;
                return client;
            }
            catch
            {
                httpClient.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Stops and releases a failed or shutting-down client. A subsequent connection attempt creates
    /// a fresh HttpClient because HueApi configures it during construction and .NET forbids changing
    /// those properties after the first request.
    /// </summary>
    internal void ResetClient(LocalHueApi client)
    {
        HttpClient? httpClient;
        lock (_clientLock)
        {
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            _client = null;
            httpClient = _httpClient;
            _httpClient = null;
        }

        try
        {
            client.StopEventStream();
        }
        finally
        {
            httpClient?.Dispose();
        }
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_configChangedSignal.CurrentCount == 0)
        {
            _configChangedSignal.Release();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            LocalHueApi? client = null;

            try
            {
                Status = ServiceStatus.Starting;
                StatusMessage = null;

                if (string.IsNullOrEmpty(BridgeId) || string.IsNullOrEmpty(AppKey))
                {
                    Status = ServiceStatus.Error;
                    StatusMessage = "Bridge not configured. Set BridgeId and AppKey.";
                    try
                    {
                        await _configChangedSignal.WaitAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                // Discovery
                var bridge = await DiscoverBridgeAsync(stoppingToken);
                if (bridge == null)
                {
                    StatusMessage = "Bridge not found on network";
                    try
                    {
                        await Task.Delay(RetryInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                _bridge = bridge;

                client = ConnectClient();

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                Status = ServiceStatus.Running;
                IsConnected = true;
                StatusMessage = null;

                // Initial poll
                await PollDevicesAsync(client, linkedCts.Token);

                // Run event stream + periodic poll in parallel
                var eventStreamTask = RunEventStreamAsync(client, linkedCts.Token);
                var pollingTask = RunPollingLoopAsync(client, linkedCts.Token);

                // Either one finishing means the connection is no longer whole, so the other is
                // cancelled and both are observed. Awaiting them together instead let a faulted event
                // stream sit unobserved behind the polling loop, which never returns: the stream stayed
                // dead, the reconnect below never ran, and the bridge went on reporting Running while
                // state changes only arrived at the poll interval.
                await Task.WhenAny(eventStreamTask, pollingTask);
                await linkedCts.CancelAsync();
                await Task.WhenAll(eventStreamTask, pollingTask);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Hue Bridge connection failed.");
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;

                try
                {
                    await Task.Delay(RetryInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                IsConnected = false;

                if (client is not null)
                {
                    ResetClient(client);
                }
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Finds the configured bridge, keeping a failure local to the locator that caused it: the SDK's
    /// aggregate helpers let one locator's exception abandon the whole discovery.
    /// </summary>
    private async Task<LocatedBridge?> DiscoverBridgeAsync(CancellationToken cancellationToken)
    {
        IBridgeLocator[] locators =
        [
            new HttpBridgeLocator(),
            new MdnsBridgeLocator(),
            new SsdpBridgeLocator(),
            new LocalNetworkScanBridgeLocator()
        ];

        foreach (var locator in locators)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The network scan probes the whole subnet, so it gets the longer budget and goes last.
            var timeout = locator is LocalNetworkScanBridgeLocator
                ? TimeSpan.FromSeconds(30)
                : TimeSpan.FromSeconds(5);

            using var locatorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            locatorCancellation.CancelAfter(timeout);

            LocatedBridge? bridge;
            try
            {
                // Enumerated inside the try: the interface returns IEnumerable, so a lazy locator
                // would otherwise throw past the catch that exists to contain it.
                var bridges = await locator.LocateBridgesAsync(locatorCancellation.Token);
                bridge = bridges.FirstOrDefault(locatedBridge => locatedBridge.BridgeId == BridgeId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Hue locator {Locator} failed.", locator.GetType().Name);
                continue;
            }

            if (bridge is not null)
            {
                return bridge;
            }
        }

        return null;
    }

    private async Task RunEventStreamAsync(LocalHueApi client, CancellationToken cancellationToken)
    {
        client.OnEventStreamMessage += OnEventStreamMessage;
        try
        {
            await client.StartEventStream(cancellationToken: cancellationToken);
        }
        finally
        {
            client.OnEventStreamMessage -= OnEventStreamMessage;
        }
    }

    // A zero interval would spin the loop and a negative one is rejected outright, so fall back to the
    // default rather than trusting a hand-edited configuration file.
    private TimeSpan EffectivePollingInterval =>
        PollingInterval > TimeSpan.Zero ? PollingInterval : TimeSpan.FromSeconds(60);

    private async Task RunPollingLoopAsync(LocalHueApi client, CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(EffectivePollingInterval, cancellationToken);

            try
            {
                await PollDevicesAsync(client, cancellationToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutivePollFailures)
                {
                    throw;
                }

                // A teardown takes the event stream with it, and that stream carries the state
                // changes; this poll only reconciles the device set.
                _logger.LogWarning(
                    exception,
                    "Hue poll failed ({FailureCount} in a row, connection dropped after {MaxFailureCount}). " +
                    "Keeping the connection and reconciling at the next interval.",
                    consecutiveFailures, MaxConsecutivePollFailures);
            }
        }
    }

    private async Task PollDevicesAsync(LocalHueApi client, CancellationToken cancellationToken)
    {
        // Issued one at a time: 11 at once trips the bridge's rate limiter, which answers 429 with an
        // HTML error page that the SDK then fails to parse as JSON.
        async Task<HueResponse<T>> PollAsync<T>(Func<Task<HueResponse<T>>> request, string resource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (await request()).ThrowOnError(resource);
        }

        var zigbeeConnectivities = await PollAsync(() => client.ZigbeeConnectivity.GetAllAsync(), "zigbee_connectivity");
        var devicePowers = await PollAsync(() => client.DevicePower.GetAllAsync(), "device_power");
        var devices = await PollAsync(() => client.Device.GetAllAsync(), "device");
        var rooms = await PollAsync(() => client.Room.GetAllAsync(), "room");
        var zones = await PollAsync(() => client.Zone.GetAllAsync(), "zone");
        var lights = await PollAsync(() => client.Light.GetAllAsync(), "light");
        var buttons = await PollAsync(() => client.Button.GetAllAsync(), "button");
        var motions = await PollAsync(() => client.Motion.GetAllAsync(), "motion");
        var groupedLights = await PollAsync(() => client.GroupedLight.GetAllAsync(), "grouped_light");
        var temperatures = await PollAsync(() => client.Temperature.GetAllAsync(), "temperature");
        var lightLevels = await PollAsync(() => client.LightLevel.GetAllAsync(), "light_level");

        var existingDevices = Devices;
        var allDevices = devices.Data
            .Select(device =>
            {
                var services = device.Services?.Select(service => service.Rid).ToArray() ?? [];
                var zigbeeConnectivity = zigbeeConnectivities.Data.SingleOrDefault(item => services.Contains(item.Id));
                var devicePower = devicePowers.Data.SingleOrDefault(item => services.Contains(item.Id));

                // Try to find existing device by ResourceId
                var existingDevice = existingDevices.SingleOrDefault(existing => existing.ResourceId == device.Id);

                // Light device
                var lightService = device.Services?.SingleOrDefault(service => service.Rtype == "light");
                if (lightService is not null)
                {
                    var light = lights.Data.SingleOrDefault(item => item.Id == lightService.Rid);
                    if (light is not null)
                    {
                        if (existingDevice is HueLightbulb existingLightbulb)
                        {
                            return existingLightbulb.Update(device, zigbeeConnectivity, light);
                        }

                        return new HueLightbulb(device, zigbeeConnectivity, light, this);
                    }
                }

                // Motion sensor device
                var motionService = device.Services?.SingleOrDefault(service => service.Rtype == "motion");
                if (motionService is not null)
                {
                    var motion = motions.Data.SingleOrDefault(item => item.Id == motionService.Rid);
                    if (motion is not null)
                    {
                        var temperature = temperatures.Data.SingleOrDefault(item => services.Contains(item.Id));
                        var lightLevel = lightLevels.Data.SingleOrDefault(item => services.Contains(item.Id));

                        if (existingDevice is HueMotionDevice existingMotion)
                        {
                            return existingMotion.Update(device, zigbeeConnectivity, devicePower, temperature, lightLevel, motion);
                        }

                        return new HueMotionDevice(device, zigbeeConnectivity, devicePower, temperature, lightLevel, motion, this);
                    }
                }

                // Button device
                var buttonServices = device.Services?
                    .Where(service => service.Rtype == "button")
                    .Select(service => buttons.Data.SingleOrDefault(button => button.Id == service.Rid))
                    .Where(button => button is not null)
                    .ToArray();

                if (buttonServices is not null && buttonServices.Length > 0)
                {
                    if (existingDevice is HueButtonDevice existingButtonDevice)
                    {
                        return existingButtonDevice.Update(device, zigbeeConnectivity, devicePower, buttonServices!);
                    }

                    return new HueButtonDevice(device, zigbeeConnectivity, devicePower, buttonServices!, this);
                }

                // Generic device
                if (existingDevice is not null)
                {
                    return existingDevice.Update(device, zigbeeConnectivity);
                }

                return new HueDevice(device, zigbeeConnectivity, this);
            })
            .OrderBy(device => device.Title)
            .ToArray();

        Devices = allDevices;

        var newLights = allDevices.OfType<HueLightbulb>().ToDictionary(d => d.ResourceId.ToString());
        if (!DictionaryEquals(Lights, newLights))
            Lights = newLights;

        var newMotionSensors = allDevices.OfType<HueMotionDevice>().ToDictionary(d => d.ResourceId.ToString());
        if (!DictionaryEquals(MotionSensors, newMotionSensors))
            MotionSensors = newMotionSensors;

        var newButtonDevices = allDevices.OfType<HueButtonDevice>().ToDictionary(d => d.ResourceId.ToString());
        if (!DictionaryEquals(ButtonDevices, newButtonDevices))
            ButtonDevices = newButtonDevices;

        var newOtherDevices = allDevices
            .Except(newLights.Values)
            .Except(newMotionSensors.Values)
            .Except(newButtonDevices.Values)
            .ToDictionary(d => d.ResourceId.ToString());

        if (!DictionaryEquals(OtherDevices, newOtherDevices))
            OtherDevices = newOtherDevices;

        // Rooms
        var existingRooms = Rooms.Values;
        var newRooms = rooms.Data
            .Select(room =>
            {
                var roomServices = room.Services?.Select(service => service.Rid).ToArray() ?? [];
                var groupedLight = groupedLights.Data.SingleOrDefault(grouped => roomServices.Contains(grouped.Id));
                var roomLights = allDevices
                    .OfType<HueLightbulb>()
                    .Where(light => room.Children.Any(child => child.Rid == light.ResourceId || child.Rid == light.ReferenceId))
                    .ToArray();

                var existingRoom = existingRooms.SingleOrDefault(existing => existing.ResourceId == room.Id);
                if (existingRoom is not null)
                {
                    return existingRoom.Update(room, groupedLight, roomLights);
                }

                return new HueGroup(room, groupedLight, roomLights, this);
            })
            .ToDictionary(room => room.ResourceId.ToString());

        if (!DictionaryEquals(Rooms, newRooms))
            Rooms = newRooms;

        // Zones
        var existingZones = Zones.Values;
        var newZones = zones.Data
            .Select(zone =>
            {
                var zoneServices = zone.Services?.Select(service => service.Rid).ToArray() ?? [];
                var groupedLight = groupedLights.Data.SingleOrDefault(grouped => zoneServices.Contains(grouped.Id));
                var zoneLights = allDevices
                    .OfType<HueLightbulb>()
                    .Where(light => zone.Children.Any(child => child.Rid == light.ResourceId || child.Rid == light.ReferenceId))
                    .ToArray();

                var existingZone = existingZones.SingleOrDefault(existing => existing.ResourceId == zone.Id);
                if (existingZone is not null)
                {
                    return existingZone.Update(zone, groupedLight, zoneLights);
                }

                return new HueGroup(zone, groupedLight, zoneLights, this);
            })
            .ToDictionary(zone => zone.ResourceId.ToString());

        if (!DictionaryEquals(Zones, newZones))
            Zones = newZones;

        LastUpdated = DateTimeOffset.Now;
    }

    private void OnEventStreamMessage(string bridgeIp, List<EventStreamResponse> events)
    {
        foreach (var eventResponse in events)
        {
            foreach (var data in eventResponse.Data)
            {
                if (data.Type == "button")
                {
                    var buttonDevice = Devices
                        .OfType<HueButtonDevice>()
                        .SingleOrDefault(device => device.ResourceId == data.Owner?.Rid);

                    var button = buttonDevice?.Buttons.SingleOrDefault(existingButton => existingButton.ResourceId == data.Id);
                    if (button is not null)
                    {
                        button.ButtonResource = Merge(button.ButtonResource, data);
                        button.LastUpdated = DateTimeOffset.Now;
                        button.RefreshButtonState();
                    }
                }
                else if (data.Type == "light")
                {
                    var lightDevice = Devices
                        .OfType<HueLightbulb>()
                        .SingleOrDefault(device => device.ResourceId == data.Owner?.Rid);

                    if (lightDevice is not null)
                    {
                        lightDevice.LightResource = Merge(lightDevice.LightResource, data);
                        lightDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "grouped_light")
                {
                    var group = Rooms.Values
                        .Union(Zones.Values)
                        .SingleOrDefault(existingGroup => existingGroup.GroupedLight?.Id == data.Id);

                    if (group?.GroupedLight != null)
                    {
                        group.GroupedLight = Merge(group.GroupedLight, data);
                        group.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "motion")
                {
                    var motionDevice = Devices
                        .OfType<HueMotionDevice>()
                        .SingleOrDefault(device => device.MotionResource?.Id == data.Id);

                    if (motionDevice is not null)
                    {
                        motionDevice.MotionResource = Merge(motionDevice.MotionResource, data);
                        motionDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "temperature")
                {
                    var motionDevice = Devices
                        .OfType<HueMotionDevice>()
                        .SingleOrDefault(device => device.TemperatureResource?.Id == data.Id);

                    if (motionDevice is not null)
                    {
                        motionDevice.TemperatureResource = Merge(motionDevice.TemperatureResource, data);
                        motionDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "light_level")
                {
                    var motionDevice = Devices
                        .OfType<HueMotionDevice>()
                        .SingleOrDefault(device => device.LightLevelResource?.Id == data.Id);

                    if (motionDevice is not null)
                    {
                        motionDevice.LightLevelResource = Merge(motionDevice.LightLevelResource, data);
                        motionDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "device_power")
                {
                    var motionDevice = Devices
                        .OfType<HueMotionDevice>()
                        .SingleOrDefault(device => device.DevicePowerResource?.Id == data.Id);

                    if (motionDevice is not null)
                    {
                        motionDevice.DevicePowerResource = Merge(motionDevice.DevicePowerResource, data);
                        motionDevice.LastUpdated = DateTimeOffset.Now;
                    }

                    var buttonDevice = Devices
                        .OfType<HueButtonDevice>()
                        .SingleOrDefault(device => device.DevicePowerResource?.Id == data.Id);

                    if (buttonDevice is not null)
                    {
                        buttonDevice.DevicePowerResource = Merge(buttonDevice.DevicePowerResource, data);
                        buttonDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "zigbee_connectivity")
                {
                    var device = Devices
                        .SingleOrDefault(device => device.ZigbeeConnectivity?.Id == data.Id);

                    if (device is not null)
                    {
                        device.ZigbeeConnectivity = Merge(device.ZigbeeConnectivity, data);
                        device.LastUpdated = DateTimeOffset.Now;
                    }
                }
            }
        }
    }

    private static T Merge<T>(T currentResource, EventStreamData newPartialResource)
    {
        var currentNode = JsonNode.Parse(JsonSerializer.Serialize(currentResource))!.AsObject();
        var partialNode = JsonNode.Parse(JsonSerializer.Serialize(newPartialResource))!.AsObject();

        MergeObjects(currentNode, partialNode);

        return JsonSerializer.Deserialize<T>(currentNode.ToJsonString())!;
    }

    private static void MergeObjects(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is null)
                continue;

            if (value is JsonObject patchChild
                && target[key] is JsonObject targetChild)
            {
                MergeObjects(targetChild, patchChild);
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }

    private static bool DictionaryEquals<T>(Dictionary<string, T> existing, Dictionary<string, T> updated) where T : class
    {
        if (existing.Count != updated.Count)
            return false;

        foreach (var (key, value) in existing)
        {
            if (!updated.TryGetValue(key, out var updatedValue) || !ReferenceEquals(value, updatedValue))
                return false;
        }

        return true;
    }

    public override void Dispose()
    {
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        IsConnected = false;

        // Route through ResetClient so the event stream is stopped and the HttpClient is released as
        // a pair. Clearing _client alone left _httpClient non-null and alive, so a disposal racing an
        // in-flight operation let GetOrCreateClient build a replacement into abandoned fields.
        // The disposed flag is set under the same lock that reads the client, which is what makes the
        // pair a snapshot. Reading the client and then closing the door separately let an operation
        // racing the shutdown install a replacement in between, and nothing was left to release it.
        // Cancel first. base.Dispose() is what cancels the stopping token, and running it last let the
        // loop start a fresh iteration after the door was closed: it would spend the discovery budget
        // rediscovering the bridge, then fail on the disposed check and overwrite the Stopped status
        // written above with an Error one.
        base.Dispose();

        LocalHueApi? client;
        lock (_clientLock)
        {
            _disposed = true;
            client = _client;
        }

        if (client is not null)
        {
            ResetClient(client);
        }

        _configChangedSignal.Dispose();
    }
}
