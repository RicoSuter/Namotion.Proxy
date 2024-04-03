﻿using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class ProxyContext : IProxyContext
{
    private readonly IEnumerable<IProxyHandler> _handlers;

    private readonly IProxyReadHandler[] _readHandlers;
    private readonly IProxyWriteHandler[] _writeHandlers;

    public static ProxyContextBuilder CreateBuilder()
    {
        return new ProxyContextBuilder();
    }

    public ProxyContext(IEnumerable<IProxyHandler> handlers)
    {
        _handlers = handlers.ToArray();
        _readHandlers = handlers.OfType<IProxyReadHandler>().Reverse().ToArray();
        _writeHandlers = handlers.OfType<IProxyWriteHandler>().Reverse().ToArray();
    }

    public IEnumerable<THandler> GetHandlers<THandler>()
        where THandler : IProxyHandler
    {
        return _handlers.OfType<THandler>();
    }

    public object? GetProperty(IProxy proxy, string propertyName, Func<object?> readValue)
    {
        var context = new ReadProxyPropertyContext(new ProxyPropertyReference(proxy, propertyName), this);

        for (int i = 0; i < _readHandlers.Length; i++)
        {
            var handler = _readHandlers[i];
            var previousReadValue = readValue;
            readValue = () =>
            {
                return handler.ReadProperty(context, ctx => previousReadValue());
            };
        }

        return readValue.Invoke();
    }

    public void SetProperty(IProxy proxy, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var context = new WriteProxyPropertyContext(new ProxyPropertyReference(proxy, propertyName), null, GetReadValueFunctionWithCache(readValue), this);

        for (int i = 0; i < _writeHandlers.Length; i++)
        {
            var handler = _writeHandlers[i];
            var previousWriteValue = writeValue;
            writeValue = (value) =>
            {
                handler.WriteProperty(context with { NewValue = value }, ctx => previousWriteValue(ctx.NewValue));
            };
        }

        writeValue.Invoke(newValue);
    }

    private static Func<object?> GetReadValueFunctionWithCache(Func<object?> readValue)
    {
        // TODO: do we need a lock?
        var isRead = false;
        object? previousValue = null;
        return () =>
        {
            if (isRead == false)
            {
                previousValue = readValue();
                isRead = true;
            }
            return previousValue;
        };
    }
}
