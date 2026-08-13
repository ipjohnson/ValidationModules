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

        // Before anything reads a property, because the situation this reports is precisely one
        // where no property carries anything and the type would otherwise look unconstrained.
        ReportRecordParameterConstraints(type);

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

        ResolveRangeBounds(property, constraints);
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

    /// <summary>
    /// Reports a constraint carrying profile attribution, which nothing here reads.
    /// </summary>
    /// <remarks>
    /// Reported per constraint rather than once per type: the author has to remove each argument,
    /// and a single diagnostic on the type would not say which rule to look at.
    /// </remarks>
    private void ReportProfileAttribution(
        AttributeData attribute, INamedTypeSymbol attributeClass, IPropertySymbol property) {

        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key is not ("FromProfile" or "UntilProfile" or "Profiles")) {
                continue;
            }

            // A null or empty argument restricts nothing, so it is not silently changing behaviour.
            if (argument.Value.IsNull ||
                (argument.Key == "Profiles" && argument.Value.Kind == TypedConstantKind.Array &&
                 argument.Value.Values.Length == 0)) {
                continue;
            }

            var location = attribute.ApplicationSyntaxReference is { } reference
                ? Microsoft.CodeAnalysis.Location.Create(reference.SyntaxTree, reference.Span)
                : Location(property);

            _diagnostics.Add(Diagnostic.Create(
                ValidationDiagnostics.ProfileAttributionNotImplemented,
                location,
                Unsuffixed(attributeClass.Name),
                property.Name));

            return;
        }
    }

    /// <summary>
    /// Reports a constraint written on a record's positional parameter without the
    /// <c>property:</c> target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The quietest failure this library had. <c>record Pet([Required] string Name)</c> binds the
    /// attribute to the primary constructor's parameter, so the generated property carries no
    /// metadata, nothing here sees a constraint, and no validator is emitted at all - not an empty
    /// one. Nothing is registered, <c>IValidatorFor&lt;Pet&gt;</c> does not resolve, and a runner
    /// merging zero validators calls every value valid.
    /// </para>
    /// <para>
    /// Scoped to the primary constructor, identified by its declaring syntax being the type
    /// declaration rather than a constructor declaration. A constraint on an ordinary constructor's
    /// parameter is equally inert, but <c>[property:]</c> is not legal there, so the advice this
    /// diagnostic gives would be wrong.
    /// </para>
    /// </remarks>
    private void ReportRecordParameterConstraints(INamedTypeSymbol type) {
        if (!type.IsRecord) {
            return;
        }

        foreach (var constructor in type.InstanceConstructors) {
            if (!IsPrimaryConstructor(constructor)) {
                continue;
            }

            foreach (var parameter in constructor.Parameters) {
                foreach (var attribute in parameter.GetAttributes()) {
                    if (attribute.AttributeClass is not { } attributeClass || !IsConstraintAttribute(attributeClass)) {
                        continue;
                    }

                    // Qualified because this class has a Location(ISymbol) helper of its own, which
                    // otherwise shadows the type.
                    var location = attribute.ApplicationSyntaxReference is { } reference
                        ? Microsoft.CodeAnalysis.Location.Create(reference.SyntaxTree, reference.Span)
                        : Location(parameter);

                    _diagnostics.Add(Diagnostic.Create(
                        ValidationDiagnostics.RecordParameterMissingPropertyTarget,
                        location,
                        Unsuffixed(attributeClass.Name)));
                }
            }
        }
    }

    /// <summary>
    /// Whether this constructor is the one written in the type's own header.
    /// </summary>
    /// <remarks>
    /// A primary constructor's declaring syntax is the type declaration; an ordinary one's is a
    /// <c>ConstructorDeclarationSyntax</c>. The record's copy constructor is implicit and has no
    /// declaring syntax at all.
    /// </remarks>
    private static bool IsPrimaryConstructor(IMethodSymbol constructor) {
        foreach (var reference in constructor.DeclaringSyntaxReferences) {
            if (reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax) {
                return true;
            }
        }

        return false;
    }

    private bool IsConstraintAttribute(INamedTypeSymbol attributeClass) {
        var ns = attributeClass.ContainingNamespace?.ToDisplayString();

        if (ns == KnownTypes.ConstraintsNamespace) {
            return true;
        }

        // Only when the second vocabulary is switched on. With it off the attribute is not enforced
        // wherever it sits, and VM0010 is the diagnostic with that news.
        return ns == KnownTypes.DataAnnotationsNamespace &&
               _compileDataAnnotations &&
               DataAnnotationsConstraintReader.IsConstraint(attributeClass.Name);
    }

    /// <summary>"RequiredAttribute" to "Required", so the suggested fix reads as it would be typed.</summary>
    private static string Unsuffixed(string attributeName) =>
        attributeName.EndsWith("Attribute", StringComparison.Ordinal)
            ? attributeName.Substring(0, attributeName.Length - "Attribute".Length)
            : attributeName;

    /// <summary>
    /// Rewrites <c>[Range]</c> bounds written as strings into expressions of the member's own type.
    /// </summary>
    /// <remarks>
    /// Runs before <see cref="ValidateConstraintsAgainstType"/> so the ordering check reports
    /// VM0003 first on a member that is not ordered at all - a bound that cannot parse against a
    /// type that could never carry a range is VM0003's news, not VM0065's. A bound that fails to
    /// parse takes its constraint with it, so the build fails on the diagnostic alone rather than
    /// also on generated code that will not compile.
    /// </remarks>
    private void ResolveRangeBounds(IPropertySymbol property, List<ConstraintModel> constraints) {
        if (!TypeFacts.IsOrdered(property.Type)) {
            return;
        }

        for (var i = constraints.Count - 1; i >= 0; i--) {
            var constraint = constraints[i];

            if (constraint.Kind != ConstraintKind.Range) {
                continue;
            }

            if (!RangeBoundReader.TryResolve(property.Type, constraint.Min ?? "0", out var min) ||
                !RangeBoundReader.TryResolve(property.Type, constraint.Max ?? "0", out var max)) {

                Report(ValidationDiagnostics.RangeBoundsNotParseable, property,
                    property.Name, property.Type.ToDisplayString());

                constraints.RemoveAt(i);
                continue;
            }

            constraints[i] = constraint with { Min = min, Max = max };
        }
    }

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
                ReportProfileAttribution(attribute, attributeClass, property);

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
