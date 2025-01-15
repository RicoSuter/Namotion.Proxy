﻿using System.Collections.Concurrent;

namespace Namotion.Interceptor;

public interface IInterceptorSubject
{
    /// <summary>
    /// Gets the currently associated context.
    /// </summary>
    IInterceptorCollection? Interceptor { get; set; }

    /// <summary>
    /// Gets the additional data of this proxy.
    /// </summary>
    ConcurrentDictionary<string, object?> Data { get; }

    /// <summary>
    /// Gets the reflected properties (should be cached).
    /// </summary>
    IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; }
}