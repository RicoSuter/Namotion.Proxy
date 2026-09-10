using System.ComponentModel;
using HueApi.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

/// <summary>
/// The recordable [State] properties of the Hue subjects are [Derived] over the raw API resources the
/// bridge assigns on each refresh. Dependency tracking only sees reads of intercepted properties, so a
/// resource held in a plain auto-property makes its derived values change with no notification: they
/// render stale and are never recorded to history.
///
/// Every case drives the same Update() path the bridge uses, because assigning a resource directly
/// would pass even if the bridge reached its subjects some other way.
/// </summary>
public class HueDerivedStateTrackingTests
{
    [Fact]
    public void WhenGroupedLightIsReplaced_ThenIsOnRaisesPropertyChanged()
    {
        // Arrange
        var room = TestHelpers.CreateRoom();
        var group = Track(new HueGroup(room, TestHelpers.CreateGroupedLight(isOn: false), [], null!));
        Assert.False(group.IsOn);
        var firedEvents = TrackPropertyChanged(group);

        // Act
        group.Update(room, TestHelpers.CreateGroupedLight(isOn: true), []);

        // Assert
        Assert.True(group.IsOn);
        Assert.Contains(nameof(HueGroup.IsOn), firedEvents);
    }

    [Fact]
    public void WhenGroupIsRenamed_ThenTitleRaisesPropertyChanged()
    {
        // Arrange
        var group = Track(new HueGroup(
            TestHelpers.CreateRoom("Old Name"), TestHelpers.CreateGroupedLight(isOn: true), [], null!));
        Assert.Equal("Old Name", group.Title);
        var firedEvents = TrackPropertyChanged(group);

        // Act
        group.Update(TestHelpers.CreateRoom("New Name"), TestHelpers.CreateGroupedLight(isOn: true), []);

        // Assert
        Assert.Equal("New Name", group.Title);
        Assert.Contains(nameof(HueGroup.Title), firedEvents);
    }

    [Fact]
    public void WhenLightResourceIsReplaced_ThenIsOnRaisesPropertyChanged()
    {
        // Arrange
        var device = TestHelpers.CreateDevice("TEST001");
        var zigbee = TestHelpers.CreateZigbeeConnectivity(ConnectivityStatus.connected);
        var lightbulb = Track(new HueLightbulb(device, zigbee, TestHelpers.CreateLight(isOn: false), null!));
        Assert.False(lightbulb.IsOn);
        var firedEvents = TrackPropertyChanged(lightbulb);

        // Act
        lightbulb.Update(device, zigbee, TestHelpers.CreateLight(isOn: true));

        // Assert
        Assert.True(lightbulb.IsOn);
        Assert.Contains(nameof(HueLightbulb.IsOn), firedEvents);
    }

    [Fact]
    public void WhenZigbeeConnectivityIsReplaced_ThenIsConnectedRaisesPropertyChanged()
    {
        // Arrange
        var device = TestHelpers.CreateDevice("TEST001");
        var hueDevice = Track(new HueDevice(
            device, TestHelpers.CreateZigbeeConnectivity(ConnectivityStatus.connected), null!));
        Assert.True(hueDevice.IsConnected);
        var firedEvents = TrackPropertyChanged(hueDevice);

        // Act
        hueDevice.Update(device, TestHelpers.CreateZigbeeConnectivity(ConnectivityStatus.connectivity_issue));

        // Assert
        Assert.False(hueDevice.IsConnected);
        Assert.Contains(nameof(HueDevice.IsConnected), firedEvents);
    }

    [Fact]
    public void WhenDeviceResourceIsReplaced_ThenSoftwareVersionRaisesPropertyChanged()
    {
        // Arrange
        var zigbee = TestHelpers.CreateZigbeeConnectivity(ConnectivityStatus.connected);
        var hueDevice = Track(new HueDevice(CreateDeviceWithVersion("1.0.0"), zigbee, null!));
        Assert.Equal("1.0.0", hueDevice.SoftwareVersion);
        var firedEvents = TrackPropertyChanged(hueDevice);

        // Act: what an over-the-air firmware update looks like to the bridge.
        hueDevice.Update(CreateDeviceWithVersion("1.1.0"), zigbee);

        // Assert
        Assert.Equal("1.1.0", hueDevice.SoftwareVersion);
        Assert.Contains(nameof(HueDevice.SoftwareVersion), firedEvents);
    }

    [Fact]
    public void WhenMotionResourceIsReplaced_ThenIsPresentRaisesPropertyChanged()
    {
        // Arrange
        var motionDevice = Track(CreateMotionDevice(isPresent: false));
        Assert.False(motionDevice.IsPresent);
        var firedEvents = TrackPropertyChanged(motionDevice);

        // Act
        motionDevice.Update(
            TestHelpers.CreateDevice("SML001"),
            TestHelpers.CreateZigbeeConnectivity(ConnectivityStatus.connected),
            null, null, null, CreateMotionResource(isPresent: true));

        // Assert
        Assert.True(motionDevice.IsPresent);
        Assert.Contains(nameof(HueMotionDevice.IsPresent), firedEvents);
    }

    [Fact]
    public void WhenTemperatureResourceIsReplaced_ThenTemperatureRaisesPropertyChanged()
    {
        // Arrange
        var motionDevice = Track(CreateMotionDevice(isPresent: false, CreateTemperatureResource(21.5m)));
        Assert.Equal(21.5m, motionDevice.Temperature);
        var firedEvents = TrackPropertyChanged(motionDevice);

        // Act
        motionDevice.Update(
            TestHelpers.CreateDevice("SML001"),
            TestHelpers.CreateZigbeeConnectivity(ConnectivityStatus.connected),
            null, CreateTemperatureResource(22.5m), null, CreateMotionResource(isPresent: false));

        // Assert
        Assert.Equal(22.5m, motionDevice.Temperature);
        Assert.Contains(nameof(HueMotionDevice.Temperature), firedEvents);
    }

    [Fact]
    public void WhenDevicePowerResourceIsReplaced_ThenBatteryLevelRaisesPropertyChanged()
    {
        // Arrange
        var buttonDevice = Track(TestHelpers.CreateButtonDevice([TestHelpers.CreateButtonResource(null)]));
        buttonDevice.DevicePowerResource = CreateDevicePower(80);
        Assert.Equal(0.8m, buttonDevice.BatteryLevel);
        var firedEvents = TrackPropertyChanged(buttonDevice);

        // Act
        buttonDevice.DevicePowerResource = CreateDevicePower(40);

        // Assert
        Assert.Equal(0.4m, buttonDevice.BatteryLevel);
        Assert.Contains(nameof(HueButtonDevice.BatteryLevel), firedEvents);
    }

    private static HueMotionDevice CreateMotionDevice(
        bool isPresent, HueApi.Models.Sensors.TemperatureResource? temperature = null) =>
        new(TestHelpers.CreateDevice("SML001"),
            TestHelpers.CreateZigbeeConnectivity(ConnectivityStatus.connected),
            null,
            temperature,
            null,
            CreateMotionResource(isPresent),
            null!);

    private static HueApi.Models.Sensors.MotionResource CreateMotionResource(bool isPresent) =>
        new()
        {
            Id = Guid.NewGuid(),
            Motion = new HueApi.Models.Sensors.Motion
            {
                MotionReport = new HueApi.Models.Sensors.MotionReport { Motion = isPresent }
            }
        };

    private static HueApi.Models.Sensors.TemperatureResource CreateTemperatureResource(decimal celsius) =>
        new()
        {
            Id = Guid.NewGuid(),
            Temperature = new HueApi.Models.Sensors.Temperature
            {
                TemperatureValid = true,
                TemperatureReport = new HueApi.Models.Sensors.TemperatureReport { Temperature = celsius }
            }
        };

    private static DevicePower CreateDevicePower(int batteryPercent) =>
        new()
        {
            Id = Guid.NewGuid(),
            PowerState = new PowerState { BatteryLevel = batteryPercent }
        };

    private static Device CreateDeviceWithVersion(string softwareVersion)
    {
        var device = TestHelpers.CreateDevice("TEST001");
        device.ProductData.SoftwareVersion = softwareVersion;
        return device;
    }

    /// <summary>
    /// Attaches the subject to a context with derived-property tracking, the way the HomeBlaze graph
    /// does when the bridge publishes its children.
    /// </summary>
    private static T Track<T>(T subject) where T : IInterceptorSubject
    {
        subject.Context.AddFallbackContext(InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry());

        return subject;
    }

    private static List<string> TrackPropertyChanged(INotifyPropertyChanged subject)
    {
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, arguments) => firedEvents.Add(arguments.PropertyName!);
        return firedEvents;
    }
}
