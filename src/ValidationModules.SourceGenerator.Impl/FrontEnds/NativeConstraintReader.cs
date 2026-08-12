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
                if (args.Length != 2) {
                    return null;
                }

                return common with {
                    Kind = ConstraintKind.Range,
                    Min = Literal(args[0]),
                    Max = Literal(args[1]),
                    ExclusiveMin = Named(attribute, "ExclusiveMin") is bool exMin && exMin,
                    ExclusiveMax = Named(attribute, "ExclusiveMax") is bool exMax && exMax,
                };
            }

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
                var values = attribute.ConstructorArguments.Length == 1 &&
                             attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
                    ? attribute.ConstructorArguments[0].Values.Select(Literal).ToImmutableArray()
                    : ImmutableArray<string>.Empty;

                return common with {
                    Kind = ConstraintKind.AllowedValues,
                    Values = new EquatableArray<string>(values),
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

    internal static object? Named(AttributeData attribute, string name) {
        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == name) {
                return argument.Value.Value;
            }
        }

        return null;
    }

    /// <summary>Renders a constant as the C# literal the emitter will write.</summary>
    internal static string Literal(TypedConstant constant) => constant.Value switch {
        null => "null",
        string text => SymbolDisplay.FormatLiteral(text, quote: true),
        bool flag => flag ? "true" : "false",
        char character => SymbolDisplay.FormatLiteral(character, quote: true),
        double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        float number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
        decimal number => number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
        var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };
}
