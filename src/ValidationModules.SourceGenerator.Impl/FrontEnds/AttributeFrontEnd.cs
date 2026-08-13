using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Turns a type's attributes into a <see cref="ValidatedTypeModel"/>.
/// </summary>
/// <remarks>
/// Reads both vocabularies - <c>ValidationModules.Constraints</c> and
/// <c>System.ComponentModel.DataAnnotations</c> - into the same IR, so a rule's origin stops
/// mattering the moment it is read. DataAnnotations attributes are never instantiated or invoked;
/// their arguments are read out of metadata at build time, which is what keeps the result free of
/// the reflection <c>Validator.TryValidateObject</c> would otherwise do.
/// </remarks>
public sealed class AttributeFrontEnd {
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly bool _compileDataAnnotations;
    private readonly Func<string, string> _fieldNamer;
    private readonly PatternPolicy _patternPolicy;

    public AttributeFrontEnd(bool compileDataAnnotations, Func<string, string> fieldNamer, PatternPolicy patternPolicy) {
        _compileDataAnnotations = compileDataAnnotations;
        _fieldNamer = fieldNamer;
        _patternPolicy = patternPolicy;
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Builds the model for <paramref name="type"/>, or null when nothing about it asks for a
    /// validator.
    /// </summary>
    /// <param name="type">The candidate type.</param>
    /// <param name="validatorNameFor">
    /// Resolves the validator name for a nested type, so a property's model can name the validator
    /// it will call before that type has itself been processed.
    /// </param>
    /// <param name="declared">
    /// Rules read out of a rules class targeting this type, if any. They merge with the attributes
    /// rather than replacing them, and land after them on each property - which is §19.7's ordering,
    /// and the reason both live in one model rather than in two validators for one type.
    /// </param>
    /// <param name="applied">Hand-written rules attached with <c>rules.Apply</c>.</param>
    public ValidatedTypeModel? Build(
        INamedTypeSymbol type,
        Func<INamedTypeSymbol, string> validatorNameFor,
        IReadOnlyList<DeclaredRule>? declared = null,
        IReadOnlyList<string>? applied = null) {

        var properties = ImmutableArray.CreateBuilder<ValidatedPropertyModel>();
        var order = new List<int>();
        var sawAnything = HasGenerateValidator(type) || declared is { Count: > 0 } || applied is { Count: > 0 };
        var sawAttribute = false;

        foreach (var member in type.GetMembers()) {
            if (member is not IPropertySymbol property || property.IsStatic || property.IsIndexer) {
                continue;
            }

            var constraints = ReadConstraints(property);
            var validateNested = HasValidateNested(property);
            string? overriddenField = null;

            sawAttribute |= constraints.Count > 0 || validateNested;

            if (declared is not null) {
                foreach (var rule in declared) {
                    if (!SymbolEqualityComparer.Default.Equals(rule.Property, property)) {
                        continue;
                    }

                    overriddenField ??= rule.Field;

                    if (rule.Constraint is not null) {
                        constraints.Add(rule.Constraint);
                    }

                    validateNested |= rule.Nesting != Nesting.None;
                }
            }

            if (constraints.Count == 0 && !validateNested) {
                continue;
            }

            sawAnything = true;

            if (property.GetMethod is null || property.GetMethod.DeclaredAccessibility == Accessibility.Private) {
                Report(ValidationDiagnostics.InaccessibleProperty, property, property.Name);
                continue;
            }

            properties.Add(BuildProperty(property, constraints, validateNested, validatorNameFor, overriddenField));
            order.Add(FirstMentionOf(property, declared));
        }

        if (!sawAnything) {
            return null;
        }

        if (ImplementsValidatableObject(type)) {
            Report(ValidationDiagnostics.ValidatableObjectNotCompiled, type, type.Name);
        }

        // The validator lives where the type lives, global namespace included. Parking those in a
        // namespace of ours made PetValidator unfindable from the file that declared Pet, and put a
        // consumer's types inside ValidationModules.
        var ns = type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : type.ContainingNamespace.ToDisplayString();

        return new ValidatedTypeModel(
            ns,
            type.Name,
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            validatorNameFor(type),
            new EquatableArray<ValidatedPropertyModel>(Ordered(properties.ToImmutable(), order, sawAttribute)),
            new EquatableArray<string>(ImmutableArray.CreateRange(applied ?? Array.Empty<string>())));
    }

    /// <summary>
    /// Reorders a rules-only type's properties into the order its Describe body first mentioned
    /// them.
    /// </summary>
    /// <remarks>
    /// §4.2 pins errors to property order, and for an attributed type that is source order because
    /// source order is where the rules were written. A rules class writes them somewhere else, and a
    /// body that constrains Notes before Start must report in that order or it disagrees with
    /// <c>DescribedValidator&lt;T&gt;</c>, which has only the body to go on and cannot see source
    /// order without reflection.
    ///
    /// Only when the type carries no attributes of its own. Mixing the two orderings on one type
    /// would be worse than either: source order stays authoritative the moment source is involved.
    /// </remarks>
    private static ImmutableArray<ValidatedPropertyModel> Ordered(
        ImmutableArray<ValidatedPropertyModel> properties, List<int> order, bool sawAttribute) {

        if (sawAttribute || properties.Length < 2) {
            return properties;
        }

        return properties
            .Select((property, index) => (property, key: order[index], index))
            .OrderBy(entry => entry.key)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.property)
            .ToImmutableArray();
    }

    /// <summary>Where a property is first constrained by a rules class, or int.MaxValue.</summary>
    private static int FirstMentionOf(IPropertySymbol property, IReadOnlyList<DeclaredRule>? declared) {
        if (declared is null) {
            return int.MaxValue;
        }

        for (var i = 0; i < declared.Count; i++) {
            if (SymbolEqualityComparer.Default.Equals(declared[i].Property, property)) {
                return i;
            }
        }

        return int.MaxValue;
    }

    private ValidatedPropertyModel BuildProperty(
        IPropertySymbol property,
        List<ConstraintModel> constraints,
        bool validateNested,
        Func<INamedTypeSymbol, string> validatorNameFor,
        string? overriddenField = null) {

        var type = property.Type;
        var isString = type.SpecialType == SpecialType.System_String;
        var elementType = TypeFacts.ElementTypeOf(type);

        var shape = PropertyShape.Scalar;
        string? elementTypeName = null;
        string? elementValidatorName = null;

        var dictionary = TypeFacts.DictionaryTypesOf(type);

        if (validateNested) {
            if (dictionary is { } entry) {
                shape = PropertyShape.Dictionary;
                elementTypeName = entry.Value.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                if (entry.Value is INamedTypeSymbol namedValue) {
                    elementValidatorName = QualifiedValidator(namedValue, validatorNameFor);
                }
            } else if (elementType is not null) {
                shape = PropertyShape.Collection;
                elementTypeName = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                if (elementType is INamedTypeSymbol namedElement) {
                    elementValidatorName = QualifiedValidator(namedElement, validatorNameFor);
                }
            } else if (type is INamedTypeSymbol namedType) {
                shape = PropertyShape.Object;
                elementValidatorName = QualifiedValidator(namedType, validatorNameFor);
            }
        }

        ValidateConstraintsAgainstType(property, constraints, isString, elementType is not null);

        return new ValidatedPropertyModel(
            property.Name,
            overriddenField ?? FieldNameFor(property),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            shape,
            elementTypeName,
            elementValidatorName,
            type.IsReferenceType,
            isString,
            TypeFacts.IsNullableValueType(type),
            elementType is not null && TypeFacts.IsIndexable(type),
            TypeFacts.CountAccessor(type),
            validateNested,
            new EquatableArray<ConstraintModel>(Order(constraints).ToImmutableArray()));
    }

    /// <summary>
    /// Required is evaluated first whatever order the attributes were written in, because the
    /// collector's suppression rule is forward-only. Everything else keeps attribute order, which
    /// is what §4.2 promises.
    /// </summary>
    private static IEnumerable<ConstraintModel> Order(List<ConstraintModel> constraints) =>
        constraints.Where(constraint => constraint.Kind == ConstraintKind.Required)
            .Concat(constraints.Where(constraint => constraint.Kind != ConstraintKind.Required));

    private void ValidateConstraintsAgainstType(
        IPropertySymbol property, List<ConstraintModel> constraints, bool isString, bool isCollection) {

        foreach (var constraint in constraints) {
            var typeName = property.Type.ToDisplayString();

            switch (constraint.Kind) {
                case ConstraintKind.StringLength when !isString:
                    Report(ValidationDiagnostics.StringConstraintOnNonString, property,
                        "[StringLength]", property.Name, typeName);
                    break;

                case ConstraintKind.Pattern when !isString:
                    Report(ValidationDiagnostics.StringConstraintOnNonString, property,
                        "[Pattern]", property.Name, typeName);
                    break;

                case ConstraintKind.ItemCount when !isCollection:
                    Report(ValidationDiagnostics.ItemCountOnNonCollection, property, property.Name, typeName);
                    break;

                case ConstraintKind.Range when !TypeFacts.IsOrdered(property.Type):
                    Report(ValidationDiagnostics.RangeOnUnorderedType, property, property.Name, typeName);
                    break;

                case ConstraintKind.Required when property.Type.IsValueType && !TypeFacts.IsNullableValueType(property.Type):
                    Report(ValidationDiagnostics.RequiredOnNonNullableValueType, property, property.Name);
                    break;
            }

            if (constraint.Kind is ConstraintKind.StringLength or ConstraintKind.ItemCount &&
                int.TryParse(constraint.Min, out var min) &&
                int.TryParse(constraint.Max, out var max) &&
                min > max) {
                Report(ValidationDiagnostics.MinExceedsMax, property, property.Name);
            }

            if (constraint.Kind == ConstraintKind.Pattern && constraint.RegexAccessor is null &&
                constraint.Pattern is { } pattern && !TypeFacts.IsValidRegex(pattern, out var error)) {
                Report(ValidationDiagnostics.InvalidPattern, property, property.Name, error);
            }

            // RegexOptions.Compiled is 8. Meaningless against a source-generated regex, and asking
            // for it usually means someone is carrying over a habit this library exists to remove.
            if (constraint.Kind == ConstraintKind.Pattern && (constraint.RegexOptions & 8) != 0) {
                Report(ValidationDiagnostics.CompiledRegexRequested, property, property.Name);
            }
        }
    }

    private List<ConstraintModel> ReadConstraints(IPropertySymbol property) {
        var constraints = new List<ConstraintModel>();

        foreach (var attribute in property.GetAttributes()) {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null) {
                continue;
            }

            var ns = attributeClass.ContainingNamespace?.ToDisplayString();

            if (ns == KnownTypes.ConstraintsNamespace) {
                var native = NativeConstraintReader.Read(attribute, attributeClass.Name);

                if (native is { Kind: ConstraintKind.Pattern }) {
                    native = ResolvePattern(native, attribute, property);
                }

                if (native is not null) {
                    constraints.Add(native);
                }

                continue;
            }

            if (ns != KnownTypes.DataAnnotationsNamespace) {
                if (DerivesFromValidationAttribute(attributeClass)) {
                    Report(ValidationDiagnostics.CustomValidationAttribute, property,
                        attributeClass.Name, property.Name);
                }

                continue;
            }

            if (!_compileDataAnnotations) {
                if (DataAnnotationsConstraintReader.IsConstraint(attributeClass.Name)) {
                    Report(ValidationDiagnostics.DataAnnotationsSkipped, property,
                        attributeClass.Name, property.Name);
                }

                continue;
            }

            var outcome = DataAnnotationsConstraintReader.Read(attribute, attributeClass.Name, property);
            if (outcome.Constraint is not null) {
                constraints.Add(outcome.Constraint);
            }

            if (outcome.Diagnostic is not null) {
                _diagnostics.Add(Diagnostic.Create(
                    outcome.Diagnostic, Location(property), attributeClass.Name, property.Name));
            }
        }

        return constraints;
    }

    /// <summary>
    /// Resolves the reference form's member, or applies the policy to the inline form.
    /// </summary>
    private ConstraintModel? ResolvePattern(ConstraintModel constraint, AttributeData attribute, IPropertySymbol property) {
        var args = attribute.ConstructorArguments;

        if (args.Length == 2 && args[0].Value is INamedTypeSymbol provider && args[1].Value is string memberName) {
            var member = provider.GetMembers(memberName).FirstOrDefault();

            string? problem = member switch {
                null => "does not exist",
                IMethodSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false } or IFieldSymbol { IsStatic: false }
                    => "is not static",
                IMethodSymbol { Parameters.Length: > 0 } => "takes parameters",
                IMethodSymbol method when !IsRegex(method.ReturnType) => "does not return Regex",
                IPropertySymbol prop when !IsRegex(prop.Type) => "does not return Regex",
                IFieldSymbol field when !IsRegex(field.Type) => "is not a Regex",
                IMethodSymbol or IPropertySymbol or IFieldSymbol => null,
                _ => "is not a method, property or field",
            };

            if (member is not null && problem is null &&
                member.DeclaredAccessibility is Accessibility.Private or Accessibility.ProtectedAndInternal) {
                problem = "is not accessible";
            }

            if (problem is not null) {
                _diagnostics.Add(Diagnostic.Create(
                    ValidationDiagnostics.RegexMemberUnusable, Location(property),
                    provider.ToDisplayString(), memberName, problem, property.Name));

                return null;
            }

            var qualified = provider.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var accessor = member is IMethodSymbol ? $"{qualified}.{memberName}()" : $"{qualified}.{memberName}";

            return constraint with { RegexAccessor = accessor };
        }

        // Inline form. Correct and AOT-clean, but it roots the regex parser and interpreter, which
        // is +1.16 MB on a published AOT binary. The policy decides whether that is acceptable here.
        if (_patternPolicy is PatternPolicy.Error or PatternPolicy.Warn) {
            var severity = _patternPolicy == PatternPolicy.Error
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning;

            _diagnostics.Add(Diagnostic.Create(
                ValidationDiagnostics.InlinePatternUnderAot, Location(property), severity,
                additionalLocations: null, properties: null,
                property.Name, property.ContainingType.Name));

            if (_patternPolicy == PatternPolicy.Error) {
                return null;
            }
        }

        return constraint;
    }

    private static bool IsRegex(ITypeSymbol type) =>
        type.ToDisplayString() == "System.Text.RegularExpressions.Regex";

    private string FieldNameFor(IPropertySymbol property) {
        foreach (var attribute in property.GetAttributes()) {
            var name = attribute.AttributeClass?.ToDisplayString();

            if (name == KnownTypes.JsonPropertyName &&
                attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is string jsonName) {
                return jsonName;
            }

            if (name == KnownTypes.DisplayAttribute) {
                foreach (var named in attribute.NamedArguments) {
                    if (named.Key == "Name" && named.Value.Value is string displayName) {
                        return displayName;
                    }
                }
            }
        }

        return _fieldNamer(property.Name);
    }

    private static string QualifiedValidator(INamedTypeSymbol type, Func<INamedTypeSymbol, string> validatorNameFor) {
        var name = validatorNameFor(type);

        return type.ContainingNamespace.IsGlobalNamespace
            ? $"global::{name}"
            : $"global::{type.ContainingNamespace.ToDisplayString()}.{name}";
    }

    private static bool HasGenerateValidator(INamedTypeSymbol type) =>
        type.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == KnownTypes.GenerateValidatorAttribute);

    private static bool HasValidateNested(IPropertySymbol property) =>
        property.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.Name == "ValidateNestedAttribute" &&
            attribute.AttributeClass.ContainingNamespace?.ToDisplayString() == KnownTypes.ConstraintsNamespace);

    private static bool DerivesFromValidationAttribute(INamedTypeSymbol attributeClass) {
        for (var current = attributeClass.BaseType; current is not null; current = current.BaseType) {
            if (current.ToDisplayString() == KnownTypes.ValidationAttribute) {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsValidatableObject(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(i => i.ToDisplayString() == KnownTypes.ValidatableObject);

    private void Report(DiagnosticDescriptor descriptor, ISymbol symbol, params object?[] args) =>
        _diagnostics.Add(Diagnostic.Create(descriptor, Location(symbol), args));

    private static Location? Location(ISymbol symbol) => symbol.Locations.FirstOrDefault();
}
