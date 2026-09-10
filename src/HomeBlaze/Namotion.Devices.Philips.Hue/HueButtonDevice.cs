using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using HueApi.Models;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Philips Hue button device (e.g., dimmer switch, tap switch) with battery and multiple buttons.
/// </summary>
[InterceptorSubject]
public partial class HueButtonDevice : HueDevice,
    IBatteryState,
    IDisposable
{
    internal partial DevicePower? DevicePowerResource { get; set; }

    [Derived]
    public override string? IconName =>
        Buttons.Any(button => button.ButtonState != HomeBlaze.Abstractions.Inputs.ButtonState.None)
            ? "RadioButtonChecked"
            : "RadioButtonUnchecked";

    [Derived]
    public override string? IconColor => null;

    [Derived]
    [State]
    public decimal? BatteryLevel => DevicePowerResource?.PowerState?.BatteryLevel / 100m;

    [State]
    public partial HueButton[] Buttons { get; internal set; }

    public HueButtonDevice(
        Device device,
        ZigbeeConnectivity? zigbeeConnectivity,
        DevicePower? devicePower,
        ButtonResource[] buttons,
        HueBridge bridge)
        : base(device, zigbeeConnectivity, bridge)
    {
        DevicePowerResource = devicePower;
        Buttons = [];
        Update(device, zigbeeConnectivity, devicePower, buttons);
    }

    internal HueButtonDevice Update(
        Device device,
        ZigbeeConnectivity? zigbeeConnectivity,
        DevicePower? devicePower,
        ButtonResource[] buttons)
    {
        Update(device, zigbeeConnectivity);
        DevicePowerResource = devicePower;

        var oldButtons = Buttons;
        var newButtons = buttons
            .Select((button, index) => oldButtons
                .SingleOrDefault(existingButton => existingButton.ResourceId == button.Id)?
                    .Update(button, oldButtons.Length == 0)
                ?? new HueButton("Button " + (index + 1), button, this, oldButtons.Length == 0))
            .ToArray();

        // Dispose buttons that are no longer present
        foreach (var oldButton in oldButtons)
        {
            if (!newButtons.Contains(oldButton))
            {
                oldButton.Dispose();
            }
        }

        Buttons = newButtons;

        return this;
    }

    public void Dispose()
    {
        foreach (var button in Buttons)
        {
            button.Dispose();
        }
    }
}
