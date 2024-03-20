﻿using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

public record struct TrackedProperty(IProxy Proxy, string PropertyName);

internal class DerivedPropertyChangeDetectionHandler : IProxyReadHandler, IProxyWriteHandler, IProxyPropertyRegistryHandler
{
    [ThreadStatic]
    private static Stack<HashSet<TrackedProperty>>? _currentTouchedProperties;
    private readonly bool _initiallyReadAllProperties;

    public DerivedPropertyChangeDetectionHandler(bool initiallyReadAllProperties)
    {
        _initiallyReadAllProperties = initiallyReadAllProperties;
    }

    public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        if (_initiallyReadAllProperties)
        {
            foreach (var property in proxy.Properties.Where(p => p.IsDerived))
            {
                property.ReadValue();
            }
        }
    }

    public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
    }

    public object? GetProperty(ProxyReadHandlerContext context, Func<ProxyReadHandlerContext, object?> next)
    {
        if (context.IsPropertyDerived)
        {
            TryStartRecordTouchedProperties();
            var result = next(context);
            StoreRecordedTouchedProperties(context);
            TouchProperty(context);
            return result;
        }
        else
        {
            var result = next(context);
            TouchProperty(context);
            return result;
        }
    }

    public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
    {
        next.Invoke(context);

        var usedByProperties = context.Proxy.GetUsedByProperties(context.PropertyName);
        if (usedByProperties.Any())
        {
            lock (usedByProperties)
            {
                foreach (var usedByProperty in usedByProperties)
                {
                    // TODO: how to provide current and new value?
                    var changedContext = new ProxyChangedHandlerContext(context.Context, usedByProperty.Proxy, usedByProperty.PropertyName, null, null);
                    foreach (var handler in context.Context.GetHandlers<IProxyChangedHandler>())
                    {
                        handler.RaisePropertyChanged(changedContext);
                    }
                }
            }
        }
    }

    private void TryStartRecordTouchedProperties()
    {
        if (_currentTouchedProperties == null)
        {
            _currentTouchedProperties = new Stack<HashSet<TrackedProperty>>();
        }

        _currentTouchedProperties.Push(new HashSet<TrackedProperty>());
    }

    private void StoreRecordedTouchedProperties(ProxyReadHandlerContext context)
    {
        var newProperties = _currentTouchedProperties!.Pop();

        var previouslyRequiredProperties = context.Proxy.GetRequiredProperties(context.PropertyName);
        foreach (var previouslyRequiredProperty in previouslyRequiredProperties)
        {
            if (!newProperties.Contains(previouslyRequiredProperty))
            {
                var usedByProperties = previouslyRequiredProperty.Proxy.GetUsedByProperties(previouslyRequiredProperty.PropertyName);
                lock (usedByProperties)
                    usedByProperties.Remove(previouslyRequiredProperty);
            }
        }

        context.Proxy.SetRequiredProperties(context.PropertyName, newProperties);

        foreach (var newlyRequiredProperty in newProperties)
        {
            var usedByProperties = newlyRequiredProperty.Proxy.GetUsedByProperties(newlyRequiredProperty.PropertyName);
            lock (usedByProperties)
                usedByProperties.Add(new TrackedProperty(context.Proxy, context.PropertyName));
        }
    }

    private void TouchProperty(ProxyReadHandlerContext context)
    {
        if (_currentTouchedProperties?.TryPeek(out var touchedProperties) == true)
        {
            touchedProperties.Add(new TrackedProperty(context.Proxy, context.PropertyName));
        }
        else
        {
            _currentTouchedProperties = null;
        }
    }
}
