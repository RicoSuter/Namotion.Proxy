using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Ads.Attributes;

namespace Namotion.Interceptor.Ads.Tests.Models;

/// <summary>
/// Test model representing a machine with scalar, array, reference, collection, and dictionary properties.
/// </summary>
[InterceptorSubject]
public partial class Machine
{
    /// <summary>
    /// Gets or sets the machine name (scalar string).
    /// </summary>
    [AdsVariable("GVL.Machine.Name")]
    public partial string Name { get; set; }

    /// <summary>
    /// Gets or sets the temperature readings (primitive array).
    /// </summary>
    [AdsVariable("GVL.Machine.Temperatures")]
    public partial float[] Temperatures { get; set; }

    /// <summary>
    /// Gets or sets the motor (subject reference).
    /// </summary>
    [AdsVariable("GVL.Machine.Motor")]
    public partial Motor Motor { get; set; }

    /// <summary>
    /// Gets or sets the axes (subject collection).
    /// </summary>
    [AdsVariable("GVL.Machine.Axes")]
    public partial IList<Axis> Axes { get; set; }

    /// <summary>
    /// Gets or sets the devices (subject dictionary).
    /// </summary>
    [AdsVariable("GVL.Machine.Devices")]
    public partial IDictionary<string, Device> Devices { get; set; }
}

/// <summary>
/// Test model representing a motor with speed and torque properties.
/// </summary>
[InterceptorSubject]
public partial class Motor
{
    /// <summary>
    /// Gets or sets the motor speed.
    /// </summary>
    [AdsVariable("Speed")]
    public partial float Speed { get; set; }

    /// <summary>
    /// Gets or sets the motor torque.
    /// </summary>
    [AdsVariable("Torque")]
    public partial float Torque { get; set; }
}

/// <summary>
/// Test model representing an axis with a position property.
/// </summary>
[InterceptorSubject]
public partial class Axis
{
    /// <summary>
    /// Gets or sets the axis position.
    /// </summary>
    [AdsVariable("Position")]
    public partial float Position { get; set; }
}

/// <summary>
/// Test model representing a device with a temperature property.
/// </summary>
[InterceptorSubject]
public partial class Device
{
    /// <summary>
    /// Gets or sets the device temperature.
    /// </summary>
    [AdsVariable("Temperature")]
    public partial float Temperature { get; set; }
}
