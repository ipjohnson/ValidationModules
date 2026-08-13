using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>Questions about a member's type that both front-ends and the emitter need answered.</summary>
public static class TypeFacts {

    /// <summary>
    /// The element type of a collection, or null if it is not one. Strings are deliberately not
    /// collections here: <c>string</c> implements <c>IEnumerable&lt;char&gt;</c>, and treating one
    /// as a collection would turn a length constraint into a per-character walk.
    /// </summary>
    /// <summary>
    /// The value type of a dictionary, or null if it is not one.
    /// </summary>
    /// <remarks>
    /// Checked before <see cref="ElementTypeOf"/>, because every dictionary is also an
    /// IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt; - and taking that reading emitted a call to a
    /// KeyValuePairValidator that does not exist and never could, so the consumer's build broke in
    /// generated code.
    /// </remarks>
    public static (ITypeSymbol Key, ITypeSymbol Value)? DictionaryTypesOf(ITypeSymbol type) {
        foreach (var candidate in Interfaces(type)) {
            if (candidate.ConstructedFrom.SpecialType is SpecialType.None &&
                candidate.ConstructedFrom.ToDisplayString() is
                    "System.Collections.Generic.IDictionary<TKey, TValue>" or
                    "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>") {
                return (candidate.TypeArguments[0], candidate.TypeArguments[1]);
            }
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> Interfaces(ITypeSymbol type) {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Interface } self) {
            yield return self;
        }

        foreach (var candidate in type.AllInterfaces) {
            yield return candidate;
        }
    }

    public static ITypeSymbol? ElementTypeOf(ITypeSymbol type) {
        if (type.SpecialType == SpecialType.System_String) {
            return null;
        }

        if (type is IArrayTypeSymbol array) {
            return array.ElementType;
        }

        if (type is INamedTypeSymbol { IsGenericType: true } named &&
            named.ConstructedFrom.ToDisplayString().StartsWith("System.Collections.Generic.IEnumerable<", StringComparison.Ordinal)) {
            return named.TypeArguments[0];
        }

        foreach (var candidate in type.AllInterfaces) {
            if (candidate.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) {
                return candidate.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the count of a collection can be read without enumerating it. Anything that reaches
    /// the emitter without a Count or Length is walked with a foreach instead, so an
    /// IEnumerable-only property still validates rather than being silently skipped.
    /// </summary>
    public static bool HasCount(ITypeSymbol type) {
        if (type is IArrayTypeSymbol) {
            return true;
        }

        return type.GetMembers("Count").Any(m => m is IPropertySymbol) ||
               type.AllInterfaces.Any(i =>
                   i.ConstructedFrom.SpecialType is SpecialType.System_Collections_Generic_ICollection_T
                       or SpecialType.System_Collections_Generic_IReadOnlyCollection_T);
    }

    public static string CountAccessor(ITypeSymbol type) => type is IArrayTypeSymbol ? "Length" : "Count";

    /// <summary>
    /// Whether the elements can be reached by index rather than through an enumerator.
    /// </summary>
    /// <remarks>
    /// This is not a micro-optimization. `foreach` over an interface-typed collection calls
    /// IEnumerable&lt;T&gt;.GetEnumerator(), which boxes List&lt;T&gt;'s struct enumerator - so a
    /// clean validation pass over a collection property would allocate, which is the one thing the
    /// runtime promises it does not do. A for loop over an indexer does not.
    /// </remarks>
    public static bool IsIndexable(ITypeSymbol type) {
        if (type is IArrayTypeSymbol) {
            return true;
        }

        if (type.GetMembers("this[]").Any(m => m is IPropertySymbol { Parameters.Length: 1 }) && HasCount(type)) {
            return true;
        }

        return type.AllInterfaces.Any(i =>
            i.ConstructedFrom.SpecialType is SpecialType.System_Collections_Generic_IList_T
                or SpecialType.System_Collections_Generic_IReadOnlyList_T);
    }

    public static bool IsNullableValueType(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    /// <summary>Whether a [Range] can be emitted as a pair of comparisons against this type.</summary>
    public static bool IsOrdered(ITypeSymbol type) {
        var underlying = IsNullableValueType(type)
            ? ((INamedTypeSymbol)type).TypeArguments[0]
            : type;

        switch (underlying.SpecialType) {
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_DateTime:
                return true;
        }

        return underlying.ToDisplayString() is "System.DateOnly" or "System.TimeOnly" or
            "System.TimeSpan" or "System.DateTimeOffset";
    }

    public static bool IsValidRegex(string pattern, out string error) {
        try {
            _ = new Regex(pattern);
            error = string.Empty;
            return true;
        } catch (ArgumentException exception) {
            error = exception.Message;
            return false;
        }
    }
}
