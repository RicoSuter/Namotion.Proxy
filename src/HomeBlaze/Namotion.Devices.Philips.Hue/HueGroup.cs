using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Light;
using HueApi.Models;
using HueApi.Models.Requests;
using Namotion.Interceptor.Attributes;
using static Namotion.Devices.Philips.Hue.HueCommandResult;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Philips Hue room or zone group with aggregated light controls.
/// </summary>
[InterceptorSubject]
public partial class HueGroup :
    ILightbulb,
    IBrightnessState, IBrightnessController,
    IColorState, IColorController,
    IColorTemperatureState, IColorTemperatureController,
    IVirtualSubject,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    internal partial HueResource Group { get; set; }

    internal partial GroupedLight? GroupedLight { get; set; }

    public HueBridge Bridge { get; }

    public Guid ResourceId => Group.Id;

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State]
    public partial Dictionary<string, HueLightbulb> Lights { get; internal set; }

    [Derived]
    public string? Title => Group?.Metadata?.Name ?? "n/a";

    [Derived]
    public string? IconName => "Layers";

    [Derived]
    public string? IconColor => IsOn == true ? "Warning" : "Default";

    [Derived]
    [State]
    public bool? IsOn => GroupedLight?.On?.IsOn;

    [Derived]
    [State]
    public decimal? Lumen => Lights.Values.Sum(light => light.Lumen);

    [Derived]
    [State]
    public string? Color =>
        Lights.Values
            .GroupBy(light => light.Color)
            .OrderBy(group => group.Count())
            .FirstOrDefault(group => group.Count() == Lights.Count)?
            .FirstOrDefault()?
            .Color;

    [Derived]
    [State]
    public decimal? ColorTemperature =>
        Lights.Values
            .GroupBy(light => light.Color)
            .OrderBy(group => group.Count())
            .FirstOrDefault(group => group.Count() == Lights.Count)?
            .FirstOrDefault()?
            .ColorTemperature;

    [Derived]
    [State]
    public decimal? Brightness =>
        (decimal?)GroupedLight?.Dimming?.Brightness / 100m ??
        Lights.Values
            .GroupBy(light => light.Color)
            .OrderBy(group => group.Count())
            .FirstOrDefault(group => group.Count() == Lights.Count)?
            .FirstOrDefault()?
            .Brightness;

    public HueGroup(HueResource group, GroupedLight? groupedLight, HueLightbulb[] lights, HueBridge bridge)
    {
        Bridge = bridge;
        Group = group;
        GroupedLight = groupedLight;
        Lights = lights.ToDictionary(l => l.ResourceId.ToString());
        LastUpdated = DateTimeOffset.Now;
    }

    internal HueGroup Update(HueResource group, GroupedLight? groupedLight, HueLightbulb[] lights)
    {
        Group = group;
        GroupedLight = groupedLight;
        Lights = lights.ToDictionary(l => l.ResourceId.ToString());
        LastUpdated = DateTimeOffset.Now;
        return this;
    }

    [Operation]
    public async Task TurnOnAsync(CancellationToken cancellationToken)
    {
        if (GroupedLight is not null)
        {
            var command = new UpdateGroupedLight()
                .TurnOn();

            var client = Bridge.GetOrCreateClient();
            ThrowOnError(await client.GroupedLight.UpdateAsync(GroupedLight.Id, command));
        }
    }

    [Operation]
    public async Task TurnOffAsync(CancellationToken cancellationToken)
    {
        if (GroupedLight is not null)
        {
            var command = new UpdateGroupedLight()
                .TurnOff();

            var client = Bridge.GetOrCreateClient();
            ThrowOnError(await client.GroupedLight.UpdateAsync(GroupedLight.Id, command));
        }
    }

    [Operation]
    public async Task SetBrightnessAsync(decimal brightness, CancellationToken cancellationToken)
    {
        if (brightness == 0m)
        {
            await TurnOffAsync(cancellationToken);
            return;
        }

        if (GroupedLight is not null)
        {
            var turnOffAfterChange = IsOn != true;

            var command = new UpdateGroupedLight()
                .TurnOn()
                .SetBrightness((double)(brightness * 100m));

            var client = Bridge.GetOrCreateClient();
            ThrowOnError(await client.GroupedLight.UpdateAsync(GroupedLight.Id, command));

            if (turnOffAfterChange)
            {
                await Task.Delay(HueBridge.SetBrightnessWhileOffDelayMs, cancellationToken);
                await TurnOffAsync(cancellationToken);
            }
        }
    }

    [Operation]
    public async Task SetColorAsync(string color, CancellationToken cancellationToken)
    {
        foreach (var light in Lights.Values)
        {
            await light.SetColorAsync(color, cancellationToken);
        }
    }

    [Operation]
    public async Task SetColorTemperatureAsync(decimal colorTemperature, CancellationToken cancellationToken)
    {
        foreach (var light in Lights.Values)
        {
            await light.SetColorTemperatureAsync(colorTemperature, cancellationToken);
        }
    }
}
