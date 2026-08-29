using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Renders an applied attribute back into the construction expression that builds an identical
/// instance: <c>new global::My.EvenNumberAttribute(2) { ErrorMessage = "…" }</c>.
/// </summary>
/// <remarks>
/// <para>
/// Attribute arguments are compile-time constants by definition, which is what makes this total in
/// practice: every argument is a primitive, a string, an enum, a <c>typeof</c>, or an array of
/// those, and each has a fully qualified spelling that binds in a generated file carrying no using
/// directives. The application's own <i>syntax</i> is deliberately not lifted - it resolves
/// against its file's imports, which is the trap the predicate pipeline solves by moving code and
/// this avoids by rendering values.
/// </para>
/// <para>
/// Null means an argument could not be rendered - a <see cref="TypedConstantKind.Error"/> from a
/// broken compilation - and the caller falls back to reporting the attribute as not enforced
/// rather than emitting code that cannot compile.
/// </para>
/// </remarks>
internal static class AttributeConstructionRenderer {

    public static string? Render(AttributeData attribute) {
        if (attribute.AttributeClass is not { } attributeClass) {
            return null;
        }

        var type = attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var arguments = new List<string>();

        foreach (var argument in attribute.ConstructorArguments) {
            if (Argument(argument) is not { } rendered) {
                return null;
            }

            arguments.Add(rendered);
        }

        var construction = $"new {type}({string.Join(", ", arguments)})";

        if (attribute.NamedArguments.Length == 0) {
            return construction;
        }

        var named = new List<string>();

        foreach (var pair in attribute.NamedArguments) {
            if (Argument(pair.Value) is not { } rendered) {
                return null;
            }

            named.Add($"{pair.Key} = {rendered}");
        }

        return $"{construction} {{ {string.Join(", ", named)} }}";
    }

    private static string? Argument(TypedConstant constant) {
        switch (constant.Kind) {
            case TypedConstantKind.Error:
                return null;

            case TypedConstantKind.Type:
                return constant.Value is ITypeSymbol type
                    ? $"typeof({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})"
                    : null;

            case TypedConstantKind.Array: {
                if (constant.IsNull) {
                    return "null";
                }

                // The element type is written out so a params argument still binds when the array
                // is empty or its elements need the type to disambiguate an overload.
                var element = constant.Type is IArrayTypeSymbol array
                    ? array.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : null;

                if (element is null) {
                    return null;
                }

                var items = new List<string>();

                foreach (var item in constant.Values) {
                    if (Argument(item) is not { } rendered) {
                        return null;
                    }

                    items.Add(rendered);
                }

                return $"new {element}[] {{ {string.Join(", ", items)} }}";
            }

            default:
                return Scalar(constant);
        }
    }

    /// <summary>
    /// A primitive or enum, through the shared literal renderer, plus the integer suffixes it never
    /// needed: a constraint bound is re-parsed against the member's type, where an attribute
    /// argument has to bind the constructor overload the author's source bound.
    /// </summary>
    private static string Scalar(TypedConstant constant) {
        var literal = NativeConstraintReader.Literal(constant);

        return constant.Type?.SpecialType switch {
            SpecialType.System_Int64 => literal + "L",
            SpecialType.System_UInt64 => literal + "UL",
            SpecialType.System_UInt32 => literal + "U",
            _ => literal,
        };
    }
}
