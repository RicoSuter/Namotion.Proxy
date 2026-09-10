using System.Reflection;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

using static Namotion.Interceptor.Tests.Context.ContextStateReflection;

namespace Namotion.Interceptor.Tests.Context;

public class ContextFunctionCacheTests
{
    private static readonly MethodInfo ExerciseFunctionCachesMethod = typeof(ContextFunctionCacheTests)
        .GetMethod(nameof(ExerciseFunctionCaches), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{nameof(ExerciseFunctionCaches)} was renamed, this test needs updating.");

    [Fact]
    public async Task WhenManyPropertyTypesGrowFunctionArraysConcurrently_ThenEveryEntryCanBeRebuilt()
    {
        // Arrange: nested generic types provide distinct runtime property types without adding a
        // source-generated property for every slot. They race both arrays on one state.
        const int propertyTypeCount = 32;
        var propertyTypes = CreatePropertyTypes(propertyTypeCount);
        var context = InterceptorSubjectContext.Create();
        var subject = new ContextProbeSubject(context);
        var executor = Assert.IsType<InterceptorExecutor>(((IInterceptorSubject)subject).Executor);

        using var ready = new CountdownEvent(propertyTypeCount);
        using var start = new ManualResetEventSlim(false);
        var builders = propertyTypes
            .Select(propertyType => Task.Factory.StartNew(() =>
            {
                ready.Signal();
                start.Wait();
                ExerciseFunctionCachesMethod.MakeGenericMethod(propertyType).Invoke(null, [executor]);
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        // Act: a growth may copy an array while another thread fills an element in the old one. A
        // lost element is allowed, so repeat each access after the race to force its rebuild.
        ready.Wait();
        start.Set();
        await AsyncTestHelpers.WaitUntilAsync(() => builders.All(builder => builder.IsCompleted),
            message: "Concurrent property function cache builders did not finish");
        await Task.WhenAll(builders);

        foreach (var propertyType in propertyTypes)
        {
            ExerciseFunctionCachesMethod.MakeGenericMethod(propertyType).Invoke(null, [executor]);
        }

        // Assert
        var readFunctions = GetReadFunctions(context);
        var writeFunctions = GetWriteFunctions(context);
        Assert.NotNull(readFunctions);
        Assert.NotNull(writeFunctions);

        foreach (var propertyType in propertyTypes)
        {
            var propertyTypeIndex = GetPropertyTypeIndex(propertyType);
            Assert.NotNull(readFunctions[propertyTypeIndex]);
            Assert.NotNull(writeFunctions[propertyTypeIndex]);
        }
    }

    [Fact]
    public void WhenFreshContextFirstSeesHighPropertyTypeIndex_ThenItsArrayCoversThatIndex()
    {
        // Arrange: assign a run of process-wide indices, then let a fresh context see only the last
        // one. Its array is intentionally indexed by the largest global index, not its local count.
        const int propertyTypeCount = 16;
        var propertyTypes = CreateHighIndexPropertyTypes(propertyTypeCount);
        var propertyTypeIndices = propertyTypes.Select(GetPropertyTypeIndex).ToArray();
        var highPropertyType = propertyTypes[^1];
        var highPropertyTypeIndex = propertyTypeIndices[^1];
        var context = InterceptorSubjectContext.Create();
        var subject = new ContextProbeSubject(context);
        var executor = Assert.IsType<InterceptorExecutor>(((IInterceptorSubject)subject).Executor);

        // Act
        ExerciseFunctionCachesMethod.MakeGenericMethod(highPropertyType).Invoke(null, [executor]);

        // Assert
        var readFunctions = Assert.IsType<Delegate?[]>(GetReadFunctions(context));
        var writeFunctions = Assert.IsType<Delegate?[]>(GetWriteFunctions(context));
        Assert.True(readFunctions.Length > highPropertyTypeIndex);
        Assert.True(writeFunctions.Length > highPropertyTypeIndex);
        Assert.Single(readFunctions, function => function is not null);
        Assert.Single(writeFunctions, function => function is not null);
    }

    [Fact]
    public void WhenMethodInvocationFunctionIsAlreadySet_ThenTheCanonicalFunctionIsReturned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        Action first = static () => { };
        Action second = static () => { };

        // Act
        var firstResult = GetOrSetMethodInvocationFunction(context, first);
        var secondResult = GetOrSetMethodInvocationFunction(context, second);

        // Assert
        Assert.Same(first, firstResult);
        Assert.Same(first, secondResult);
    }

    [Fact]
    public async Task WhenMethodInvocationFunctionIsSetConcurrently_ThenEveryCallerObservesOneWinner()
    {
        // Arrange: the single slot is filled by a CAS, so unlike the read and write arrays every
        // racing caller has to come back with the winner rather than with what it brought.
        const int callerCount = 32;
        var context = InterceptorSubjectContext.Create();
        var candidates = Enumerable
            .Range(0, callerCount)
            .Select(index => (Delegate)(Action)(() => GC.KeepAlive(index)))
            .ToArray();

        // The closures capture index, so these are distinct instances rather than one cached lambda.
        Assert.Equal(callerCount, candidates.Distinct().Count());

        var results = new Delegate[callerCount];
        using var ready = new CountdownEvent(callerCount);
        using var start = new ManualResetEventSlim(false);
        var callers = candidates
            .Select((candidate, index) => Task.Factory.StartNew(() =>
            {
                ready.Signal();
                start.Wait();
                results[index] = GetOrSetMethodInvocationFunction(context, candidate);
            }, TaskCreationOptions.LongRunning))
            .ToArray();

        // Act
        ready.Wait();
        start.Set();
        await AsyncTestHelpers.WaitUntilAsync(() => callers.All(caller => caller.IsCompleted),
            message: "Concurrent method invocation function callers did not finish");
        await Task.WhenAll(callers);

        // Assert
        Assert.Single(results.Distinct());
        Assert.Contains(results[0], candidates);
    }

    [Fact]
    public void WhenFunctionArrayGrowsPastItsLength_ThenItDoublesInsteadOfSizingToTheIndex()
    {
        // Arrange: a run of indices, so the two used below are far enough from zero that doubling
        // the first array overshoots the second index. That is what makes the assertion below
        // distinguish doubling from sizing exactly to the index.
        var propertyTypes = CreateDoublingPropertyTypes(16);
        var propertyTypeIndices = propertyTypes.Select(GetPropertyTypeIndex).ToArray();
        var firstIndex = propertyTypeIndices[^2];
        var secondIndex = propertyTypeIndices[^1];
        Assert.True(secondIndex > firstIndex, "The property type indices are not increasing.");
        Assert.True(secondIndex + 1 < (firstIndex + 1) * 2,
            $"Index {secondIndex} is beyond double of {firstIndex}, so this cannot tell the two sizing rules apart.");

        var context = InterceptorSubjectContext.Create();
        var subject = new ContextProbeSubject(context);
        var executor = Assert.IsType<InterceptorExecutor>(((IInterceptorSubject)subject).Executor);

        // Act: the first fill sizes the array to its own index, the second has to grow it.
        ExerciseFunctionCachesMethod.MakeGenericMethod(propertyTypes[^2]).Invoke(null, [executor]);
        var lengthAfterFirst = Assert.IsType<Delegate?[]>(GetReadFunctions(context)).Length;
        ExerciseFunctionCachesMethod.MakeGenericMethod(propertyTypes[^1]).Invoke(null, [executor]);

        // Assert
        Assert.Equal(firstIndex + 1, lengthAfterFirst);
        Assert.Equal(lengthAfterFirst * 2, Assert.IsType<Delegate?[]>(GetReadFunctions(context)).Length);
        Assert.Equal(lengthAfterFirst * 2, Assert.IsType<Delegate?[]>(GetWriteFunctions(context)).Length);
    }

    /// <summary>
    /// Pins the invariant the cache design is derived from: a mutation installs a fresh state
    /// object, so a compiled chain or service query cached on the previous state can never serve
    /// the new topology. A registration that kept the same state object when it carried no caches
    /// would pass every other test here.
    /// </summary>
    [Fact]
    public void WhenServiceIsAdded_ThenTheStateObjectIsReplaced()
    {
        // Arrange: no query, so the state carries no caches and is the one a mutation could be
        // tempted to keep.
        var context = InterceptorSubjectContext.Create();
        var stateBefore = GetState(context);

        // Act
        context.AddService(new object());

        // Assert
        Assert.NotSame(stateBefore, GetState(context));
    }

    private static Type[] CreatePropertyTypes(int count)
    {
        return CreatePropertyTypes(count, typeof(PropertyType<>), typeof(PropertyTypeRoot));
    }

    private static Type[] CreateDoublingPropertyTypes(int count)
    {
        return CreatePropertyTypes(count, typeof(DoublingPropertyType<>), typeof(DoublingPropertyTypeRoot));
    }

    private static Type[] CreateHighIndexPropertyTypes(int count)
    {
        return CreatePropertyTypes(count, typeof(HighIndexPropertyType<>), typeof(HighIndexPropertyTypeRoot));
    }

    private static Type[] CreatePropertyTypes(int count, Type openGenericType, Type rootType)
    {
        var result = new Type[count];
        var propertyType = rootType;
        for (var index = 0; index < count; index++)
        {
            propertyType = openGenericType.MakeGenericType(propertyType);
            result[index] = propertyType;
        }

        return result;
    }

    private static void ExerciseFunctionCaches<TProperty>(InterceptorExecutor executor)
    {
        _ = executor.GetPropertyValue<TProperty>(nameof(ContextProbeSubject.Value), static _ => default!);
        executor.SetPropertyValue<TProperty>(
            nameof(ContextProbeSubject.Value),
            default!,
            default!,
            static (_, _) => { });
    }

    private sealed class PropertyType<TProperty>;

    private sealed class PropertyTypeRoot;

    private sealed class HighIndexPropertyType<TProperty>;

    private sealed class HighIndexPropertyTypeRoot;

    private sealed class DoublingPropertyType<TProperty>;

    private sealed class DoublingPropertyTypeRoot;
}

[InterceptorSubject]
public partial class ContextProbeSubject
{
    public partial int Value { get; set; }

    public partial string? Text { get; set; }

    protected int EchoWithoutInterceptor(int input) => input;
}
