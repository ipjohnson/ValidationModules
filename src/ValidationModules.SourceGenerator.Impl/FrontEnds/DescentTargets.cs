using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Whether a descent into a type has a validator to call. One answer for <c>[ValidateNested]</c>,
/// for a rules class's <c>Nested</c> and <c>Each</c>, and for the <c>.Validate&lt;T&gt;()</c>
/// analyzer.
/// </summary>
/// <remarks>
/// <para>
/// A descent names the target's validator in generated code: the standalone path constructs
/// <c>new global::Sample.AddressValidator()</c>. A descent kept when no such class exists fails the
/// consumer's build inside a generated file, and one dropped when it does exist stops validating
/// with nothing but a warning. So every caller has to agree with what the generator actually emits,
/// which is why the question is answered here rather than by each of them.
/// </para>
/// <para>
/// The answer for a type declared in this compilation is read off its declarations, the same ones
/// the attribute front end builds the validator from. The answer for a type declared in another
/// assembly is read off that assembly's metadata: its generated validator is a type like any other,
/// and either this compilation can reach it or the descent has nothing to call.
/// </para>
/// </remarks>
public static class DescentTargets
{
    /// <summary>What a descent into a type would find.</summary>
    public enum Verdict
    {
        /// <summary>A validator exists, or this compilation generates one.</summary>
        Callable,

        /// <summary>Declared in this compilation, with nothing that asks for a validator.</summary>
        NoRules,

        /// <summary>
        /// A constructed generic, an array or a nullable value type, none of which can ever have a
        /// generated validator.
        /// </summary>
        CannotHaveValidator,

        /// <summary>
        /// Declared in another assembly that offers no validator this compilation can reach, and
        /// targeted by no rules class here.
        /// </summary>
        NoValidatorVisible,
    }

    /// <summary>What a descent into <paramref name="target"/> would find.</summary>
    /// <param name="target">
    /// The type the descent reaches: the property's own type, a collection's element or a
    /// dictionary's value, with <c>Nullable&lt;T&gt;</c> already unwrapped.
    /// </param>
    /// <param name="compilation">The compilation the generated descent lands in.</param>
    /// <param name="compileDataAnnotations">
    /// Whether <c>ValidationModules_DataAnnotations</c> lets the DataAnnotations vocabulary produce
    /// rules.
    /// </param>
    /// <param name="hasRulesClass">
    /// Whether a rules class in this compilation targets a type. Its validator is generated here,
    /// wherever the type itself is declared.
    /// </param>
    public static Verdict Judge(
        ITypeSymbol target,
        Compilation compilation,
        bool compileDataAnnotations,
        Func<INamedTypeSymbol, bool>? hasRulesClass
    )
    {
        if (target is not INamedTypeSymbol { IsGenericType: false } named)
        {
            return Verdict.CannotHaveValidator;
        }

        if (named.DeclaringSyntaxReferences.Length > 0)
        {
            return ProducesAValidator(named, compilation, compileDataAnnotations, hasRulesClass)
                ? Verdict.Callable
                : Verdict.NoRules;
        }

        return hasRulesClass?.Invoke(named) == true || HasReachableValidator(named, compilation)
            ? Verdict.Callable
            : Verdict.NoValidatorVisible;
    }

    /// <summary>
    /// The diagnostic that drops a descent, with its message arguments, or null when there is none
    /// to report.
    /// </summary>
    /// <remarks>
    /// Null for a type parameter as well as for <see cref="Verdict.Callable"/>. A type-parameter
    /// target only occurs inside a generic validated type, which VM1010 refuses wholesale, and a
    /// second diagnostic there would be noise.
    /// </remarks>
    /// <param name="verdict">What <see cref="Judge"/> answered.</param>
    /// <param name="target">The type the descent reaches.</param>
    /// <param name="member">The property the descent starts from.</param>
    /// <param name="construct">
    /// What asked for the descent, as the author wrote it: <c>[ValidateNested]</c>,
    /// <c>rules.Nested</c> or <c>rules.Each</c>.
    /// </param>
    public static (DiagnosticDescriptor Descriptor, object?[] Arguments)? Problem(
        Verdict verdict,
        ITypeSymbol target,
        string member,
        string construct
    ) =>
        verdict switch
        {
            Verdict.NoRules => (
                ValidationDiagnostics.NestedTypeHasNoRules,
                new object?[] { target.Name, member, construct }
            ),
            Verdict.CannotHaveValidator when target.TypeKind != TypeKind.TypeParameter => (
                ValidationDiagnostics.NestedTargetCannotHaveValidator,
                new object?[] { target.ToDisplayString(), member, construct }
            ),
            Verdict.NoValidatorVisible when target is INamedTypeSymbol named => (
                ValidationDiagnostics.NestedTargetHasNoVisibleValidator,
                new object?[]
                {
                    target.Name,
                    target.ContainingAssembly?.Name,
                    GeneratedNames.Validator(named),
                    construct,
                    member,
                }
            ),
            _ => null,
        };

    /// <summary>
    /// Whether anything about <paramref name="type"/> asks for a validator to be generated.
    /// </summary>
    /// <remarks>
    /// Deliberately the same things <see cref="AttributeFrontEnd.Build"/> itself treats as asking:
    /// a constraint on a member, a class-level <c>ValidationAttribute</c>, <c>[GenerateValidator]</c>,
    /// or a rules class, plus <c>[ValidateNested]</c>, which produces a validator that descends
    /// even with no constraints of its own. Any narrower test would drop a descent into a type that
    /// does get a validator, and any wider one would keep a descent into a type that does not.
    /// </remarks>
    public static bool ProducesAValidator(
        INamedTypeSymbol type,
        Compilation compilation,
        bool compileDataAnnotations,
        Func<INamedTypeSymbol, bool>? hasRulesClass
    )
    {
        if (HasGenerateValidator(type) || hasRulesClass?.Invoke(type) == true)
        {
            return true;
        }

        // A class-level ValidationAttribute is a rule of the type's own, found where
        // ReadObjectRules looks for it, so Build gives the type a validator on its strength.
        if (
            compileDataAnnotations
            && AttributeFrontEnd
                .ObjectRuleSources(type)
                .Any(declaring =>
                    declaring
                        .GetAttributes()
                        .Any(attribute =>
                            attribute.AttributeClass is { } attributeClass
                            && AttributeFrontEnd.DerivesFromValidationAttribute(attributeClass)
                        )
                )
        )
        {
            return true;
        }

        bool Carries(IPropertySymbol property) =>
            CarriesConstraints(property, compileDataAnnotations);

        // The walk rather than GetMembers(): a type whose only constraints are inherited still
        // produces a validator, so asking only about declared members would drop a descent into it.
        foreach (var member in MemberWalk.PropertiesOf(type, compilation, Carries))
        {
            if (member.Sources.Any(Carries))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a declaration carries anything either front end turns into a rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DataAnnotations attribute counts only when the generator compiles something from it. The
    /// namespace alone is not enough: <c>[Display]</c>, <c>[Key]</c>, <c>[DataType]</c>,
    /// <c>[Editable]</c> and <c>[ScaffoldColumn]</c> label or describe a property and produce no
    /// check, and <c>[Compare]</c> and <c>[EnumDataType]</c> are reported rather than compiled. A
    /// type carrying only those gets no validator, so a descent into it would call one that does not
    /// exist.
    /// </para>
    /// <para>
    /// Shared with <see cref="MemberWalk"/>, which consults it to decide whether an interface
    /// declaration is worth resolving to its implementer and whether a hidden base declaration is
    /// worth a VM1009.
    /// </para>
    /// </remarks>
    public static bool CarriesConstraints(IPropertySymbol property, bool compileDataAnnotations)
    {
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass)
            {
                continue;
            }

            var ns = attributeClass.ContainingNamespace?.ToDisplayString();

            // [ValidateNested] included: it lives in the constraints namespace.
            if (ns == KnownTypes.ConstraintsNamespace)
            {
                return true;
            }

            if (ns == KnownTypes.DataAnnotationsNamespace)
            {
                if (
                    compileDataAnnotations
                    && DataAnnotationsConstraintReader.Compiles(attributeClass.Name)
                )
                {
                    return true;
                }

                continue;
            }

            // A CustomConstraintAttribute subclass is native vocabulary wherever it is declared,
            // independent of the DataAnnotations switch. An IConstraintFor<T> implementer is the
            // same vocabulary's instance shape, and counts for the same reason.
            if (
                AttributeFrontEnd.DerivesFromCustomConstraint(attributeClass)
                || AttributeFrontEnd.ImplementsConstraintInterface(attributeClass)
            )
            {
                return true;
            }

            // A custom ValidationAttribute compiles to an invocation, so a property carrying only
            // one is a validated property.
            if (
                compileDataAnnotations
                && AttributeFrontEnd.DerivesFromValidationAttribute(attributeClass)
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <c>[GenerateValidator]</c> is on the type.</summary>
    public static bool HasGenerateValidator(INamedTypeSymbol type) =>
        type.GetAttributes()
            .Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == KnownTypes.GenerateValidatorAttribute
            );

    /// <summary>Whether <c>[ValidateNested]</c> is on this declaration of a property.</summary>
    public static bool HasValidateNested(IPropertySymbol property) =>
        property
            .GetAttributes()
            .Any(attribute =>
                attribute.AttributeClass?.Name == "ValidateNestedAttribute"
                && attribute.AttributeClass.ContainingNamespace?.ToDisplayString()
                    == KnownTypes.ConstraintsNamespace
            );

    /// <summary>
    /// Whether this compilation can call a validator for a type declared in another assembly.
    /// </summary>
    /// <remarks>
    /// The generator names a validator after its type and places it in the type's namespace, so
    /// the one a descent would construct is looked up by that name, in every assembly this
    /// compilation references. It has to be accessible from here, validate the type, and have the
    /// parameterless constructor the standalone path calls.
    /// </remarks>
    private static bool HasReachableValidator(INamedTypeSymbol target, Compilation compilation)
    {
        var name = MetadataName(target.ContainingNamespace, GeneratedNames.Validator(target));

        foreach (var candidate in compilation.GetTypesByMetadataName(name))
        {
            if (
                compilation.IsSymbolAccessibleWithin(candidate, compilation.Assembly)
                && Validates(candidate, target)
                && candidate.InstanceConstructors.Any(constructor =>
                    constructor.Parameters.Length == 0
                    && compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly)
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool Validates(INamedTypeSymbol candidate, INamedTypeSymbol target) =>
        candidate.AllInterfaces.Any(contract =>
            contract.ConstructedFrom.ToDisplayString() == KnownTypes.ValidatorForInterface
            && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], target)
        );

    /// <summary>
    /// The metadata name of a type in <paramref name="ns"/>. Built from each segment's metadata
    /// name, because a namespace's display string keeps a keyword's <c>@</c> escape, which no
    /// metadata name carries.
    /// </summary>
    private static string MetadataName(INamespaceSymbol ns, string typeName)
    {
        var segments = new List<string>();

        for (
            var current = ns;
            current is { IsGlobalNamespace: false };
            current = current.ContainingNamespace
        )
        {
            segments.Insert(0, current.MetadataName);
        }

        segments.Add(typeName);

        return string.Join(".", segments);
    }
}
