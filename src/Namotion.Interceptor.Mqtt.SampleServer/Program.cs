using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet.Protocol;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
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

var root = Root.CreateWithPersons(context);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddHostedService<ServerWorker>();
builder.Services.AddMqttSubjectServer(
    _ => root,
    _ => new MqttServerConfiguration
    {
        // BrokerHost = "127.0.0.1", // Optional: bind to specific interface
        BrokerPort = 1883,
        Mapper = new MqttCompositeMapper(
            new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
            new MqttAttributeMapper("mqtt")),
        DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        UseRetainedMessages = true
    });

using var performanceProfiler = new PerformanceProfiler(context, "Server");
var host = builder.Build();
host.Run();
