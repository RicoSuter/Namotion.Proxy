﻿using Namotion.Interceptor;

namespace Namotion.Proxy.ChangeTracking;

public static class ReadPropertyRecorderExtensions
{
    public static ReadPropertyRecorderScope BeginReadPropertyRecording(this IInterceptor context)
    {
        ReadPropertyRecorder.Scopes.Value =
            ReadPropertyRecorder.Scopes.Value ??
            new Dictionary<IInterceptor, List<HashSet<PropertyReference>>>();

        var scope = new HashSet<PropertyReference>();
        ReadPropertyRecorder.Scopes.Value.TryAdd(context, new List<HashSet<PropertyReference>>());
        ReadPropertyRecorder.Scopes.Value[context].Add(scope);

        return new ReadPropertyRecorderScope(context, scope);
    }
}
