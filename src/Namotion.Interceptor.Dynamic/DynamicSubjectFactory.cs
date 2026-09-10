using System.Collections.Concurrent;
using System.Reflection;
using Castle.DynamicProxy;

namespace Namotion.Interceptor.Dynamic;

public class DynamicSubjectFactory
{
    private static readonly ProxyGenerator ProxyGenerator = new();
    private static readonly ConcurrentDictionary<string, SubjectPropertyMetadata[]> PropertyCache = new();

    public static IInterceptorSubject CreateDynamicSubject(params Type[] interfaces)
    {
        return CreateSubject<DynamicSubject>(interfaces);
    }

    public static TSubject CreateSubject<TSubject>(params Type[] interfaces)
        where TSubject : IInterceptorSubject
    {
        return (TSubject)CreateSubject(typeof(TSubject), interfaces);
    }
    
    public static IInterceptorSubject CreateSubject(Type type, params Type[] interfaces)
    {
        var subject = (IInterceptorSubject)ProxyGenerator
            .CreateClassProxy(type, interfaces, new DynamicSubjectInterceptor());
        
        var key = type.FullName + "|" + string.Join("|", interfaces.Select(i => i.FullName));
        var missingProperties = PropertyCache.GetOrAdd(key, static (_, newSubject) =>
        {
            var existingProperties = newSubject.Properties.Values;
            return newSubject
                .GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => existingProperties.All(ep => ep.Name != p.Name))
                .DistinctBy(p => p.Name)
                .Select(property => new SubjectPropertyMetadata(
                    property.Name,
                    property.PropertyType,
                    property.GetCustomAttributesIncludingInterfaces(),
                    property.GetValue,
                    property.SetValue,
                    isIntercepted: true,
                    isDynamic: false))
                .ToArray();
        }, subject);

        subject.AddProperties(missingProperties);
        return subject;
    }

    private class DynamicSubjectInterceptor : IInterceptor
    {
        private readonly ConcurrentDictionary<string, object?> _propertyValues = new();

        // Keyed on the intercepted MethodInfo rather than the property name, so a call resolves
        // its name, declared type, reader delegate and setter in one reference-hashed probe. The
        // name and the delegates are built once per accessor instead of per call: the name is a
        // substring of "get_X"/"set_X", the declared type of a setter comes from GetParameters(),
        // and the reader closes over both, so deriving them per call allocated a string, an array
        // and a closure on every property access.
        private readonly ConcurrentDictionary<MethodInfo, PropertyAccessor> _accessors = new();

        public void Intercept(IInvocation invocation)
        {
            if (invocation.MethodInvocationTarget is not null)
            {
                invocation.Proceed();
                return;
            }
            
            var subject = (IInterceptorSubject)invocation.Proxy;
            var context = subject.Executor;

            if (invocation.Method.IsSpecialName &&
                invocation.Method.Name.StartsWith("get_"))
            {
                var accessor = GetAccessor(invocation.Method, isSetter: false);
                invocation.ReturnValue = context.GetPropertyValue(accessor.Name, accessor.Read);
            }
            else if (invocation.Method.IsSpecialName &&
                     invocation.Method.Name.StartsWith("set_"))
            {
                // The value arrives boxed here, so a TProperty-routed write would classify
                // every proxied property as structural; the intercepted setter carries the
                // declared type, so a setter built once per property routes from it instead.
                var accessor = GetAccessor(invocation.Method, isSetter: true);
                accessor.Write!(subject, invocation.Arguments[0]);
                invocation.ReturnValue = null;
            }
            else
            {
                invocation.ReturnValue = context.InvokeMethod(
                    // TODO: Should we really throw away subject here?
                    invocation.Method.Name, invocation.Arguments, (_, parameters) =>
                    {
                        parameters.CopyTo(invocation.Arguments, 0);
                        invocation.Proceed();
                        return invocation.ReturnValue;
                    });
            }
        }

        private PropertyAccessor GetAccessor(MethodInfo method, bool isSetter)
        {
            return _accessors.GetOrAdd(
                method,
                static (accessorMethod, state) => PropertyAccessor.Create(accessorMethod, state.isSetter, state.interceptor),
                (interceptor: this, isSetter));
        }

        private object? ReadProperty(string propertyName, Type propertyType)
        {
            // A present key is the hot path, and GetOrAdd builds its factory delegate before it
            // looks, so going through it unconditionally allocated a closure on every read. A
            // stored null is a legitimate hit, which TryGetValue reports correctly.
            if (_propertyValues.TryGetValue(propertyName, out var existing))
            {
                return existing;
            }

            return _propertyValues.GetOrAdd(
                propertyName,
                static (_, type) => type.IsValueType ? Activator.CreateInstance(type) : null,
                propertyType);
        }

        private void WriteProperty(string propertyName, object? newValue)
        {
            _propertyValues[propertyName] = newValue;
        }

        /// <summary>
        /// Everything one intercepted property accessor needs, resolved once. <see cref="Read"/>
        /// is retained rather than rebuilt so the getter path hands the same delegate instance to
        /// the interceptor chain on every call.
        /// </summary>
        private sealed class PropertyAccessor
        {
            private PropertyAccessor(string name, Func<IInterceptorSubject, object?> read, Action<IInterceptorSubject, object?>? write)
            {
                Name = name;
                Read = read;
                Write = write;
            }

            public string Name { get; }

            public Func<IInterceptorSubject, object?> Read { get; }

            public Action<IInterceptorSubject, object?>? Write { get; }

            public static PropertyAccessor Create(MethodInfo method, bool isSetter, DynamicSubjectInterceptor interceptor)
            {
                var name = method.Name[4..];
                var declaredType = isSetter
                    ? method.GetParameters().Single().ParameterType
                    : method.ReturnType;

                var read = new Func<IInterceptorSubject, object?>(_ => interceptor.ReadProperty(name, declaredType));
                var write = isSetter
                    ? TypedPropertyWriteFactory.CreateSetter(
                        declaredType, name, read, (_, value) => interceptor.WriteProperty(name, value))
                    : null;

                return new PropertyAccessor(name, read, write);
            }
        }
    }
}