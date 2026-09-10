using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.SamplesModel.Workers;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithHostedServices(builder.Services);

var root = new Root(context);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddHostedService<ClientWorker>();
builder.Services.AddWebSocketSubjectClientSource<Root>(configuration =>
{
    configuration.ServerUri = new Uri("ws://localhost:8080/ws");
    configuration.PathProvider = new AttributeBasedPathProvider("ws");
});

Console.WriteLine("Connecting to WebSocket server...");

using var performanceProfiler = new PerformanceProfiler(context, "Client");
var host = builder.Build();

// Register the IServiceProvider in the context so DefaultSubjectFactory can create subjects with context
context.AddService(host.Services);

host.Run();
