using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.SamplesModel.Workers;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle()
    .WithDataAnnotationValidation()
    .WithHostedServices(builder.Services);

// OPC UA client creates just the root - persons array will be loaded from server
var root = new Root(context);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddHostedService<ClientWorker>();
builder.Services.AddOpcUaSubjectClientSource<Root>("opc.tcp://localhost:4840", "opc", rootPath: ["Root"]);

using var performanceProfiler = new PerformanceProfiler(context, "Client");
var host = builder.Build();
host.Run();
