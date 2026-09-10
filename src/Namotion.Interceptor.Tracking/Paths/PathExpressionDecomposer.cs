using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// Decomposes a subscription path expression (<c>x =&gt; x.Child.Children[2].Name</c>) into an
/// ordered list of <see cref="PathSegment"/>s and enforces the static tier of the validation
/// boundary: expression shapes that can never become valid throw here at subscribe time. Index and
/// dictionary-key argument expressions are evaluated exactly once during decomposition.
/// </summary>
internal static class PathExpressionDecomposer
{
    internal static PathSegment[] Decompose<TSubject, TValue>(Expression<Func<TSubject, TValue>> path)
    {
        var parameter = path.Parameters[0];

        // Step 1: unwrap only the compiler's sanctioned leaf convert on the body: a boxing or
        // reference-widening conversion (int -> object, concrete -> interface, derived -> base),
        // recognised by the target type being assignable from the operand type. Any other convert
        // (numeric narrowing/widening, reference downcast, or a convert deeper in the chain) is a
        // user cast and falls through to the cast rejection in the walk below.
        var current = path.Body;
        if (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } boxing
            && boxing.Type.IsAssignableFrom(boxing.Operand.Type))
        {
            current = boxing.Operand;
        }

        // Steps 2-6: walk from the outermost member/index down to the lambda parameter, collecting
        // segments leaf-first. The leaf property segment is marked here; index segments never are, so
        // a path ending in an index yields no leaf and is rejected afterwards.
        var segments = new List<PathSegment>();
        while (current != parameter)
        {
            switch (current)
            {
                case MemberExpression member:
                    current = ConsumeMember(member, segments, parameter);
                    break;

                case MethodCallExpression call:
                    current = ConsumeIndexer(call, segments, parameter);
                    break;

                case BinaryExpression { NodeType: ExpressionType.ArrayIndex } arrayIndex:
                    current = ConsumeArrayIndex(arrayIndex, segments, parameter);
                    break;

                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked }:
                    throw Invalid(path,
                        "a cast is not supported inside a path; only property access and single-argument " +
                        "indexers on the lambda parameter are allowed, for example x => x.Child.Children[2].Name.");

                default:
                    throw Invalid(path,
                        $"the path does not start from the lambda parameter (found a '{current.NodeType}' node); " +
                        "captured objects, static members, and constants are not allowed, for example x => other.Name.");
            }
        }

        if (segments.Count == 0)
        {
            throw Invalid(path, "the identity path 'x => x' does not select a property.");
        }

        // The first-collected segment is the outermost (leaf). It carries IsLeaf only when it is a
        // property; an index there means the path ends in an indexed element, which is rejected.
        if (!segments[0].IsLeaf)
        {
            throw Invalid(path,
                $"the path ends in an indexed element on '{segments[0].PropertyName}'; append a property, " +
                "for example x => x.Children[2].Name.");
        }

        // Step 7: every intermediate (non-leaf) segment must resolve to a subject-typed value so the
        // walk can descend into it at runtime.
        for (var i = 1; i < segments.Count; i++)
        {
            var segment = segments[i];
            var intermediateType = GetIntermediateType(segment);
            if (!intermediateType.IsSubjectReferenceType())
            {
                throw Invalid(path,
                    $"the intermediate segment '{segment.PropertyName}' resolves to '{intermediateType.Name}', " +
                    "which is not a subject type; only subjects (or collections/dictionaries of subjects) can be traversed.");
            }
        }

        // Collected leaf-first; reverse so the root is first and the leaf (IsLeaf) is last.
        segments.Reverse();
        return segments.ToArray();
    }

    private static Expression ConsumeMember(MemberExpression member, List<PathSegment> segments, ParameterExpression parameter)
    {
        if (member.Member is not PropertyInfo property)
        {
            if (member.Member is FieldInfo)
            {
                // Closure captures compile to a field access on a ConstantExpression holding the
                // display-class instance, so the path does not start from the lambda parameter, for
                // example x => other.Name. Any other field access is a genuine (rejected) field
                // selector, whether on the parameter (x.PlainField) or reached through it
                // (x.Child.PlainField).
                throw member.Expression is ConstantExpression
                    ? Invalid(parameter,
                        $"the path does not start from the lambda parameter; the captured object '{member.Member.Name}' cannot be used, for example x => other.Name.")
                    : Invalid(parameter,
                        $"'{member.Member.Name}' is a field, not a property; only property access is supported.");
            }

            throw Invalid(parameter,
                $"the path member '{member.Member.Name}' is not a property.");
        }

        if (member.Expression is null)
        {
            throw Invalid(parameter, $"the static member '{property.Name}' cannot be used in a path.");
        }

        segments.Add(new PathSegment
        {
            PropertyName = property.Name,
            Kind = PathSegmentKind.Property,
            PropertyStaticType = property.PropertyType,
            IsLeaf = segments.Count == 0
        });

        return member.Expression;
    }

    private static Expression ConsumeIndexer(MethodCallExpression call, List<PathSegment> segments, ParameterExpression parameter)
    {
        // Multi-dimensional array access (x.Grid[1, 2]) compiles to a synthesized Get(int, int, ...)
        // method call rather than a get_Item indexer; name the receiver and the shape explicitly.
        if (call.Object?.Type.IsArray == true && call.Method.Name == "Get")
        {
            throw Invalid(parameter,
                $"the multi-dimensional array indexer on '{DescribeReceiver(call.Object)}' is not supported; " +
                "only single-argument indexers on single-dimensional collections are allowed.");
        }

        if (call.Method.Name != "get_Item")
        {
            throw Invalid(parameter,
                $"the method call '{call.Method.Name}' is not supported; only a single-argument indexer (get_Item) is allowed.");
        }

        if (call.Arguments.Count != 1)
        {
            throw Invalid(parameter,
                $"the multi-argument indexer on '{DescribeReceiver(call.Object)}' is not supported; only single-argument indexers are allowed.");
        }

        if (call.Object is not MemberExpression receiver)
        {
            throw Invalid(parameter,
                "a nested indexer (an indexer applied to another indexer's result) is not supported; " +
                "the indexer receiver must be a collection or dictionary property, for example x => x.Children[2].Name.");
        }

        return ConsumeIndexedReceiver(receiver, call.Arguments[0], segments, parameter);
    }

    private static Expression ConsumeArrayIndex(BinaryExpression arrayIndex, List<PathSegment> segments, ParameterExpression parameter)
    {
        if (arrayIndex.Left is not MemberExpression receiver)
        {
            throw Invalid(parameter,
                "a nested indexer (an index applied to another indexer's result) is not supported; " +
                "the indexer receiver must be a collection property, for example x => x.Children[2].Name.");
        }

        return ConsumeIndexedReceiver(receiver, arrayIndex.Right, segments, parameter);
    }

    private static Expression ConsumeIndexedReceiver(
        MemberExpression receiver, Expression argument, List<PathSegment> segments, ParameterExpression parameter)
    {
        if (receiver.Member is not PropertyInfo property)
        {
            throw Invalid(parameter,
                $"the indexer receiver '{receiver.Member.Name}' is not a property; only collection and dictionary properties can be indexed.");
        }

        if (receiver.Expression is null)
        {
            throw Invalid(parameter, $"the static member '{property.Name}' cannot be indexed in a path.");
        }

        var receiverType = property.PropertyType;
        var isCollection = receiverType.IsSubjectCollectionType();
        var isDictionary = receiverType.IsSubjectDictionaryType();
        if (!isCollection && !isDictionary)
        {
            throw Invalid(parameter,
                $"the property '{property.Name}' of type '{receiverType.Name}' is indexed but is neither a subject collection nor a subject dictionary.");
        }

        if (ReferencesParameter(argument, parameter))
        {
            throw Invalid(parameter,
                $"the index argument of '{property.Name}' references the lambda parameter; the index must be a constant or captured value, for example x => x.Children[2].Name.");
        }

        // Evaluate the index/key exactly once, now.
        var argumentValue = Expression.Lambda(argument).Compile().DynamicInvoke();

        if (isCollection)
        {
            if (argumentValue is not int index)
            {
                throw Invalid(parameter,
                    $"the collection index of '{property.Name}' must be an int, but evaluated to '{argumentValue ?? "null"}'.");
            }

            if (index < 0)
            {
                throw Invalid(parameter, $"the collection index of '{property.Name}' is negative ({index}).");
            }

            var isValueTypedCollection = PathTypeResolver.IsClosedImmutableArray(receiverType);
            var (interfaceType, elementType) = PathTypeResolver.ResolveCollectionTypes(receiverType);
            segments.Add(new PathSegment
            {
                PropertyName = property.Name,
                Kind = PathSegmentKind.CollectionIndex,
                PropertyStaticType = receiverType,
                CollectionIndex = index,
                IsValueTypedCollection = isValueTypedCollection,
                CollectionInterfaceType = isValueTypedCollection ? null : interfaceType,
                CollectionElementType = elementType
            });
        }
        else
        {
            if (argumentValue is null)
            {
                throw Invalid(parameter, $"the dictionary key of '{property.Name}' evaluated to null.");
            }

            var (interfaceType, keyType, valueType) = PathTypeResolver.ResolveDictionaryTypes(property, receiverType, parameter);
            segments.Add(new PathSegment
            {
                PropertyName = property.Name,
                Kind = PathSegmentKind.DictionaryKey,
                PropertyStaticType = receiverType,
                DictionaryKey = argumentValue,
                DictionaryInterfaceType = interfaceType,
                DictionaryKeyType = keyType,
                DictionaryValueType = valueType
            });
        }

        return receiver.Expression;
    }

    private static Type GetIntermediateType(PathSegment segment)
    {
        return segment.Kind switch
        {
            PathSegmentKind.Property => segment.PropertyStaticType,
            PathSegmentKind.CollectionIndex => PathTypeResolver.GetCollectionElementType(segment.PropertyStaticType),
            PathSegmentKind.DictionaryKey => segment.DictionaryValueType!,
            _ => segment.PropertyStaticType
        };
    }

    private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
    {
        var finder = new ParameterReferenceFinder(parameter);
        finder.Visit(expression);
        return finder.Found;
    }

    private static string DescribeReceiver(Expression? receiver)
        => receiver is MemberExpression member ? member.Member.Name : "an indexer";

    private static ArgumentException Invalid<TSubject, TValue>(Expression<Func<TSubject, TValue>> path, string reason)
        => new($"The subscription path '{path}' is not supported: {reason}", nameof(path));

    private static ArgumentException Invalid(ParameterExpression parameter, string reason)
        => new($"The subscription path on '{parameter.Name}' is not supported: {reason}", "path");

    private sealed class ParameterReferenceFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
            {
                Found = true;
            }

            return base.VisitParameter(node);
        }
    }
}
