using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using HueApi.Models;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Base device subject for Philips Hue devices.
/// Updated by the bridge via polling and event stream.
/// </summary>
[InterceptorSubject]
public partial class HueDevice :
    IConnectionState,
    IMonitoredService,
    IUnknownDevice,
    IDeviceInfo,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    internal partial Device Device { get; private set; }

    internal partial ZigbeeConnectivity? ZigbeeConnectivity { get; set; }

    public HueBridge Bridge { get; }

    public Guid ResourceId => Device.Id;

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public virtual string? Title => Device?.Metadata?.Name ?? "n/a";

    [Derived]
    public virtual string? IconName => "QuestionMark";

    [Derived]
    public virtual string? IconColor => null;

    [Derived]
    public bool IsConnected =>
        ZigbeeConnectivity == null ||
        ZigbeeConnectivity.Status == ConnectivityStatus.connected;

    [Derived]
    public ServiceStatus Status =>
        IsConnected ? ServiceStatus.Running : ServiceStatus.Error;

    [Derived]
    public string? StatusMessage =>
        IsConnected ? null : "Device disconnected";

    [Derived]
    [State]
    public bool? IsCertified => Device?.ProductData?.Certified;

    [Derived]
    [State]
    public string? ProductName => Device?.ProductData?.ProductName;

    [Derived]
    [State]
    public string? HardwarePlatformType => Device?.ProductData?.HardwarePlatformType;

    [Derived]
    [State]
    public string? SoftwareVersion => Device?.ProductData?.SoftwareVersion;

    // IDeviceInfo

    [Derived]
    [State]
    public string? Manufacturer => Device?.ProductData?.ManufacturerName;

    [Derived]
    [State]
    public string? Model => Device?.ProductData?.ModelId;

    [Derived]
    [State]
    public string? ProductCode => Device?.ProductData?.ProductName;

    [Derived]
    [State]
    public string? SerialNumber => null;

    [Derived]
    [State]
    public string? HardwareRevision => null;

    public HueDevice(Device device, ZigbeeConnectivity? zigbeeConnectivity, HueBridge bridge)
    {
        Bridge = bridge;
        Device = device;
        ZigbeeConnectivity = zigbeeConnectivity;
        LastUpdated = DateTimeOffset.Now;
    }

    internal virtual HueDevice Update(Device device, ZigbeeConnectivity? zigbeeConnectivity)
    {
        Device = device;
        ZigbeeConnectivity = zigbeeConnectivity;
        LastUpdated = DateTimeOffset.Now;
        return this;
    }
}
