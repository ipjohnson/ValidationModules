using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>Reads a <c>ValidationModules.Constraints</c> attribute into the IR.</summary>
public static class NativeConstraintReader {

    public static ConstraintModel? Read(AttributeData attribute, string attributeName) {
        var common = ReadCommon(attribute);

        switch (attributeName) {
            case "RequiredAttribute":
                return common with {
                    Kind = ConstraintKind.Required,
                    AllowEmptyStrings = Named(attribute, "AllowEmptyStrings") is bool allow && allow,
                };

            case "StringLengthAttribute":
            case "ItemCountAttribute": {
                var kind = attributeName == "StringLengthAttribute" ? ConstraintKind.StringLength : ConstraintKind.ItemCount;
                var (min, max) = ReadBounds(attribute);
                return common with { Kind = kind, Min = min, Max = max };
            }

            case "RangeAttribute": {
                var args = attribute.ConstructorArguments;
                if (args.Length is not (0 or 2)) {
                    return null;
                }

                string? min = null;
                string? max = null;

                if (args.Length == 2) {
                    min = Literal(args[0]);
                    max = Literal(args[1]);
                }

                // Named wins where set, the same arrangement ReadBounds has. A null bound stays
                // null rather than becoming the type's extreme - see RangeAttribute's own remarks.
                if (NamedConstant(attribute, "Min") is { IsNull: false } namedMin) {
                    min = Literal(namedMin);
                }

                if (NamedConstant(attribute, "Max") is { IsNull: false } namedMax) {
                    max = Literal(namedMax);
                }

                return common with {
                    Kind = ConstraintKind.Range,
                    Min = min,
                    Max = max,
                    ExclusiveMin = Named(attribute, "ExclusiveMin") is bool exMin && exMin,
                    ExclusiveMax = Named(attribute, "ExclusiveMax") is bool exMax && exMax,
                };
            }

            case "MultipleOfAttribute": {
                var args = attribute.ConstructorArguments;
                if (args.Length != 1) {
                    return null;
                }

                // Carried through as written. Resolving it needs the member's type, which is the
                // front end's to supply - the same division of labour [Range] bounds already have.
                return common with { Kind = ConstraintKind.MultipleOf, Divisor = Literal(args[0]) };
            }

            // Presence is the constraint; there is nothing to read.
            case "UniqueItemsAttribute":
                return common with { Kind = ConstraintKind.UniqueItems };

            case "PatternAttribute": {
                var args = attribute.ConstructorArguments;

                // The reference form. The member is resolved and checked in the front end, which
                // has the symbols; here it is only carried through.
                if (args.Length == 2 && args[0].Value is INamedTypeSymbol && args[1].Value is string) {
                    return common with { Kind = ConstraintKind.Pattern };
                }

                if (args.Length != 1 || args[0].Value is not string pattern) {
                    return null;
                }

                return common with {
                    Kind = ConstraintKind.Pattern,
                    Pattern = pattern,
                    Anchored = Named(attribute, "Anchored") is bool anchored && anchored,
                    RegexOptions = Named(attribute, "Options") is int options ? options : 0,
                };
            }

            case "AllowedValuesAttribute": {
                var declared = attribute.ConstructorArguments.Length == 1 &&
                               attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
                    ? attribute.ConstructorArguments[0].Values
                    : ImmutableArray<TypedConstant>.Empty;

                return common with {
                    Kind = ConstraintKind.AllowedValues,
                    Values = new EquatableArray<string>(declared.Select(Literal).ToImmutableArray()),
                    ValueDisplays = new EquatableArray<string>(declared.Select(Display).ToImmutableArray()),
                };
            }

            // ValidateNested carries no check of its own; the property model records it separately.
            default:
                return null;
        }
    }

    private static ConstraintModel ReadCommon(AttributeData attribute) => new(
        ConstraintKind.Required,
        Code: Named(attribute, "Code") as string,
        Message: Named(attribute, "Message") as string);

    private static (string Min, string Max) ReadBounds(AttributeData attribute) {
        // Positional (min, max) and the named Min/Max form are both legal; named wins where set,
        // because the parameterless constructor is what makes declaring only one bound readable.
        var min = "0";
        var max = int.MaxValue.ToString();

        if (attribute.ConstructorArguments.Length == 2) {
            min = Literal(attribute.ConstructorArguments[0]);
            max = Literal(attribute.ConstructorArguments[1]);
        }

        if (Named(attribute, "Min") is int namedMin) {
            min = namedMin.ToString();
        }

        if (Named(attribute, "Max") is int namedMax) {
            max = namedMax.ToString();
        }

        return (min, max);
    }

    /// <summary>
    /// A named argument as its constant, rather than as its value.
    /// </summary>
    /// <remarks>
    /// <see cref="Named"/> unwraps to <c>object?</c>, which is enough for a bool or an int but loses
    /// what <see cref="Literal"/> needs to render a bound: an <c>object</c>-typed <c>Min</c> holding
    /// the string "0.00" and one holding the double 0.0 unwrap to values that render differently.
    /// </remarks>
    internal static TypedConstant? NamedConstant(AttributeData attribute, string name) {
        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == name) {
                return argument.Value;
            }
        }

        return null;
    }

    internal static object? Named(AttributeData attribute, string name) {
        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == name) {
                return argument.Value.Value;
            }
        }

        return null;
    }

    /// <summary>Renders a constant as the C# literal the emitter will write.</summary>
    /// <remarks>
    /// <b>Enums are handled before <see cref="TypedConstant.Value"/> is looked at, and have to be.</b>
    /// An enum constant carries its <i>underlying</i> value there - a boxed <see cref="int"/> in the
    /// usual case - so the scalar switch renders <c>[AllowedValues(Tier.Pro)]</c> as <c>1</c>, and
    /// the emitted <c>value.Plan != 1</c> is CS0019 rather than a comparison. The member has to be
    /// named instead, and fully qualified, because the generated file carries none of the declaring
    /// file's using directives.
    /// </remarks>
    internal static string Literal(TypedConstant constant) =>
        constant.Kind == TypedConstantKind.Enum && constant.Type is not null
            ? EnumLiteral(constant)
            : Scalar(constant);

    /// <summary>The qualified member name for an enum constant - <c>global::My.Tier.Pro</c>.</summary>
    /// <remarks>
    /// A constant with no matching member is legal C# - <c>(Tier)7</c>, or any combination of
    /// <c>[Flags]</c> members - so this falls back to a cast over the underlying value, which
    /// compiles and compares identically.
    /// </remarks>
    private static string EnumLiteral(TypedConstant constant) {
        var type = constant.Type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        foreach (var member in constant.Type.GetMembers()) {
            if (member is IFieldSymbol { HasConstantValue: true } field &&
                Equals(field.ConstantValue, constant.Value)) {
                return type + "." + field.Name;
            }
        }

        return "((" + type + ")" + Scalar(constant) + ")";
    }

    /// <summary>
    /// The display form of a constant, for a message rather than a comparison: the bare enum member
    /// name, and the unquoted text of a string.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Literal"/> because the two genuinely differ. The comparison needs
    /// <c>global::My.Tier.Pro</c> to bind at all; a caller told "must be one of:
    /// global::My.Tier.Pro" has been told less than one told "must be one of: Pro".
    /// </remarks>
    internal static string Display(TypedConstant constant) {
        if (constant.Kind != TypedConstantKind.Enum || constant.Type is null) {
            var scalar = Scalar(constant);

            return scalar.Length >= 2 && scalar[0] == '"'
                ? scalar.Substring(1, scalar.Length - 2)
                : scalar;
        }

        var qualified = EnumLiteral(constant);

        // A value with no member of its own rendered as a cast, and there is no name to show. The
        // underlying number is what a caller would have sent, so it is what the message names -
        // "must be one of: Pro, 7" rather than leaking a cast expression into an error string.
        if (qualified.EndsWith(")", StringComparison.Ordinal)) {
            return Scalar(constant);
        }

        var dot = qualified.LastIndexOf('.');

        return dot >= 0 ? qualified.Substring(dot + 1) : qualified;
    }

    private static string Scalar(TypedConstant constant) => constant.Value switch {
        null => "null",
        string text => SymbolDisplay.FormatLiteral(text, quote: true),
        bool flag => flag ? "true" : "false",
        char character => SymbolDisplay.FormatLiteral(character, quote: true),
        double number => double.IsNaN(number) || double.IsInfinity(number)
            ? NonFinite("double", number)
            : number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        float number => float.IsNaN(number) || float.IsInfinity(number)
            ? NonFinite("float", number)
            : number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
        decimal number => number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
        var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    /// <summary>
    /// The named form of a non-finite bound. <c>double.NegativeInfinity</c> is a constant, so it is
    /// a legal attribute argument, but <c>"R"</c> renders it "-Infinity" - which is not an
    /// expression, so the bound reached the emitter and generated code failed to compile.
    /// </summary>
    private static string NonFinite(string type, double value) =>
        double.IsNaN(value) ? type + ".NaN"
        : value > 0 ? type + ".PositiveInfinity"
        : type + ".NegativeInfinity";
}
