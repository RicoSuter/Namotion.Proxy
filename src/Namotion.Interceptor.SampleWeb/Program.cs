using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.SampleWeb
{
    [InterceptorSubject]
    public partial class Car
    {
        [Path("mqtt", "name")]
        [Path("opc", "Name")]
        public partial string Name { get; set; }

        [Path("mqtt", "tires")]
        [Path("opc", "Tires")]
        public partial Tire[] Tires { get; set; }

        [Derived]
        [Path("mqtt", "averagePressure")]
        [Path("opc", "AveragePressure")]
        public decimal AveragePressure => Tires.Average(t => t.Pressure);

        public Car()
        {
            Tires = Enumerable
                .Range(1, 4)
                .Select(_ => new Tire())
                .ToArray();

            Name = "My Car";
        }
    }

    [InterceptorSubject]
    public partial class Tire //: BackgroundService
    {
        [Path("mqtt", "pressure")]
        [Path("opc", "Pressure")]
        [Unit("bar")]
        public partial decimal Pressure { get; set; }

        [Unit("bar")]
        [Path("mqtt", "minimum")]
        [PropertyAttribute(nameof(Pressure), "Minimum")]
        public partial decimal Pressure_Minimum { get; set; }

        [Derived]
        [Path("mqtt", "maximum")]
        [Path("opc", "Maximum")]
        [PropertyAttribute(nameof(Pressure), "Maximum")]
        public decimal Pressure_Maximum => 4 * Pressure;

        public Tire()
        {
            Pressure_Minimum = 0.0m;
        }

        // protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        // {
        //     // This is automatically started by .WithHostedServices()
        //     while (!stoppingToken.IsCancellationRequested)
        //     {
        //         await Task.Delay(1000, stoppingToken);
        //         Console.WriteLine("Current pressure: " + Pressure);
        //     }
        // }
    }

    public class UnitAttribute : Attribute, ISubjectPropertyInitializer
    {
        private readonly string _unit;

        public UnitAttribute(string unit)
        {
            _unit = unit;
        }

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            property.AddAttribute("Unit", typeof(string),
                _ => _unit, null,
                new PathAttribute("mqtt", "unit"));
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithDataAnnotationValidation()
                .WithHostedServices(builder.Services);

            var car = new Car(context);

            // register subject and context
            builder.Services.AddSingleton(car);
            builder.Services.AddSingleton(context);

            // expose subject via OPC UA
            builder.Services.AddOpcUaSubjectServer<Car>("opc", rootName: "Root");
            // builder.Services.AddOpcUaSubjectClient<Car>("opc.tcp://localhost:4840", "opc", rootName: "Root");

            // expose subject via MQTT
            builder.Services.AddMqttSubjectServer<Car>("mqtt");
            //builder.Services.AddMqttSubjectServer<Tire>(sp => sp.GetRequiredService<Car>().Tires[2], "mqtt");

            // expose subject via GraphQL
            builder.Services
                .AddGraphQLServer()
                .AddInMemorySubscriptions()
                .AddSubjectGraphQL<Car>();

            // other asp services
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddOpenApiDocument();
            builder.Services.AddAuthorization();

            builder.Services.AddHostedService<Simulator>();

            var app = builder.Build();

            // expose subject via HTTP web api
            app.MapSubjectWebApis<Car>("api/car");
            
            app.UseHttpsRedirection();
            app.UseAuthorization();
            
            app.MapGraphQL();

            app.UseOpenApi();
            app.UseSwaggerUi();

            app.Run();
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

                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
    }
}