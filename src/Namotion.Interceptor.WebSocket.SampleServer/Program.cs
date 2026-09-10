using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.SamplesModel.Workers;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle()
    .WithHostedServices(builder.Services);

var root = Root.CreateWithPersons(context);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddHostedService<ServerWorker>();
builder.Services.AddWebSocketSubjectServer<Root>(configuration =>
{
    configuration.Port = 8080;
    configuration.PathProvider = new AttributeBasedPathProvider("ws");
});

Console.WriteLine("Starting WebSocket server on ws://localhost:8080/ws");

using var performanceProfiler = new PerformanceProfiler(context, "Server");
var host = builder.Build();
host.Run();
