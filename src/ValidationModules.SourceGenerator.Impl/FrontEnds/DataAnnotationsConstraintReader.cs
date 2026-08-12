using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Reads a <c>System.ComponentModel.DataAnnotations</c> attribute into the IR.
/// </summary>
/// <remarks>
/// The attribute is never constructed and <c>IsValid</c> is never called - its arguments are read
/// out of metadata and compiled. See API-SURFACE.md §18 for the full mapping, and §18.4 for the two
/// DataAnnotations behaviours deliberately not reproduced.
/// </remarks>
public static class DataAnnotationsConstraintReader {

    public readonly record struct Outcome(ConstraintModel? Constraint, DiagnosticDescriptor? Diagnostic);

    private static readonly string[] Constraints = {
        "RequiredAttribute", "StringLengthAttribute", "LengthAttribute", "MinLengthAttribute",
        "MaxLengthAttribute", "RangeAttribute", "RegularExpressionAttribute",
        "AllowedValuesAttribute", "DeniedValuesAttribute",
    };

    private static readonly string[] FormatValidators = {
        "EmailAddressAttribute", "PhoneAttribute", "UrlAttribute", "CreditCardAttribute",
        "Base64StringAttribute", "FileExtensionsAttribute",
    };

    public static bool IsConstraint(string attributeName) => Array.IndexOf(Constraints, attributeName) >= 0;

    public static Outcome Read(AttributeData attribute, string attributeName, IPropertySymbol property) {
        switch (attributeName) {
            case "RequiredAttribute":
                return new Outcome(
                    new ConstraintModel(
                        ConstraintKind.Required,
                        Message: NativeConstraintReader.Named(attribute, "ErrorMessage") as string,
                        AllowEmptyStrings: NativeConstraintReader.Named(attribute, "AllowEmptyStrings") is bool allow && allow),
                    null);

            case "StringLengthAttribute": {
                var max = First(attribute) ?? int.MaxValue.ToString();
                var min = NativeConstraintReader.Named(attribute, "MinimumLength") is int m ? m.ToString() : "0";
                return new Outcome(Bounded(ConstraintKind.StringLength, attribute, min, max), null);
            }

            case "LengthAttribute": {
                var args = attribute.ConstructorArguments;
                var min = args.Length > 0 ? NativeConstraintReader.Literal(args[0]) : "0";
                var max = args.Length > 1 ? NativeConstraintReader.Literal(args[1]) : int.MaxValue.ToString();
                return Sized(attribute, property, min, max);
            }

            // Both apply to strings and to collections in DataAnnotations, so the member's type
            // decides which constraint this becomes. A member that is neither is VM0064.
            case "MinLengthAttribute":
                return Sized(attribute, property, First(attribute) ?? "0", int.MaxValue.ToString());

            case "MaxLengthAttribute":
                return Sized(attribute, property, "0", First(attribute) ?? int.MaxValue.ToString());

            case "RangeAttribute": {
                var args = attribute.ConstructorArguments;
                if (args.Length < 2) {
                    return default;
                }

                // The (Type, string, string) overload puts the bounds in positions 1 and 2.
                var minIndex = args.Length == 3 ? 1 : 0;

                return new Outcome(
                    new ConstraintModel(
                        ConstraintKind.Range,
                        Message: NativeConstraintReader.Named(attribute, "ErrorMessage") as string,
                        Min: NativeConstraintReader.Literal(args[minIndex]),
                        Max: NativeConstraintReader.Literal(args[minIndex + 1]),
                        ExclusiveMin: NativeConstraintReader.Named(attribute, "MinimumIsExclusive") is bool exMin && exMin,
                        ExclusiveMax: NativeConstraintReader.Named(attribute, "MaximumIsExclusive") is bool exMax && exMax),
                    null);
            }

            case "RegularExpressionAttribute": {
                if (attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not string pattern) {
                    return default;
                }

                // Anchored, unlike the native [Pattern]. DataAnnotations checks that the match
                // starts at 0 and consumes the whole value; the native attribute follows JSON
                // Schema, which does not. See API-SURFACE.md §18.3.
                return new Outcome(
                    new ConstraintModel(
                        ConstraintKind.Pattern,
                        Message: NativeConstraintReader.Named(attribute, "ErrorMessage") as string,
                        Pattern: pattern,
                        Anchored: true),
                    null);
            }

            case "AllowedValuesAttribute":
            case "DeniedValuesAttribute": {
                var values = attribute.ConstructorArguments.Length == 1 &&
                             attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
                    ? attribute.ConstructorArguments[0].Values.Select(NativeConstraintReader.Literal).ToImmutableArray()
                    : ImmutableArray<string>.Empty;

                return new Outcome(
                    new ConstraintModel(
                        ConstraintKind.AllowedValues,
                        Message: NativeConstraintReader.Named(attribute, "ErrorMessage") as string,
                        Values: new EquatableArray<string>(values),
                        Negated: attributeName == "DeniedValuesAttribute"),
                    null);
            }

            case "CompareAttribute":
                return new Outcome(null, ValidationDiagnostics.CrossFieldAttribute);

            case "CustomValidationAttribute":
                return new Outcome(null, ValidationDiagnostics.CustomValidationAttribute);

            default:
                return Array.IndexOf(FormatValidators, attributeName) >= 0
                    ? new Outcome(null, ValidationDiagnostics.FormatValidatorAttribute)
                    : default;
        }
    }

    private static Outcome Sized(AttributeData attribute, IPropertySymbol property, string min, string max) {
        if (property.Type.SpecialType == SpecialType.System_String) {
            return new Outcome(Bounded(ConstraintKind.StringLength, attribute, min, max), null);
        }

        if (TypeFacts.ElementTypeOf(property.Type) is not null) {
            return new Outcome(Bounded(ConstraintKind.ItemCount, attribute, min, max), null);
        }

        return new Outcome(null, ValidationDiagnostics.LengthOnUnsupportedMember);
    }

    private static ConstraintModel Bounded(ConstraintKind kind, AttributeData attribute, string min, string max) =>
        new(kind,
            Message: NativeConstraintReader.Named(attribute, "ErrorMessage") as string,
            Min: min,
            Max: max);

    private static string? First(AttributeData attribute) =>
        attribute.ConstructorArguments.Length > 0
            ? NativeConstraintReader.Literal(attribute.ConstructorArguments[0])
            : null;
}
