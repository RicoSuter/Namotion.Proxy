using System.Reflection;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// The private state of <see cref="InterceptorSubjectContext"/>, reached by reflection and shared
/// by the context tests that need it. The copy on write invariants are only observable on these
/// fields: that a mutation installs a fresh state object, and that the compiled chain caches
/// belong to the state that produced them. Asserting those through the public API alone would mean
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

    /// <summary>Returns the state object currently installed on the given context.</summary>
    internal static object GetState(InterceptorSubjectContext context)
    {
        return StateField.GetValue(context)!;
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
}
