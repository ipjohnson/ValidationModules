using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using ValidationModules.SourceGenerator.Impl;

namespace ValidationModules.SourceGenerator;

/// <summary>
/// Flags a <c>.Validate&lt;T&gt;()</c> whose <c>T</c> - or whose <c>List&lt;T&gt;</c>/<c>T[]</c>
/// element - is declared in this compilation and produces no validator.
/// </summary>
/// <remarks>
/// <para>
/// Build time beats startup. The endpoint filter factory already refuses an unregistered type
/// when the endpoint is built, which minimal APIs do on the first request; this is the same
/// guarantee a build earlier, where the mistake was made.
/// </para>
/// <para>
/// The same cross-assembly caution as VM0007: only a type this compilation declares is judged,
/// because a referenced assembly may carry its own generated validator, and a rules class in
/// another assembly may target even a local type - which is why this is a warning naming the
/// startup check as the backstop, not an error.
/// </para>
/// <para>
/// Matched by the extension type's name and namespace rather than by assembly identity, which is
/// also what makes the analyzer testable without an ASP.NET Core reference; only the genuine
/// package declares <c>Microsoft.AspNetCore.Builder.ValidationModulesEndpointExtensions</c>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ValidateCallAnalyzer : DiagnosticAnalyzer {

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(ValidationDiagnostics.ValidateTargetHasNoValidator);

    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static start => {
            // Whether a type is the target of a rules class is a compilation-wide question, so the
            // judging waits for the end action - the same reason the generator collects its
            // candidates before building any model.
            var ruleTargets = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var calls = new ConcurrentBag<(ITypeSymbol Type, Location Location)>();

            start.RegisterSymbolAction(symbolContext => {
                foreach (var contract in ((INamedTypeSymbol)symbolContext.Symbol).AllInterfaces) {
                    // A hand-written IValidatorFor<T>/IAsyncValidatorFor<T> counts alongside a
                    // rules class: registered by hand, it satisfies the startup check, and this
                    // analyzer must never be louder than the check it fronts.
                    var definition = contract.ConstructedFrom.ToDisplayString();

                    if (definition is not (KnownTypes.ValidationRulesForInterface
                            or "ValidationModules.IValidatorFor<T>"
                            or "ValidationModules.IAsyncValidatorFor<T>")) {
                        continue;
                    }

                    if (contract.TypeArguments.Length == 1 &&
                        contract.TypeArguments[0] is INamedTypeSymbol target) {
                        ruleTargets.TryAdd(target, 0);
                    }
                }
            }, SymbolKind.NamedType);

            start.RegisterOperationAction(operationContext => {
                var invocation = (IInvocationOperation)operationContext.Operation;
                var method = invocation.TargetMethod;

                if (method is not { Name: "Validate", IsGenericMethod: true, TypeArguments.Length: 1 } ||
                    method.ContainingType is not { Name: "ValidationModulesEndpointExtensions" } extensions ||
                    extensions.ContainingNamespace?.ToDisplayString() != "Microsoft.AspNetCore.Builder") {
                    return;
                }

                calls.Add((method.TypeArguments[0], invocation.Syntax.GetLocation()));
            }, OperationKind.Invocation);

            start.RegisterCompilationEndAction(end => {
                foreach (var (type, location) in calls) {
                    Judge(end, type, location, ruleTargets);
                }
            });
        });
    }

    private static void Judge(
        CompilationAnalysisContext context,
        ITypeSymbol type,
        Location location,
        ConcurrentDictionary<INamedTypeSymbol, byte> ruleTargets) {

        // A List<T> or T[] body validates element-wise through the generated registration, so the
        // element is what has to have rules.
        var target = ElementOf(type) ?? type;

        // Anything else - a metadata type, another collection shape, a constructed generic - is
        // the startup check's business: from here it may legitimately carry a validator this
        // compilation cannot see.
        if (target is not INamedTypeSymbol { IsGenericType: false } named ||
            named.DeclaringSyntaxReferences.Length == 0) {
            return;
        }

        if (ProducesAValidator(named, ruleTargets)) {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            ValidationDiagnostics.ValidateTargetHasNoValidator, location,
            named.Name, type.ToDisplayString()));
    }

    private static ITypeSymbol? ElementOf(ITypeSymbol type) => type switch {
        IArrayTypeSymbol array => array.ElementType,
        INamedTypeSymbol { IsGenericType: true } named when
            named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.List<T>" =>
            named.TypeArguments[0],
        _ => null,
    };

    /// <summary>
    /// The analyzer's reading of the front end's ProducesAValidator: <c>[GenerateValidator]</c>, a
    /// rules class in this compilation, or any constraint either front end reads on the type's
    /// own or inherited properties.
    /// </summary>
    private static bool ProducesAValidator(
        INamedTypeSymbol type, ConcurrentDictionary<INamedTypeSymbol, byte> ruleTargets) {

        if (ruleTargets.ContainsKey(type)) {
            return true;
        }

        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() == KnownTypes.GenerateValidatorAttribute) {
                return true;
            }
        }

        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType) {
            foreach (var member in current.GetMembers()) {
                if (member is IPropertySymbol property && CarriesConstraints(property)) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CarriesConstraints(IPropertySymbol property) {
        foreach (var attribute in property.GetAttributes()) {
            if (attribute.AttributeClass is not { } attributeClass) {
                continue;
            }

            var ns = attributeClass.ContainingNamespace?.ToDisplayString();

            if (ns == KnownTypes.ConstraintsNamespace || ns == KnownTypes.DataAnnotationsNamespace) {
                return true;
            }

            for (var baseType = attributeClass.BaseType; baseType is not null; baseType = baseType.BaseType) {
                var name = baseType.ToDisplayString();

                if (name == KnownTypes.CustomConstraintAttribute || name == KnownTypes.ValidationAttribute) {
                    return true;
                }
            }

            foreach (var contract in attributeClass.AllInterfaces) {
                if (contract.OriginalDefinition.ToDisplayString() == KnownTypes.ConstraintForInterface) {
                    return true;
                }
            }
        }

        return false;
    }
}
