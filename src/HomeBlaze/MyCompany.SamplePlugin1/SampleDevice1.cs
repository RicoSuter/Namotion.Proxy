using Bogus;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using MyCompany.Abstractions;
using System.ComponentModel;
using Namotion.Interceptor.Attributes;

namespace MyCompany.SamplePlugin1;

[InterceptorSubject]
[Category("Devices")]
[Description("A sample temperature sensor plugin that simulates temperature, humidity, pressure and battery readings.")]
public partial class SampleDevice1 : BackgroundService, IConfigurable, ITitleProvider, IMyDevice
{
    private readonly Faker _faker = new();

    public SampleDevice1()
    {
        Name = "Sample Device 1";
        PollingIntervalMs = 2000;
        Temperature = 22.0;
        Humidity = 55.0;
        Pressure = 1013.0;
        BatteryLevel = 100.0;
    }

    [Derived]
    public string? Title => Name;

    // IMyDevice
    [Derived]
    public string DeviceName => Name;
    public string DeviceType => "TemperatureSensor";
    [Derived]
    public double? CurrentValue => Temperature;

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial int PollingIntervalMs { get; set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial double Temperature { get; internal set; }

    [State]
    public partial double Humidity { get; internal set; }

    [State(Unit = StateUnit.Hectopascal)]
    public partial double Pressure { get; internal set; }

    [State]
    public partial double BatteryLevel { get; internal set; }

    [Derived]
    public double HeatIndex => CalculateHeatIndex(Temperature, Humidity);

    [Derived]
    public bool IsLowBattery => BatteryLevel < 20;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Temperature = Math.Clamp(Temperature + _faker.Random.Double(-0.5, 0.5), -20, 50);
            Humidity = Math.Clamp(Humidity + _faker.Random.Double(-1, 1), 0, 100);
            Pressure = Math.Clamp(Pressure + _faker.Random.Double(-0.5, 0.5), 950, 1050);
            BatteryLevel = Math.Clamp(BatteryLevel - _faker.Random.Double(0, 0.1), 0, 100);

            await Task.Delay(PollingIntervalMs, stoppingToken);
        }
    }

    private static double CalculateHeatIndex(double temperatureC, double humidity)
    {
        var t = temperatureC * 9.0 / 5.0 + 32.0;
        var hi = -42.379 + 2.04901523 * t + 10.14333127 * humidity
                 - 0.22475541 * t * humidity - 0.00683783 * t * t
                 - 0.05481717 * humidity * humidity + 0.00122874 * t * t * humidity
                 + 0.00085282 * t * humidity * humidity - 0.00000199 * t * t * humidity * humidity;
        return (hi - 32.0) * 5.0 / 9.0;
    }
}
