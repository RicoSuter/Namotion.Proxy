using Microsoft.AspNetCore.Mvc;
using Namotion.Proxy;
using Namotion.Proxy.Abstractions;
using Namotion.Proxy.AspNetCore.Controllers;
using Namotion.Proxy.Attributes;
using Namotion.Proxy.Sources.Abstractions;
using Namotion.Proxy.Sources.Attributes;
using NSwag.Annotations;

namespace Namotion.Trackable.SampleWeb
{
    [GenerateProxy]
    public abstract class CarBase
    {
        public CarBase()
        {
            Tires = new Tire[]
            {
                new(),
                new(),
                new(),
                new()
            };
        }

        [TrackableSource("mqtt", "name")]
        public virtual string Name { get; set; } = "My Car";

        [TrackableSourcePath("mqtt", "tires")]
        public virtual Tire[] Tires { get; set; }

        [TrackableSource("mqtt", "averagePressure")]
        public virtual decimal AveragePressure => Tires.Average(t => t.Pressure);
    }

    [GenerateProxy]
    public abstract class TireBase
    {
        [TrackableSource("mqtt", "pressure")]
        [Unit("bar")]
        public virtual decimal Pressure { get; set; }

        [Unit("bar")]
        [PropertyAttribute(nameof(Pressure), "Minimum")]
        public virtual decimal Pressure_Minimum { get; set; } = 0.0m;

        [PropertyAttribute(nameof(Pressure), "Maximum")]
        public virtual decimal Pressure_Maximum => 4 * Pressure;
    }

    public class UnitAttribute : Attribute, IProxyPropertyInitializer
    {
        private readonly string _unit;

        public UnitAttribute(string unit)
        {
            _unit = unit;
        }

        public void InitializeProperty(ProxyProperty property, object? parentCollectionKey, IProxyContext context)
        {
            property.Parent.AddProperty(property.Property.PropertyName + "_Unit", typeof(string), () => _unit, null, new PropertyAttributeAttribute(property.Property.PropertyName, "Unit"));
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var context = ProxyContext
                .CreateBuilder()
                .WithRegistry()
                .WithFullPropertyTracking()
                .WithProxyLifecycle()
                .Build();

            var car = new Car(context);

            // trackable
            builder.Services.AddSingleton(car);
            builder.Services.AddSingleton<IProxyContext>(context);

            // trackable api controllers
            builder.Services.AddTrackableControllers<Car, TrackablesController<Car>>();

            // trackable UPC UA
            //builder.Services.AddOpcUaServerTrackableSource<Car>("mqtt");

            // trackable mqtt
            builder.Services.AddMqttServerTrackableSource<Car>("mqtt");

            // trackable graphql
            builder.Services
                .AddGraphQLServer()
                .AddInMemorySubscriptions()
                .AddTrackedGraphQL<Car>();

            // other asp services
            builder.Services.AddHostedService<Simulator>();
            builder.Services.AddOpenApiDocument();
            builder.Services.AddAuthorization();

            var app = builder.Build();

            app.UseHttpsRedirection();
            app.UseAuthorization();

            app.MapGraphQL();

            app.UseOpenApi();
            app.UseSwaggerUi();

            app.MapControllers();
            app.Run();
        }

        [OpenApiTag("Car")]
        [Route("/api/car")]
        public class TrackablesController<TProxy> : TrackablesControllerBase<TProxy>
            where TProxy : class, IProxy
        {
            public TrackablesController(IProxyContext context, TProxy proxy)
                : base(context, proxy)
            {
            }
        }

        public class Simulator : BackgroundService
        {
            private readonly Car _car;

            public Simulator(Car car)
            {
                _car = car;
            }

            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    _car.Tires[0].Pressure = Random.Shared.Next(0, 40) / 10m;
                    _car.Tires[1].Pressure = Random.Shared.Next(0, 40) / 10m;
                    _car.Tires[2].Pressure = Random.Shared.Next(0, 40) / 10m;
                    _car.Tires[3].Pressure = Random.Shared.Next(0, 40) / 10m;

                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
    }
}