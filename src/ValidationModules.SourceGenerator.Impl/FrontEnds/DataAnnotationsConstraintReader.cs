using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Reads a <c>System.ComponentModel.DataAnnotations</c> attribute into the IR.
/// </summary>
/// <remarks>
/// The attribute is never constructed and <c>IsValid</c> is never called - its arguments are read
/// out of metadata and compiled. Two DataAnnotations behaviours are deliberately not reproduced.
/// </remarks>
public static class DataAnnotationsConstraintReader {

    /// <param name="Constraint">The constraint read, when the attribute maps to one.</param>
    /// <param name="Diagnostic">A diagnostic to report beside it, when there is news.</param>
    /// <param name="Detail">
    /// The third format argument the diagnostic's message wants, when it wants one - the member's
    /// type for VM0064, the exact compiled semantics for VM0063. The front end falls back to the
    /// VM0060 enforce tail when this is null, which is the argument every other reader diagnostic
    /// ignores.
    /// </param>
    public readonly record struct Outcome(
        ConstraintModel? Constraint, DiagnosticDescriptor? Diagnostic, string? Detail = null);

    private static readonly string[] Constraints = {
        "RequiredAttribute", "StringLengthAttribute", "LengthAttribute", "MinLengthAttribute",
        "MaxLengthAttribute", "RangeAttribute", "RegularExpressionAttribute",
        "AllowedValuesAttribute", "DeniedValuesAttribute",
    };

    private static readonly string[] FormatValidators = {
        "EmailAddressAttribute", "PhoneAttribute", "UrlAttribute", "CreditCardAttribute",
        "Base64StringAttribute", "FileExtensionsAttribute",
    };

    /// <summary>
    /// <c>FileExtensionsAttribute</c>'s default set, verbatim - "Default file extensions match
    /// those from jquery validate", per its source.
    /// </summary>
    private const string DefaultFileExtensions = "png,jpg,jpeg,gif";

    /// <summary>
    /// Whether the attribute reads as a constraint here - including the format validators, which
    /// compile like any other constraint and so count wherever "is this enforced" is the question:
    /// VM0010 under Ignore, and VM0051 on a record parameter.
    /// </summary>
    public static bool IsConstraint(string attributeName) =>
        Array.IndexOf(Constraints, attributeName) >= 0 ||
        Array.IndexOf(FormatValidators, attributeName) >= 0;

    public static Outcome Read(AttributeData attribute, string attributeName, ITypeSymbol memberType) =>
        FinishMessage(ReadCore(attribute, attributeName, memberType), attribute, attributeName);

    private static Outcome ReadCore(AttributeData attribute, string attributeName, ITypeSymbol memberType) {
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
                return Sized(attribute, memberType, min, max);
            }

            // Both apply to strings and to collections in DataAnnotations, so the member's type
            // decides which constraint this becomes. A member that is neither is VM0064.
            case "MinLengthAttribute":
                return Sized(attribute, memberType, First(attribute) ?? "0", int.MaxValue.ToString());

            case "MaxLengthAttribute":
                return Sized(attribute, memberType, "0", First(attribute) ?? int.MaxValue.ToString());

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
                // Schema, which does not.
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
                return CustomValidation(attribute, memberType);

            // The format validators compile to the BCL's own checks - semantics in
            // ConstraintChecks, parity pinned by its tests. Each carries VM0063 (Info) stating
            // exactly what was emitted, because the checks are looser than the attribute names
            // suggest and an author who wants more should hear it where they typed the attribute.
            case "EmailAddressAttribute":
                return Format(ConstraintKind.Email, attribute, memberType,
                    "the value must contain exactly one '@', neither first nor last, and no line " +
                    "breaks - 'a@b' passes, as RFC 5322 permits");

            case "PhoneAttribute":
                return Format(ConstraintKind.Phone, attribute, memberType,
                    "'+' signs are stripped, a trailing extension ('ext.', 'ext' or 'x' plus " +
                    "digits) is removed, and what remains must contain a digit and only digits, " +
                    "whitespace and '-.()'");

            case "UrlAttribute":
                return Format(ConstraintKind.Url, attribute, memberType,
                    IsUri(memberType)
                        ? "the Uri must be absolute with scheme http, https or ftp"
                        : "the value must start with 'http://', 'https://' or 'ftp://' " +
                          "(case-insensitive); nothing past the prefix is checked");

            case "CreditCardAttribute":
                return Format(ConstraintKind.CreditCard, attribute, memberType,
                    "the digits (spaces and dashes allowed) must pass the Luhn mod-10 checksum");

            case "Base64StringAttribute":
                return Format(ConstraintKind.Base64, attribute, memberType,
                    "the value must be well-formed Base64, as Convert.FromBase64String reads it");

            case "FileExtensionsAttribute": {
                // Normalized at build time exactly as the attribute normalizes its Extensions
                // property - spaces and dots removed, lowercased invariantly, split on commas,
                // dot-prefixed - so its quirks survive: "tar.gz" reads as ".targz" in both.
                var raw = NativeConstraintReader.Named(attribute, "Extensions") as string;
                var extensions = (string.IsNullOrWhiteSpace(raw) ? DefaultFileExtensions : raw!)
                    .Replace(" ", string.Empty)
                    .Replace(".", string.Empty)
                    .ToLowerInvariant()
                    .Split(',')
                    .Select(extension => "." + extension)
                    .ToImmutableArray();

                var constraint = new ConstraintModel(
                    ConstraintKind.FileExtension,
                    Message: NativeConstraintReader.Named(attribute, "ErrorMessage") as string,
                    Values: new EquatableArray<string>(
                        extensions.Select(e => SymbolDisplay.FormatLiteral(e, quote: true)).ToImmutableArray()),
                    ValueDisplays: new EquatableArray<string>(extensions));

                return memberType.SpecialType == SpecialType.System_String
                    ? new Outcome(constraint, ValidationDiagnostics.FormatValidatorCompiled,
                        "the file name's extension must be one of " +
                        $"{string.Join(", ", extensions)} (case-insensitive)")
                    : new Outcome(constraint, null);
            }

            default:
                return default;
        }
    }

    /// <summary>
    /// A format validator's outcome: the constraint, and - only when the member's type can carry
    /// it - the VM0063 Info stating the compiled semantics. On any other type the constraint
    /// still flows, so the applicability check drops it with VM0001 and the Info does not talk
    /// over the error.
    /// </summary>
    private static Outcome Format(
        ConstraintKind kind, AttributeData attribute, ITypeSymbol memberType, string semantics) {

        var constraint = new ConstraintModel(
            kind, Message: NativeConstraintReader.Named(attribute, "ErrorMessage") as string);

        var fits = memberType.SpecialType == SpecialType.System_String ||
            (kind == ConstraintKind.Url && IsUri(memberType));

        return fits
            ? new Outcome(constraint, ValidationDiagnostics.FormatValidatorCompiled, semantics)
            : new Outcome(constraint, null);
    }

    /// <summary>
    /// Whether the member is <c>System.Uri</c>, ignoring nullable annotation - which
    /// <c>ToDisplayString</c> would not.
    /// </summary>
    internal static bool IsUri(ITypeSymbol type) =>
        type is INamedTypeSymbol {
            Name: "Uri",
            ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true },
        };

    /// <summary>
    /// Resolves <c>[CustomValidation(typeof(T), "Method")]</c> to the static method the emitter
    /// will call directly, or to VM0080 with the reason it cannot.
    /// </summary>
    /// <remarks>
    /// The accepted signatures are DataAnnotations' own - public static, returning
    /// <c>ValidationResult</c>, taking the value alone or the value and a
    /// <c>ValidationContext</c> - with one deliberate narrowing, recorded on the descriptor: the
    /// value parameter must accept the member's type or be <c>object</c>, because the runtime
    /// string-conversion fallback <c>CustomValidationAttribute</c> performs is a conversion this
    /// library will not do silently.
    /// </remarks>
    private static Outcome CustomValidation(AttributeData attribute, ITypeSymbol memberType) {
        var args = attribute.ConstructorArguments;

        if (args.Length != 2 ||
            args[0].Value is not INamedTypeSymbol provider ||
            args[1].Value is not string methodName) {
            return new Outcome(null, ValidationDiagnostics.CustomValidationTargetUnusable,
                "its arguments are not a validator type and a method name");
        }

        IMethodSymbol? candidate = null;

        foreach (var member in provider.GetMembers(methodName)) {
            if (member is IMethodSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } method &&
                method.Parameters.Length is 1 or 2) {
                candidate = method;
                break;
            }
        }

        if (candidate is null) {
            return new Outcome(null, ValidationDiagnostics.CustomValidationTargetUnusable,
                $"'{provider.ToDisplayString()}.{methodName}' is not a public static method " +
                "taking one or two parameters");
        }

        if (candidate.ReturnType.ToDisplayString() is not
            ("System.ComponentModel.DataAnnotations.ValidationResult"
            or "System.ComponentModel.DataAnnotations.ValidationResult?")) {
            return new Outcome(null, ValidationDiagnostics.CustomValidationTargetUnusable,
                $"'{provider.ToDisplayString()}.{methodName}' does not return ValidationResult");
        }

        if (candidate.Parameters.Length == 2 &&
            candidate.Parameters[1].Type.ToDisplayString() !=
                "System.ComponentModel.DataAnnotations.ValidationContext") {
            return new Outcome(null, ValidationDiagnostics.CustomValidationTargetUnusable,
                $"'{provider.ToDisplayString()}.{methodName}' has a second parameter that is not " +
                "a ValidationContext");
        }

        if (!Accepts(candidate.Parameters[0].Type, memberType)) {
            return new Outcome(null, ValidationDiagnostics.CustomValidationTargetUnusable,
                $"'{provider.ToDisplayString()}.{methodName}' takes " +
                $"'{candidate.Parameters[0].Type.ToDisplayString()}', which cannot accept this " +
                "member without DataAnnotations' runtime string conversion; take the member's " +
                "type, or object");
        }

        var qualified = provider.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new Outcome(
            new ConstraintModel(
                ConstraintKind.CustomValidationMethod,
                CustomAccessor: $"{qualified}.{methodName}",
                CustomTakesContext: candidate.Parameters.Length == 2),
            null);
    }

    /// <summary>
    /// Whether the member's value can be passed where <paramref name="parameter"/> is declared:
    /// identity, a base type, an implemented interface, or <c>object</c>. The member's type is
    /// compared as declared - a <c>int?</c> member needs a parameter that takes <c>int?</c> or
    /// <c>object</c>, never bare <c>int</c>, because the emitted call passes the property straight
    /// through and has no null to hide.
    /// </summary>
    private static bool Accepts(ITypeSymbol parameter, ITypeSymbol memberType) {
        if (parameter.SpecialType == SpecialType.System_Object) {
            return true;
        }

        var comparer = SymbolEqualityComparer.Default;

        for (ITypeSymbol? current = memberType; current is not null; current = current.BaseType) {
            if (comparer.Equals(parameter, current)) {
                return true;
            }
        }

        foreach (var contract in memberType.AllInterfaces) {
            if (comparer.Equals(parameter, contract)) {
                return true;
            }
        }

        return false;
    }

    private static Outcome Sized(AttributeData attribute, ITypeSymbol memberType, string min, string max) {
        if (memberType.SpecialType == SpecialType.System_String) {
            return new Outcome(Bounded(ConstraintKind.StringLength, attribute, min, max), null);
        }

        if (TypeFacts.ElementTypeOf(memberType) is not null) {
            return new Outcome(Bounded(ConstraintKind.ItemCount, attribute, min, max), null);
        }

        return new Outcome(null, ValidationDiagnostics.LengthOnUnsupportedMember, memberType.ToDisplayString());
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

    /// <summary>
    /// Finishes a mapped constraint's message the way DataAnnotations' own formatting would have:
    /// an <c>ErrorMessage</c> template gets every placeholder but <c>{0}</c> baked in - they are
    /// all compile-time constants - and a resx-backed message becomes an accessor the emitter
    /// wraps in a per-render provider. Both paths mark the message as DataAnnotations-dialect so
    /// the emitter substitutes <c>{0}</c> with the display name, which is DataAnnotations'
    /// meaning for it, not this library's <c>{field}</c>.
    /// </summary>
    /// <remarks>
    /// The placeholder order is each attribute's own <c>FormatErrorMessage</c> order, which is not
    /// this model's Min/Max order everywhere: <c>[StringLength]</c> formats <c>{1}</c> as the
    /// maximum and <c>{2}</c> as the minimum, while <c>[Range]</c> and <c>[Length]</c> put the
    /// minimum first. Encoded here, beside the reader that knows which attribute it read, because
    /// after normalization into the model the original order is gone.
    /// </remarks>
    private static Outcome FinishMessage(Outcome outcome, AttributeData attribute, string attributeName) {
        if (outcome.Constraint is not { } constraint ||
            constraint.Kind == ConstraintKind.CustomValidationMethod) {
            return outcome;
        }

        if (constraint.Message is { } message) {
            return outcome with {
                Constraint = constraint with {
                    Message = BakeComposite(message, attribute, attributeName),
                    DataAnnotationsMessage = true,
                },
            };
        }

        // An explicit ErrorMessage wins over the resource pair, which is DataAnnotations' own
        // precedence; reaching here means there was none.
        if (NativeConstraintReader.Named(attribute, "ErrorMessageResourceName") is string resourceName &&
            NativeConstraintReader.Named(attribute, "ErrorMessageResourceType") is INamedTypeSymbol resourceType) {
            return outcome with {
                Constraint = constraint with {
                    MessageResourceAccessor =
                        $"{resourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{resourceName}",
                    MessageResourceArgs = new EquatableArray<string>(FormatArgumentLiterals(attribute, attributeName)),
                    DataAnnotationsMessage = true,
                },
            };
        }

        return outcome;
    }

    /// <summary>
    /// Replaces <c>{1}</c>…<c>{9}</c> with the attribute's own format arguments, rendered as text.
    /// <c>{0}</c> - the display name - survives for the emitter, which knows it.
    /// </summary>
    private static string BakeComposite(string message, AttributeData attribute, string attributeName) {
        var arguments = FormatArgumentValues(attribute, attributeName);

        for (var i = 0; i < arguments.Length; i++) {
            var value = arguments[i];
            message = message.Replace(
                $"{{{i + 1}}}",
                value is IFormattable formattable
                    ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                    : value?.ToString() ?? string.Empty);
        }

        return message;
    }

    /// <summary>
    /// The values behind <c>{1}</c>…, in the declaring attribute's <c>FormatErrorMessage</c> order.
    /// </summary>
    private static ImmutableArray<object?> FormatArgumentValues(AttributeData attribute, string attributeName) {
        var args = attribute.ConstructorArguments;

        return attributeName switch {
            // (name, MaximumLength, MinimumLength) - max first, unlike this model.
            "StringLengthAttribute" => ImmutableArray.Create(
                args.Length > 0 ? args[0].Value : null,
                NativeConstraintReader.Named(attribute, "MinimumLength") is int m ? m : (object?)0),
            "MinLengthAttribute" or "MaxLengthAttribute" =>
                ImmutableArray.Create(args.Length > 0 ? args[0].Value : null),
            "LengthAttribute" => ImmutableArray.Create(
                args.Length > 0 ? args[0].Value : null,
                args.Length > 1 ? args[1].Value : null),
            "RangeAttribute" when args.Length == 3 => ImmutableArray.Create(args[1].Value, args[2].Value),
            "RangeAttribute" when args.Length == 2 => ImmutableArray.Create(args[0].Value, args[1].Value),
            "RegularExpressionAttribute" =>
                ImmutableArray.Create(args.Length > 0 ? args[0].Value : null),
            _ => ImmutableArray<object?>.Empty,
        };
    }

    /// <summary>
    /// The same arguments as C# constant expressions, for the emitted provider-backed info whose
    /// resx template fills <c>{1}</c>… at render time.
    /// </summary>
    private static ImmutableArray<string> FormatArgumentLiterals(AttributeData attribute, string attributeName) {
        var args = attribute.ConstructorArguments;

        return attributeName switch {
            "StringLengthAttribute" => ImmutableArray.Create(
                args.Length > 0 ? NativeConstraintReader.Literal(args[0]) : "0",
                NativeConstraintReader.Named(attribute, "MinimumLength") is int m ? m.ToString() : "0"),
            "MinLengthAttribute" or "MaxLengthAttribute" =>
                ImmutableArray.Create(args.Length > 0 ? NativeConstraintReader.Literal(args[0]) : "0"),
            "LengthAttribute" => ImmutableArray.Create(
                args.Length > 0 ? NativeConstraintReader.Literal(args[0]) : "0",
                args.Length > 1 ? NativeConstraintReader.Literal(args[1]) : "0"),
            "RangeAttribute" when args.Length == 3 => ImmutableArray.Create(
                NativeConstraintReader.Literal(args[1]),
                NativeConstraintReader.Literal(args[2])),
            "RangeAttribute" when args.Length == 2 => ImmutableArray.Create(
                NativeConstraintReader.Literal(args[0]),
                NativeConstraintReader.Literal(args[1])),
            "RegularExpressionAttribute" =>
                ImmutableArray.Create(args.Length > 0 ? NativeConstraintReader.Literal(args[0]) : "\"\""),
            _ => ImmutableArray<string>.Empty,
        };
    }
}
