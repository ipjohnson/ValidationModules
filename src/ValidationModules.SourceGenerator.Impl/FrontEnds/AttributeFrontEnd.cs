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

            var constraints = ReadConstraintsFor(property, property.Type);
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
            new EquatableArray<string>(ImmutableArray.CreateRange(applied ?? Array.Empty<string>())),
            IsExternallyVisible(type));
    }

    /// <summary>
    /// Whether the type - and every type it is nested in - is visible outside the assembly.
    /// </summary>
    /// <remarks>
    /// The containing chain is walked because effective accessibility is the minimum along it: a
    /// public type nested inside an internal one is internal in effect, and a public validator over
    /// it is the same CS0051 as one over a plainly internal type.
    /// </remarks>
    private static bool IsExternallyVisible(INamedTypeSymbol type) {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType) {
            if (current.DeclaredAccessibility != Accessibility.Public) {
                return false;
            }
        }

        return true;
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

        ValidateAndResolve(property, type, constraints, isString, elementType);

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
    private void ResolveRangeBounds(ISymbol member, ITypeSymbol memberType, List<ConstraintModel> constraints) {
        if (!TypeFacts.IsOrdered(memberType)) {
            return;
        }

        for (var i = constraints.Count - 1; i >= 0; i--) {
            var constraint = constraints[i];

            if (constraint.Kind != ConstraintKind.Range) {
                continue;
            }

            if (constraint.Min is null && constraint.Max is null) {
                Report(ValidationDiagnostics.RangeHasNoBounds, member, member.Name);
                constraints.RemoveAt(i);
                continue;
            }

            // Each bound resolves on its own, because either may be absent. A [Range(Min = 1)] on a
            // spec that set only `minimum` has to emit one comparison, not a second one against the
            // type's extreme - which is what put "must be between 1 and 7.9228162514264338E+28" in a
            // 400 body.
            var min = constraint.Min;
            var max = constraint.Max;
            var parsed = true;

            if (min is not null) {
                parsed = RangeBoundReader.TryResolve(memberType, min, out var resolved);
                min = resolved;
            }

            if (parsed && max is not null) {
                parsed = RangeBoundReader.TryResolve(memberType, max, out var resolved);
                max = resolved;
            }

            if (!parsed) {
                Report(ValidationDiagnostics.RangeBoundsNotParseable, member,
                    member.Name, memberType.ToDisplayString());

                constraints.RemoveAt(i);
                continue;
            }

            constraints[i] = constraint with { Min = min, Max = max };
        }
    }

    /// <summary>
    /// Resolves a member's constraints against its type and reports the ones that do not fit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public and typed on <see cref="ISymbol"/> rather than <see cref="IPropertySymbol"/>, together
    /// with <see cref="ReadConstraintsFor"/>, so a host that carries constraints somewhere other
    /// than a property - a handler's method parameter, in Hardened's case - reaches the same
    /// vocabulary, the same pattern reference form, the same AOT pattern policy and the same
    /// diagnostics. The alternative was a second implementation of a policy this package owns, which
    /// is the arrangement the two front ends here exist to avoid.
    /// </para>
    /// <para>
    /// Order matters and is the reason this is one method rather than three calls a caller sequences
    /// itself. Both resolvers run before the applicability check, so a bound or a divisor that
    /// cannot parse against a type that could never carry the constraint reports the constraint's
    /// diagnostic rather than the parse one.
    /// </para>
    /// </remarks>
    /// <param name="member">The property or parameter the constraints were written on.</param>
    /// <param name="memberType">Its type.</param>
    /// <param name="constraints">Read in; rewritten and pruned in place.</param>
    /// <param name="isString">Whether <paramref name="memberType"/> is <c>string</c>.</param>
    /// <param name="elementType">Its element type, or null when it is not a collection.</param>
    public void ValidateAndResolve(
        ISymbol member,
        ITypeSymbol memberType,
        List<ConstraintModel> constraints,
        bool isString,
        ITypeSymbol? elementType) {

        ResolveRangeBounds(member, memberType, constraints);
        ResolveMultipleOfDivisors(member, memberType, constraints);
        ValidateConstraintsAgainstType(member, memberType, constraints, isString, elementType);
    }

    /// <summary>
    /// Rewrites <c>[MultipleOf]</c> divisors into the denomination their check runs in, dropping any
    /// that has no such form.
    /// </summary>
    /// <remarks>
    /// Runs before <see cref="ValidateConstraintsAgainstType"/> for the reason
    /// <see cref="ResolveRangeBounds"/> does: on a member no divisor could apply to, VM0021 is the
    /// news rather than VM0023. A divisor that survives is positive and rendered, so the emitter
    /// never writes <c>% 0</c> - CS0020 for an integral member, DivideByZeroException for a decimal
    /// one, and either way a failure inside generated code.
    /// </remarks>
    private void ResolveMultipleOfDivisors(ISymbol member, ITypeSymbol memberType, List<ConstraintModel> constraints) {
        if (!MultipleOfReader.IsSupported(memberType)) {
            return;
        }

        for (var i = constraints.Count - 1; i >= 0; i--) {
            var constraint = constraints[i];

            if (constraint.Kind != ConstraintKind.MultipleOf) {
                continue;
            }

            if (!MultipleOfReader.TryResolve(
                    memberType, constraint.Divisor ?? "0",
                    out var divisor, out var value, out var decimalDomain)) {

                Report(ValidationDiagnostics.MultipleOfDivisorNotParseable, member,
                    member.Name, memberType.ToDisplayString());

                constraints.RemoveAt(i);
                continue;
            }

            if (value <= 0m) {
                Report(ValidationDiagnostics.MultipleOfDivisorNotPositive, member, member.Name, divisor);
                constraints.RemoveAt(i);
                continue;
            }

            constraints[i] = constraint with { Divisor = divisor, DecimalDomain = decimalDomain };
        }
    }

    private void ValidateConstraintsAgainstType(
        ISymbol member, ITypeSymbol memberType, List<ConstraintModel> constraints,
        bool isString, ITypeSymbol? elementType) {

        var isCollection = elementType is not null;

        foreach (var constraint in constraints) {
            var typeName = memberType.ToDisplayString();

            switch (constraint.Kind) {
                case ConstraintKind.StringLength when !isString:
                    Report(ValidationDiagnostics.StringConstraintOnNonString, member,
                        "[StringLength]", member.Name, typeName);
                    break;

                case ConstraintKind.Pattern when !isString:
                    Report(ValidationDiagnostics.StringConstraintOnNonString, member,
                        "[Pattern]", member.Name, typeName);
                    break;

                case ConstraintKind.ItemCount when !isCollection:
                    Report(ValidationDiagnostics.ItemCountOnNonCollection, member, member.Name, typeName);
                    break;

                case ConstraintKind.Range when !TypeFacts.IsOrdered(memberType):
                    Report(ValidationDiagnostics.RangeOnUnorderedType, member, member.Name, typeName);
                    break;

                case ConstraintKind.MultipleOf when !MultipleOfReader.IsSupported(memberType):
                    Report(ValidationDiagnostics.MultipleOfOnUnsupportedType, member, member.Name, typeName);
                    break;

                case ConstraintKind.UniqueItems when !isCollection:
                    Report(ValidationDiagnostics.UniqueItemsOnNonCollection, member, member.Name, typeName);
                    break;

                // The check runs through EqualityComparer<T>.Default, so an element type with no
                // equality of its own compares by reference and two elements with identical contents
                // are "unique". A rule that passes for the wrong reason, which is worse than one
                // that fails.
                case ConstraintKind.UniqueItems when elementType is { } element && TypeFacts.ComparesByReference(element):
                    Report(ValidationDiagnostics.UniqueItemsComparesByReference, member,
                        member.Name, element.ToDisplayString());
                    break;

                case ConstraintKind.Required when memberType.IsValueType && !TypeFacts.IsNullableValueType(memberType):
                    Report(ValidationDiagnostics.RequiredOnNonNullableValueType, member, member.Name);
                    break;
            }

            if (constraint.Kind is ConstraintKind.StringLength or ConstraintKind.ItemCount &&
                int.TryParse(constraint.Min, out var min) &&
                int.TryParse(constraint.Max, out var max) &&
                min > max) {
                Report(ValidationDiagnostics.MinExceedsMax, member, member.Name);
            }

            if (constraint.Kind == ConstraintKind.Pattern && constraint.RegexAccessor is null &&
                constraint.Pattern is { } pattern && !TypeFacts.IsValidRegex(pattern, out var error)) {
                Report(ValidationDiagnostics.InvalidPattern, member, member.Name, error);
            }

            // RegexOptions.Compiled is 8. Meaningless against a source-generated regex, and asking
            // for it usually means someone is carrying over a habit this library exists to remove.
            if (constraint.Kind == ConstraintKind.Pattern && (constraint.RegexOptions & 8) != 0) {
                Report(ValidationDiagnostics.CompiledRegexRequested, member, member.Name);
            }
        }
    }

    public List<ConstraintModel> ReadConstraintsFor(ISymbol member, ITypeSymbol memberType) {
        var constraints = new List<ConstraintModel>();

        foreach (var attribute in member.GetAttributes()) {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null) {
                continue;
            }

            var ns = attributeClass.ContainingNamespace?.ToDisplayString();

            if (ns == KnownTypes.ConstraintsNamespace) {
                var native = NativeConstraintReader.Read(attribute, attributeClass.Name);

                if (native is { Kind: ConstraintKind.Pattern }) {
                    native = ResolvePattern(native, attribute, member);
                }

                if (native is not null) {
                    constraints.Add(native);
                }

                continue;
            }

            if (ns != KnownTypes.DataAnnotationsNamespace) {
                if (DerivesFromValidationAttribute(attributeClass)) {
                    Report(ValidationDiagnostics.CustomValidationAttribute, member,
                        attributeClass.Name, member.Name);
                }

                continue;
            }

            if (!_compileDataAnnotations) {
                if (DataAnnotationsConstraintReader.IsConstraint(attributeClass.Name)) {
                    Report(ValidationDiagnostics.DataAnnotationsSkipped, member,
                        attributeClass.Name, member.Name);
                }

                continue;
            }

            var outcome = DataAnnotationsConstraintReader.Read(attribute, attributeClass.Name, memberType);
            if (outcome.Constraint is not null) {
                constraints.Add(outcome.Constraint);
            }

            if (outcome.Diagnostic is not null) {
                _diagnostics.Add(Diagnostic.Create(
                    outcome.Diagnostic, Location(member), attributeClass.Name, member.Name));
            }
        }

        return constraints;
    }

    /// <summary>
    /// Resolves the reference form's member, or applies the policy to the inline form.
    /// </summary>
    private ConstraintModel? ResolvePattern(ConstraintModel constraint, AttributeData attribute, ISymbol owner) {
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
                    ValidationDiagnostics.RegexMemberUnusable, Location(owner),
                    provider.ToDisplayString(), memberName, problem, owner.Name));

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
                ValidationDiagnostics.InlinePatternUnderAot, Location(owner), severity,
                additionalLocations: null, properties: null,
                owner.Name, owner.ContainingType?.Name));

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
