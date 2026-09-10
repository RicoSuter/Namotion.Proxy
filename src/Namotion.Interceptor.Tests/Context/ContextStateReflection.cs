using System.Collections.Immutable;
using System.Reflection;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// The private state of <see cref="InterceptorSubjectContext"/>, reached by reflection and shared
/// by the context tests that need it. Several invariants of the copy on write design are only
/// observable on these fields: that a state object is installed exactly once, that a recorded chain
/// end belongs to the state which recorded it, and that a traversal buffer is dropped rather than
/// kept once it grows past its threshold. Asserting those through the public API alone would mean
/// asserting nothing.
///
/// Each lookup raises with the name it was looking for, so a rename fails with the field to fix
/// rather than with a null reference somewhere in a test body.
/// </summary>
internal static class ContextStateReflection
{
    private static readonly Type ContextStateType = typeof(InterceptorSubjectContext)
        .GetNestedType("ContextState", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("InterceptorSubjectContext.ContextState was renamed, the context tests need updating.");

    internal static readonly FieldInfo StateField = typeof(InterceptorSubjectContext)
        .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("InterceptorSubjectContext._state was renamed, the context tests need updating.");

    internal static readonly FieldInfo ResolvedTerminalField = ContextStateType
        .GetField("_resolvedTerminal", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ContextState._resolvedTerminal was renamed, the context tests need updating.");

    private static readonly FieldInfo FallbackContextsField = ContextStateType
        .GetField("FallbackContexts", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ContextState.FallbackContexts was renamed, the context tests need updating.");

    private static readonly FieldInfo ReadFunctionsField = ContextStateType
        .GetField("_readFunctions", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ContextState._readFunctions was renamed, the context tests need updating.");

    private static readonly FieldInfo WriteFunctionsField = ContextStateType
        .GetField("_writeFunctions", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ContextState._writeFunctions was renamed, the context tests need updating.");

    private static readonly MethodInfo GetOrSetMethodInvocationFunctionMethod = ContextStateType
        .GetMethod("GetOrSetMethodInvocationFunction", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ContextState.GetOrSetMethodInvocationFunction was renamed, the context tests need updating.");

    private static readonly Type PropertyTypeIndexType = typeof(InterceptorSubjectContext)
        .GetNestedType("PropertyTypeIndex`1", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("InterceptorSubjectContext.PropertyTypeIndex was renamed, the context tests need updating.");

    internal static readonly object CyclicDelegationMarker = typeof(InterceptorSubjectContext)
        .GetField("CyclicDelegationMarker", BindingFlags.Static | BindingFlags.NonPublic)
        ?.GetValue(null)
        ?? throw new InvalidOperationException("InterceptorSubjectContext.CyclicDelegationMarker was renamed, the context tests need updating.");

    /// <summary>Returns the state object currently installed on the given context.</summary>
    internal static object GetState(InterceptorSubjectContext context)
    {
        return StateField.GetValue(context)!;
    }

    /// <summary>
    /// Returns what the installed state records about the end of its delegation chain: the context
    /// the chain ends on, <see cref="CyclicDelegationMarker"/> when it runs in a circle, or null
    /// when the chain was never walked, which is always legal.
    /// </summary>
    internal static object? GetResolvedTerminal(InterceptorSubjectContext context)
    {
        return ResolvedTerminalField.GetValue(GetState(context));
    }

    /// <summary>
    /// Returns the fallback contexts the installed state publishes. Typed, so a change to what the
    /// state stores per edge breaks the build here rather than silently reporting no match.
    /// </summary>
    internal static ImmutableArray<InterceptorSubjectContext> GetFallbackContexts(InterceptorSubjectContext context)
    {
        return (ImmutableArray<InterceptorSubjectContext>)FallbackContextsField.GetValue(GetState(context))!;
    }

    /// <summary>Returns whether the installed state publishes an edge to the given fallback.</summary>
    internal static bool HasFallbackContext(InterceptorSubjectContext context, InterceptorSubjectContext fallback)
    {
        return GetFallbackContexts(context).Contains(fallback);
    }

    /// <summary>Returns the read-function array owned by the context's installed state.</summary>
    internal static Delegate?[]? GetReadFunctions(InterceptorSubjectContext context)
    {
        return (Delegate?[]?)ReadFunctionsField.GetValue(GetState(context));
    }

    /// <summary>Returns the write-function array owned by the context's installed state.</summary>
    internal static Delegate?[]? GetWriteFunctions(InterceptorSubjectContext context)
    {
        return (Delegate?[]?)WriteFunctionsField.GetValue(GetState(context));
    }

    /// <summary>Returns the process-wide dense cache index assigned to a property type.</summary>
    internal static int GetPropertyTypeIndex(Type propertyType)
    {
        var valueField = PropertyTypeIndexType
            .MakeGenericType(propertyType)
            .GetField("Value", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PropertyTypeIndex.Value was renamed, the context tests need updating.");

        return (int)valueField.GetValue(null)!;
    }

    /// <summary>Installs or retrieves the state's canonical method-invocation function.</summary>
    internal static Delegate GetOrSetMethodInvocationFunction(InterceptorSubjectContext context, Delegate function)
    {
        return (Delegate)GetOrSetMethodInvocationFunctionMethod.Invoke(GetState(context), [function])!;
    }

    /// <summary>
    /// Returns one of the thread static traversal buffers, read from the calling thread, so that a
    /// test asserts against the buffers of the walk it just ran.
    /// </summary>
    internal static object? GetThreadStaticBuffer(string fieldName)
    {
        var field = typeof(InterceptorSubjectContext)
            .GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"InterceptorSubjectContext.{fieldName} was renamed, the context tests need updating.");

        return field.GetValue(null);
    }
}
