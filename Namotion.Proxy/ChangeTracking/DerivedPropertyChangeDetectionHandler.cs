﻿using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.ChangeTracking;

/// <summary>
/// Should be used with <see cref="InitiallyLoadDerivedPropertiesHandler"/> so that dependencies are initially set up.
/// </summary>
internal class DerivedPropertyChangeDetectionHandler : IProxyReadHandler, IProxyWriteHandler
{
    [ThreadStatic]
    private static Stack<HashSet<ProxyPropertyReference>>? _currentTouchedProperties;

    public object? GetProperty(ReadProxyPropertyContext context, Func<ReadProxyPropertyContext, object?> next)
    {
        if (context.Property.Proxy.Properties[context.Property.PropertyName].IsDerived)
        {
            TryStartRecordTouchedProperties();

            var result = next(context);

            context.Property.Proxy.SetLastKnownValue(context.Property.PropertyName, result);

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

    public void SetProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next)
    {
        next.Invoke(context);

        var usedByProperties = context.Property.Proxy.GetUsedByProperties(context.Property.PropertyName);
        if (usedByProperties.Any())
        {
            lock (usedByProperties)
            {
                foreach (var usedByProperty in usedByProperties)
                {
                    var oldValue = usedByProperty.Proxy.GetLastKnownValue(usedByProperty.PropertyName);
                    var newValue = usedByProperty.Proxy
                        .Properties[usedByProperty.PropertyName]
                        .GetValue?
                        .Invoke(usedByProperty.Proxy);

                    var changedContext = new ProxyPropertyChanged(usedByProperty, oldValue, newValue, context.Context);
                    foreach (var handler in context.Context.GetHandlers<IProxyPropertyChangedHandler>())
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
            _currentTouchedProperties = new Stack<HashSet<ProxyPropertyReference>>();
        }

        _currentTouchedProperties.Push(new HashSet<ProxyPropertyReference>());
    }

    private void StoreRecordedTouchedProperties(ReadProxyPropertyContext context)
    {
        var newProperties = _currentTouchedProperties!.Pop();

        var previouslyRequiredProperties = context.Property.Proxy.GetRequiredProperties(context.Property.PropertyName);
        foreach (var previouslyRequiredProperty in previouslyRequiredProperties)
        {
            if (!newProperties.Contains(previouslyRequiredProperty))
            {
                var usedByProperties = previouslyRequiredProperty.Proxy.GetUsedByProperties(previouslyRequiredProperty.PropertyName);
                lock (usedByProperties)
                    usedByProperties.Remove(previouslyRequiredProperty);
            }
        }

        context.Property.Proxy.SetRequiredProperties(context.Property.PropertyName, newProperties);

        foreach (var newlyRequiredProperty in newProperties)
        {
            var usedByProperties = newlyRequiredProperty.Proxy.GetUsedByProperties(newlyRequiredProperty.PropertyName);
            lock (usedByProperties)
                usedByProperties.Add(new ProxyPropertyReference(context.Property.Proxy, context.Property.PropertyName));
        }
    }

    private void TouchProperty(ReadProxyPropertyContext context)
    {
        if (_currentTouchedProperties?.TryPeek(out var touchedProperties) == true)
        {
            touchedProperties.Add(new ProxyPropertyReference(context.Property.Proxy, context.Property.PropertyName));
        }
        else
        {
            _currentTouchedProperties = null;
        }
    }
}
