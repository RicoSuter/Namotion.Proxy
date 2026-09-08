using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Sensors;
using HueApi.Models;
using HueApi.Models.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Philips Hue motion sensor device with presence, temperature, light level, and battery.
/// </summary>
[InterceptorSubject]
public partial class HueMotionDevice : HueDevice,
    IPresenceSensor,
    IBatteryState,
    ILightSensor,
    ITemperatureSensor
{
    internal partial MotionResource MotionResource { get; set; }

    internal partial DevicePower? DevicePowerResource { get; set; }

    internal partial TemperatureResource? TemperatureResource { get; set; }

    internal partial LightLevelResource? LightLevelResource { get; set; }

    [Derived]
    public override string? IconName => "DirectionsRun";

    [Derived]
    [State]
    public bool? IsPresent => MotionResource?.Motion?.MotionReport?.Motion;

    [Derived]
    [State]
    public decimal? BatteryLevel => DevicePowerResource?.PowerState?.BatteryLevel / 100m;

    [Derived]
    [State]
    public decimal? Temperature =>
        TemperatureResource?.Temperature?.TemperatureValid == true
            ? TemperatureResource?.Temperature?.TemperatureReport?.Temperature
            : null;

    [Derived]
    [State]
    public decimal? Illuminance =>
        LightLevelResource?.Enabled == true
            ? (decimal?)LightLevelResource?.Light?.LightLevelReport?.LuxLevel
            : null;

    public HueMotionDevice(
        Device device,
        ZigbeeConnectivity? zigbeeConnectivity,
        DevicePower? devicePower,
        TemperatureResource? temperature,
        LightLevelResource? lightLevel,
        MotionResource motion,
        HueBridge bridge)
        : base(device, zigbeeConnectivity, bridge)
    {
        DevicePowerResource = devicePower;
        TemperatureResource = temperature;
        LightLevelResource = lightLevel;
        MotionResource = motion;
    }

    internal HueMotionDevice Update(
        Device device,
        ZigbeeConnectivity? zigbeeConnectivity,
        DevicePower? devicePower,
        TemperatureResource? temperature,
        LightLevelResource? lightLevel,
        MotionResource motion)
    {
        Update(device, zigbeeConnectivity);
        DevicePowerResource = devicePower;
        TemperatureResource = temperature;
        LightLevelResource = lightLevel;
        MotionResource = motion;
        return this;
    }
}
