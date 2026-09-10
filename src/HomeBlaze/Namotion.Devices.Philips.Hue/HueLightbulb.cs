using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Light;
using HomeBlaze.Abstractions.Sensors;
using HueApi.ColorConverters;
using HueApi.ColorConverters.Original.Extensions;
using HueApi.Models;
using HueApi.Models.Requests;
using Namotion.Interceptor.Attributes;
using static Namotion.Devices.Philips.Hue.HueCommandResult;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Philips Hue lightbulb with dimming, color, and color temperature support.
/// </summary>
[InterceptorSubject]
public partial class HueLightbulb : HueDevice,
    ILightbulb,
    IBrightnessState, IBrightnessController,
    IColorState, IColorController,
    IColorTemperatureState, IColorTemperatureController,
    IPowerSensor
{
    internal partial Light LightResource { get; set; }

    /// <summary>
    /// The light resource ID, used for room/zone child matching.
    /// </summary>
    public Guid ReferenceId => LightResource.Id;

    [Derived]
    public bool IsOnOffLight => LightResource?.Type == "On/Off light";

    [Derived]
    [State]
    public bool? IsOn => LightResource?.On?.IsOn;

    [Derived]
    [State]
    public decimal? Brightness => !IsOnOffLight ? (decimal?)LightResource?.Dimming?.Brightness / 100m : null;

    [Derived]
    [State]
    public decimal? ColorTemperature =>
        LightResource?.ColorTemperature != null
            ? (LightResource?.ColorTemperature?.Mirek - LightResource?.ColorTemperature?.MirekSchema.MirekMinimum) /
              (decimal?)(LightResource?.ColorTemperature?.MirekSchema.MirekMaximum - LightResource?.ColorTemperature?.MirekSchema.MirekMinimum)
            : null;

    [Derived]
    [State]
    public string? Color => LightResource?.Color != null ? LightResource.ToHex(Model ?? "LCT001") : null;

    // From https://homeautotechs.com/philips-hue-light-models-full-list/
    [Derived]
    [State]
    public decimal? Power =>
        IsOn == true ? Model switch
        {
            "LWA001" => 9m,
            "LWA011" => 9m,
            "LWA017" => 9.5m,
            "LCA008" => 13.5m,
            "LCT015" => 9.5m,
            "LCT016" => 6.5m,
            "LCT007" => 9m,
            "LCT001" => 8.5m,
            "LCT011" => 9m,
            "LCT003" => 6.5m,
            "LCT012" => 6.5m,
            "LTW015" => 9.5m,
            "LTW010" => 9m,
            "LTG002" => 5m,
            "LTW004" => null,
            "LTW001" => null,
            "LTW011" => null,
            "LTW012" => 6m,
            "LTW013" => null,
            "LWB006" => null,
            "LWB010" => null,
            "LWB014" => null,
            "LWB004" => 9m,
            "LWA004" => 9m,
            "LWB007" => 9m,
            "LWE002" => 9m,
            "LWO001" => 9m,
            "LCL001" => 25m,
            "LCT026" => 6m,
            "LST002" => 20m,
            "LST001" => null,
            "VIYU-GU10-350-CCT-10011724" => 5m,
            _ => null,
        } :
        IsOn == false && IsConnected ? 0.5m :
        null;

    [Derived]
    public decimal? EnergyConsumed => null;

    [Derived]
    [State]
    public string? Socket =>
        Model switch
        {
            "LWA001" => "E26/E27",
            "LWA011" => "E26/E27",
            "LWA017" => "E26/E27",
            "LCA008" => "E26/E27",
            "LCT015" => "E26/E27",
            "LCT016" => "E26/E27",
            "LCT007" => "E26/E27",
            "LCT001" => null,
            "LCT011" => "E26/E27",
            "LCT003" => "GU10",
            "LCT012" => "E12/E14",
            "LTW015" => "E26/E27",
            "LTW010" => "E26/E27",
            "LTG002" => "GU10",
            "LTW004" => null,
            "LTW001" => null,
            "LTW011" => null,
            "LTW012" => "E12/E14",
            "LTW013" => "GU10",
            "LWB006" => "E26/E27",
            "LWB010" => null,
            "LWB014" => null,
            "LWB004" => "E26/E27",
            "LWA004" => "E26/E27",
            "LWB007" => null,
            "LWO001" => "E26/E27",
            "LWE002" => "E12/E14",
            "LCL001" => "n/a",
            "LCT026" => "n/a",
            "LST002" => "n/a",
            "LST001" => "n/a",
            "VIYU-GU10-350-CCT-10011724" => "GU10",
            _ => null,
        };

    [Derived]
    [State]
    public decimal? Lumen =>
        IsOn == true ? Model switch
        {
            "LWA001" => 806m,
            "LWA011" => 806m,
            "LWA017" => 1055m,
            "LCA008" => 1600m,
            "LCT015" => 85m * 9.5m,
            "LCT016" => 85m * 6.5m,
            "LCT007" => 90m * 9m,
            "LCT001" => 70m * 8.5m,
            "LCT011" => 650m,
            "LCT003" => 300m,
            "LCT012" => 450m,
            "LTW015" => 840m,
            "LTW010" => 806m,
            "LTG002" => 350m,
            "LTW004" => null,
            "LTW001" => null,
            "LTW011" => null,
            "LTW012" => 450m,
            "LTW013" => null,
            "LWB006" => null,
            "LWB010" => null,
            "LWB014" => null,
            "LWB004" => 750m,
            "LWA004" => 600m,
            "LWB007" => 750m,
            "LWO001" => 600m,
            "LWE002" => 470m,
            "LCL001" => 1600m,
            "LCT026" => 520m,
            "LST002" => 1600m,
            "LST001" => null,
            "VIYU-GU10-350-CCT-10011724" => 350m,
            _ => null,
        } :
        IsOn == false && IsConnected ? 0m :
        null;

    [Derived]
    public override string? Title => $"{base.Title} " +
        $"({string.Join(", ", new string?[]
        {
            Color != null ? "Color" : null,
            ColorTemperature.HasValue ? "Temperature" : null,
            Brightness.HasValue ? "Dimmable" : null,
            IsOnOffLight ? "On/Off" : null,
            Model
        }.Where(element => element != null))})";

    [Derived]
    public override string? IconName => "Lightbulb";

    [Derived]
    public override string? IconColor =>
        IsConnected == false ? "Error" :
        IsOn == true ? "Warning" : "Default";

    public HueLightbulb(Device device, ZigbeeConnectivity? zigbeeConnectivity, Light light, HueBridge bridge)
        : base(device, zigbeeConnectivity, bridge)
    {
        LightResource = light;
    }

    internal HueLightbulb Update(Device device, ZigbeeConnectivity? zigbeeConnectivity, Light light)
    {
        LightResource = light;
        Update(device, zigbeeConnectivity);
        return this;
    }

    [Operation]
    public async Task TurnOnAsync(CancellationToken cancellationToken)
    {
        var command = new UpdateLight()
            .TurnOn();

        var client = Bridge.GetOrCreateClient();
        ThrowOnError(await client.Light.UpdateAsync(LightResource.Id, command));
    }

    [Operation]
    public async Task TurnOffAsync(CancellationToken cancellationToken)
    {
        var command = new UpdateLight()
            .TurnOff();

        var client = Bridge.GetOrCreateClient();
        ThrowOnError(await client.Light.UpdateAsync(LightResource.Id, command));
    }

    [Operation]
    public async Task SetBrightnessAsync(decimal brightness, CancellationToken cancellationToken)
    {
        if (brightness == 0m)
        {
            await TurnOffAsync(cancellationToken);
            return;
        }

        var turnOffAfterChange = IsOn != true;

        var command = new UpdateLight()
            .TurnOn()
            .SetBrightness((double)(brightness * 100m));

        var client = Bridge.GetOrCreateClient();
        ThrowOnError(await client.Light.UpdateAsync(LightResource.Id, command));

        if (turnOffAfterChange)
        {
            await Task.Delay(HueBridge.SetBrightnessWhileOffDelayMs, cancellationToken);
            await TurnOffAsync(cancellationToken);
        }
    }

    [Operation]
    public async Task SetColorAsync(string color, CancellationToken cancellationToken)
    {
        var rgbColor = new RGBColor(color);
        var command = new UpdateLight()
            .SetColor(rgbColor, Model ?? "LCT001");

        var client = Bridge.GetOrCreateClient();
        ThrowOnError(await client.Light.UpdateAsync(LightResource.Id, command));
    }

    [Operation]
    public async Task SetColorTemperatureAsync(decimal colorTemperature, CancellationToken cancellationToken)
    {
        var newColorTemperature = (int?)(
            LightResource.ColorTemperature?.MirekSchema.MirekMinimum +
            colorTemperature * (LightResource.ColorTemperature?.MirekSchema.MirekMaximum -
                                LightResource.ColorTemperature?.MirekSchema.MirekMinimum));

        if (newColorTemperature != null)
        {
            var command = new UpdateLight
            {
                ColorTemperature = new ColorTemperature
                {
                    Mirek = newColorTemperature,
                }
            };

            var client = Bridge.GetOrCreateClient();
            ThrowOnError(await client.Light.UpdateAsync(LightResource.Id, command));
        }
    }
}
