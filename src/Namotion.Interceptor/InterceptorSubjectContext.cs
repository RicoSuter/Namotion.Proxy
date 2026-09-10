using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Ordering;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor;

public sealed class InterceptorSubjectContext : IInterceptorSubjectContext
{
    // The services and everything derived from them (service query results, compiled interceptor
    // chains) live in one immutable snapshot per context, published atomically. Queries take no
    // lock: a query pins one snapshot with a single volatile read, and every mutation publishes a
    // fresh snapshot with empty caches, so a cache entry can never survive the registration that
    // invalidated it. Mutators serialize on _mutationLock.

    // Declared before the marker so that a future change to marker construction cannot observe
    // its zero-initialized value.
    private static int _lastPropertyTypeIndex = -1;

    /// <summary>
    /// The per-property-type statics of the write path: a dense index per intercepted property
    /// type, so a compiled chain is found by indexing an array instead of hashing a
    /// <see cref="Type"/>, and the structural classification of the type, so the unified write
    /// entry routes without calling the classifier. The index is handed out process wide to keep
    /// the lookup a plain array read; the cost is that an array is as long as the largest index
    /// its context has seen rather than the number of types it uses.
    /// </summary>
    internal static class PropertyTypeIndex<TProperty>
    {
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly int Value = Interlocked.Increment(ref _lastPropertyTypeIndex);

        // Type-only classification agrees with the runtime authority (the lifecycle classifies
        // from the declared property type) whenever TProperty is that declared type, which holds
        // by construction for generated setters. A boxed object fails closed to structural, while
        // a TProperty narrowed below the declared type routes scalar and forfeits the pre-chain
        // seam, which is why boxed callers instantiate this entry with the declared type via a
        // cached typed delegate rather than write as object.
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool CanContainSubjects = typeof(TProperty).CanContainSubjects();
    }

    // Closed ISingletonContextService<TContract> interfaces per implementation type, discovered
    // once per type so that repeated registrations pay a dictionary lookup instead of an interface
    // walk. Only the mutators below read it; service resolution and interceptor execution never
    // touch singleton contracts.
    private static readonly ConcurrentDictionary<Type, Type[]> SingletonContractsByImplementationType = new();

    // Written via Interlocked/Volatile rather than declared volatile, which would raise CS0420
    // when passed by ref under warnings-as-errors. Every context builds its own initial state
    // because caches live on the state: one shared empty instance would let contexts contaminate
    // each other.
    private ContextState _state = new(ImmutableArray<object>.Empty);

    // Serializes mutators; never held on a query path.
    private readonly object _mutationLock = new();

    // Whether a subject was ever anchored to this context while it had no lifecycle. Such a root is
    // invisible to a lifecycle registered behind it: nothing ever admits it to the ownership graph,
    // so it stays attached and unowned forever. Registering the lifecycle is what refuses that
    // order. A flag rather than a count of live attachments, because the question is about the
    // order the context was configured in and not about how many subjects are on it.
    private bool _wasAttachedWithoutLifecycle;

    /// <inheritdoc cref="_wasAttachedWithoutLifecycle"/>
    internal void MarkAttachedWithoutLifecycle()
    {
        // Read before writing, so building many subjects on one lifecycle-free context does not
        // dirty this line once per construction.
        if (!Volatile.Read(ref _wasAttachedWithoutLifecycle))
        {
            Volatile.Write(ref _wasAttachedWithoutLifecycle, true);
        }
    }

    /// <inheritdoc cref="_wasAttachedWithoutLifecycle"/>
    internal bool WasAttachedWithoutLifecycle => Volatile.Read(ref _wasAttachedWithoutLifecycle);

    /// <summary>
    /// Restricts construction to <see cref="Create"/> because attachment tracking requires context
    /// reference identity.
    /// </summary>
    private InterceptorSubjectContext()
    {
    }

    /// <summary>
    /// Creates a new interceptor subject context.
    /// </summary>
    /// <returns>The newly created context.</returns>
    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ImmutableArray<TInterface> GetServices<TInterface>()
    {
        return GetServicesFromState<TInterface>(Volatile.Read(ref _state));
    }

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists)
    {
        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);

            // Computed from the pinned state, which is atomic against concurrent mutators of this
            // context because they all serialize on _mutationLock.
            if (ComputeServices<TService>(state).Any(exists))
            {
                return false;
            }

            var service = factory();

            // The factory may reenter this context (Monitor is reentrant) and publish, so re-read
            // the state to not lose it. Mutating a different context from here is forbidden, see
            // the remarks on IInterceptorSubjectContext.TryAddService.
            state = Volatile.Read(ref _state);

            // Validated against the re-read state, so a contract a reentrant factory published
            // cannot be doubled by the factory's own product.
            ValidateSingletonContracts(state.Services, service);
            PublishState(new ContextState(state.Services.Add(service!)));
        }

        return true;
    }

    public void AddService<TService>(TService service)
    {
        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            ValidateSingletonContracts(state.Services, service);
            PublishState(new ContextState(state.Services.Add(service!)));
        }
    }

    /// <summary>
    /// Throws when the service implements a singleton contract another registered service (or the
    /// same instance, registered again) already reserves. Runs under <see cref="_mutationLock"/>
    /// and before the publish, so a rejected registration leaves the context untouched.
    /// </summary>
    private static void ValidateSingletonContracts(ImmutableArray<object> services, object? service)
    {
        if (service is null)
        {
            return;
        }

        var contracts = GetSingletonContracts(service.GetType());
        if (contracts.Length == 0)
        {
            return;
        }

        foreach (var contract in contracts)
        {
            foreach (var existingService in services)
            {
                if (contract.IsInstanceOfType(existingService))
                {
                    throw CreateSingletonContractConflictException(contract, existingService, service);
                }
            }
        }
    }

    private static Type[] GetSingletonContracts(Type implementationType)
    {
        return SingletonContractsByImplementationType.GetOrAdd(implementationType, static type =>
        {
            List<Type>? contracts = null;
            foreach (var interfaceType in type.GetInterfaces())
            {
                if (interfaceType.IsGenericType &&
                    interfaceType.GetGenericTypeDefinition() == typeof(ISingletonContextService<>))
                {
                    (contracts ??= []).Add(interfaceType);
                }
            }

            return contracts?.ToArray() ?? Type.EmptyTypes;
        });
    }

    private static InvalidOperationException CreateSingletonContractConflictException(
        Type contract, object existingService, object offendingService)
    {
        var contractType = contract.GetGenericArguments()[0];
        return new InvalidOperationException(
            $"Cannot add service '{offendingService.GetType().FullName}': the singleton contract " +
            $"'{contractType.FullName}' is already reserved by service " +
            $"'{existingService.GetType().FullName}' registered on this context.");
    }

    public TInterface? TryGetService<TInterface>()
    {
        return TryGetServiceFromState<TInterface>(PinState());
    }

    /// <summary>
    /// Pins the current state snapshot with a single volatile read. A caller that must make
    /// several decisions against one consistent view of the context (the structural write's
    /// routing and its chain) pins once and passes the snapshot to the FromState members.
    /// </summary>
    internal ContextState PinState()
    {
        return Volatile.Read(ref _state);
    }

    /// <summary>
    /// <see cref="TryGetService{TInterface}"/> against a pinned snapshot instead of the current
    /// state.
    /// </summary>
    internal TInterface? TryGetServiceFromState<TInterface>(ContextState state)
    {
        var services = GetServicesFromState<TInterface>(state);
        return services.Length switch
        {
            1 => services[0],
            0 => default,
            _ => throw new InvalidOperationException($"There must be exactly one service of type {typeof(TInterface).FullName}.")
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TProperty ExecuteInterceptedRead<TProperty>(ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> readValue)
    {
        var state = Volatile.Read(ref _state);
        var function = GetReadInterceptorFunction<TProperty>(state);
        return function(ref context, readValue);
    }

    /// <summary>
    /// Runs the write chain. <paramref name="propertyTypeIndex"/> is
    /// <see cref="PropertyTypeIndex{TProperty}.Value"/>, read once by the executor's unified
    /// entry and threaded down so this path never touches the generic statics again.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExecuteInterceptedWrite<TProperty>(int propertyTypeIndex, ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        ExecuteInterceptedWrite(Volatile.Read(ref _state), propertyTypeIndex, ref context, writeValue);
    }

    /// <summary>
    /// <see cref="ExecuteInterceptedWrite{TProperty}(int, ref PropertyWriteContext{TProperty}, Action{IInterceptorSubject, TProperty})"/>
    /// against a pinned snapshot instead of the current state. The structural write passes the
    /// snapshot its routing decision read, so the chain cannot contain a lifecycle the routing
    /// did not see.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ExecuteInterceptedWrite<TProperty>(ContextState state, int propertyTypeIndex, ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var action = GetWriteInterceptorFunction<TProperty>(state, propertyTypeIndex);
        action(ref context, writeValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? ExecuteInterceptedInvoke(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var state = Volatile.Read(ref _state);
        var function = GetMethodInvocationFunction(state);
        return function(ref context, invokeMethod);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadFunc<TProperty> GetReadInterceptorFunction<TProperty>(ContextState state)
    {
        var cached = state.TryGetReadFunction(PropertyTypeIndex<TProperty>.Value);
        if (cached is not null)
        {
            return (ReadFunc<TProperty>)cached;
        }

        return CreateReadInterceptorFunction<TProperty>(state);
    }

    private ReadFunc<TProperty> CreateReadInterceptorFunction<TProperty>(ContextState state)
    {
        // Services are resolved from the same snapshot that caches the compiled chain, so a
        // registration (which publishes a new state with fresh caches) can never keep a chain
        // that misses an interceptor.
        var readInterceptors = GetServicesFromState<IReadInterceptor>(state);
        var function = ReadInterceptorFactory<TProperty>.Create(readInterceptors);
        state.SetReadFunction(PropertyTypeIndex<TProperty>.Value, function);
        return function;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WriteAction<TProperty> GetWriteInterceptorFunction<TProperty>(ContextState state, int propertyTypeIndex)
    {
        var cached = state.TryGetWriteFunction(propertyTypeIndex);
        if (cached is not null)
        {
            return (WriteAction<TProperty>)cached;
        }

        return CreateWriteInterceptorFunction<TProperty>(state, propertyTypeIndex);
    }

    private WriteAction<TProperty> CreateWriteInterceptorFunction<TProperty>(ContextState state, int propertyTypeIndex)
    {
        var writeInterceptors = GetServicesFromState<IWriteInterceptor>(state);
        var action = WriteInterceptorFactory<TProperty>.Create(writeInterceptors);
        state.SetWriteFunction(propertyTypeIndex, action);
        return action;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private InvokeFunc GetMethodInvocationFunction(ContextState state)
    {
        var cached = state.MethodInvocationFunction;
        if (cached is not null)
        {
            return (InvokeFunc)cached;
        }

        return CreateMethodInvocationFunction(state);
    }

    private InvokeFunc CreateMethodInvocationFunction(ContextState state)
    {
        var methodInterceptors = GetServicesFromState<IMethodInterceptor>(state);
        var function = MethodInvocationFactory.Create(methodInterceptors);

        // A single slot, so returning the CAS winner is free. The read and write paths return what
        // they built instead, which is equivalent: racers compile from the same state's services,
        // so their chains differ only in identity.
        return (InvokeFunc)state.GetOrSetMethodInvocationFunction(function);
    }

    /// <summary>
    /// Resolves services from the given pinned snapshot. The cache entry is computed from the same
    /// snapshot that owns the cache, so a registration (which publishes a new state) can never
    /// receive a stale entry.
    /// </summary>
    private ImmutableArray<TInterface> GetServicesFromState<TInterface>(ContextState state)
    {
        // An empty context allocates no service cache. The compiled chain arrays are unaffected:
        // an empty state that answers intercepted access still fills those.
        if (state.IsEmpty)
        {
            return ImmutableArray<TInterface>.Empty;
        }

        var serviceCache = state.GetServiceCache();
        if (serviceCache.TryGetValue(typeof(TInterface), out var cachedServices))
        {
            return (ImmutableArray<TInterface>)cachedServices;
        }

        var services = ComputeServices<TInterface>(state);

        // The two-argument GetOrAdd canonicalizes racing computations without a closure allocation.
        return (ImmutableArray<TInterface>)serviceCache.GetOrAdd(typeof(TInterface), services);
    }

    /// <summary>
    /// Filters the pinned snapshot's services by the queried type, drops duplicate registrations
    /// keeping the first occurrence, and reorders by the ordering attributes
    /// (<see cref="ServiceOrderResolver.OrderByDependencies{T}"/> keeps input order among services
    /// no attribute separates).
    /// </summary>
    private static ImmutableArray<TInterface> ComputeServices<TInterface>(ContextState state)
    {
        var services = state.Services;
        List<object>? matching = null;
        foreach (var service in services)
        {
            if (service is TInterface)
            {
                (matching ??= []).Add(service);
            }
        }

        if (matching is null)
        {
            return ImmutableArray<TInterface>.Empty;
        }

        if (matching.Count > 1)
        {
            // Registration keeps insertion order and tolerates duplicate references, so dedup
            // happens here, keeping the first occurrence under the default comparer.
            var distinctServices = new HashSet<object>();
            matching.RemoveAll(service => !distinctServices.Add(service));
        }

        var ordered = ServiceOrderResolver.OrderByDependencies(matching.ToArray());
        var result = ImmutableArray.CreateBuilder<TInterface>(ordered.Length);
        foreach (var service in ordered)
        {
            result.Add((TInterface)service);
        }

        return result.MoveToImmutable();
    }

    /// <summary>
    /// Mutators publish under <see cref="_mutationLock"/>, no CAS loop, so none can lose another's
    /// registration. The published state carries no caches, so a query pinning it recomputes from
    /// post-mutation services.
    /// </summary>
    private void PublishState(ContextState state)
    {
        Volatile.Write(ref _state, state);
    }

    internal sealed class ContextState
    {
        // Insertion order is kept and duplicate references tolerated: dedup lives in the service
        // computation, which keeps the first occurrence under the default comparer. The walk
        // filters by the queried type first, so two services that compare equal while only the
        // later matches that type resolve to it.
        internal readonly ImmutableArray<object> Services;

        // Caches belong to the state that produced them, created lazily via CAS. A registration
        // publishes a new state, so a late insert lands in a state no later query pins.
        private ConcurrentDictionary<Type, object>? _serviceCache; // stores ImmutableArray<T> boxed
        private Delegate? _methodInvocationFunction;

        // Indexed by PropertyTypeIndex, grown by replacing the array.
        private Delegate?[]? _readFunctions;
        private Delegate?[]? _writeFunctions;

        internal ContextState(ImmutableArray<object> services)
        {
            Services = services;
        }

        internal bool IsEmpty => Services.IsEmpty;

        internal Delegate? MethodInvocationFunction
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref _methodInvocationFunction);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ConcurrentDictionary<Type, object> GetServiceCache()
        {
            return Volatile.Read(ref _serviceCache) ?? InitializeServiceCache();
        }

        private ConcurrentDictionary<Type, object> InitializeServiceCache()
        {
            // The CAS keeps one canonical dictionary when two readers race the first use.
            var created = new ConcurrentDictionary<Type, object>();
            return Interlocked.CompareExchange(ref _serviceCache, created, null) ?? created;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Delegate? TryGetReadFunction(int propertyTypeIndex)
        {
            return TryGetFunction(ref _readFunctions, propertyTypeIndex);
        }

        internal void SetReadFunction(int propertyTypeIndex, Delegate function)
        {
            SetFunction(ref _readFunctions, propertyTypeIndex, function);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Delegate? TryGetWriteFunction(int propertyTypeIndex)
        {
            return TryGetFunction(ref _writeFunctions, propertyTypeIndex);
        }

        internal void SetWriteFunction(int propertyTypeIndex, Delegate function)
        {
            SetFunction(ref _writeFunctions, propertyTypeIndex, function);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Delegate? TryGetFunction(ref Delegate?[]? functions, int propertyTypeIndex)
        {
            // Read plainly to avoid the array element address helper on the hot path. Managed
            // reference reads are atomic and safely published, so a concurrent fill can only be
            // missed (costing one rebuild), never observed partially.
            var current = Volatile.Read(ref functions);
            return current is not null && propertyTypeIndex < current.Length
                ? current[propertyTypeIndex]
                : null;
        }

        /// <summary>
        /// Stores a compiled chain, growing the array when the index is beyond it. A store lost to
        /// a concurrent growth costs the next caller one recompilation, which is what a caller
        /// losing the race already does.
        /// </summary>
        private static void SetFunction(ref Delegate?[]? functions, int propertyTypeIndex, Delegate function)
        {
            while (true)
            {
                var current = Volatile.Read(ref functions);
                if (current is not null && propertyTypeIndex < current.Length)
                {
                    Interlocked.CompareExchange(ref current[propertyTypeIndex], function, null);
                    return;
                }

                // Doubled rather than sized to the index, which would reallocate once per property
                // type in a process that has many.
                var grown = new Delegate?[Math.Max(propertyTypeIndex + 1, (current?.Length ?? 0) * 2)];

                // CopyTo can miss a slot filled concurrently; that entry is rebuilt on next use.
                current?.CopyTo(grown, 0);
                grown[propertyTypeIndex] = function;

                if (ReferenceEquals(Interlocked.CompareExchange(ref functions, grown, current), current))
                {
                    return;
                }
            }
        }

        internal Delegate GetOrSetMethodInvocationFunction(Delegate function)
        {
            return Interlocked.CompareExchange(ref _methodInvocationFunction, function, null) ?? function;
        }
    }
}
