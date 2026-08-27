using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using ValidationModules.SourceGenerator.Impl;
using ValidationModules.SourceGenerator.Impl.Emitters;
using ValidationModules.SourceGenerator.Impl.FrontEnds;
using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator;

/// <summary>
/// The generator this package ships, and the only <c>[Generator]</c> in the product.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a plain <c>IIncrementalGenerator</c>, deliberately.</b> DependencyModules'
/// <c>BaseSourceGenerator</c> is available here - its sources are compiled in - and deriving from
/// it would be the obvious move. It is the wrong one: that host processes
/// <c>[DependencyModule]</c>, so a project referencing both this package and
/// <c>DependencyModules.SourceGenerator</c> would have two generators claiming the same attribute
/// and emit the module twice. DependencyModules' writers and models are used as a library; its host
/// is not. See plan §7.2.
/// </para>
/// <para>
/// The entry point lives here and is not packed into the Impl package, which is what lets a
/// framework author compile Impl into their own generator and get every stage with no entry point
/// to collide with.
/// </para>
/// </remarks>
[Generator]
public sealed class ValidationSourceGenerator : IIncrementalGenerator {

    /// <summary>
    /// What VM0019 names as the site when the declaration is assembly-level rather than on a
    /// property. The assembly's own name would be more precise and reads worse in the message,
    /// which already says which attribute it is.
    /// </summary>
    private const string compilationAssemblyLabel = "this assembly";

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var options = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => {
            provider.GlobalOptions.TryGetValue("build_property.ValidationModules_Registration", out var registration);
            provider.GlobalOptions.TryGetValue("build_property.ValidationModules_FieldNaming", out var naming);
            provider.GlobalOptions.TryGetValue("build_property.ValidationModules_DataAnnotations", out var dataAnnotations);
            provider.GlobalOptions.TryGetValue("build_property.ValidationModules_PatternPolicy", out var patternPolicy);
            provider.GlobalOptions.TryGetValue("build_property.PublishAot", out var publishAot);
            provider.GlobalOptions.TryGetValue("build_property.IsAotCompatible", out var aotCompatible);

            return new GeneratorOptions(registration, naming, dataAnnotations, patternPolicy,
                IsTrue(publishAot) || IsTrue(aotCompatible));
        });

        // Probed once. An IncrementalValueProvider<bool> so downstream stages invalidate only when
        // the answer flips, rather than on every edit to the compilation.
        var hasDependencyModules = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.GetTypeByMetadataName(KnownTypes.DependencyModule) is not null);

        // Version lockstep. Projected to an int rather than the Compilation so the stage caches on
        // the answer, not on every edit. Plan §7.5.
        var runtimeContract = context.CompilationProvider.Select(static (compilation, _) =>
            EmitterContract.ResolveRuntimeContract(compilation));

        context.RegisterSourceOutput(runtimeContract, static (production, found) => {
            if (found < EmitterContract.RequiredRuntimeContract) {
                production.ReportDiagnostic(Diagnostic.Create(
                    ValidationDiagnostics.RuntimeContractTooOld, Location.None,
                    EmitterContract.RequiredRuntimeContract, found));
            }
        });

        // An assembly name is not necessarily a valid namespace: "My-App" emitted `namespace My-App;`
        // and broke the consumer's build in generated code.
        var assemblyNamespace = context.CompilationProvider.Select(static (compilation, _) =>
            SanitizeNamespace(compilation.AssemblyName));

        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax,
                static (syntaxContext, _) => syntaxContext.SemanticModel.GetDeclaredSymbol(syntaxContext.Node) as INamedTypeSymbol)
            .Where(static symbol => symbol is not null)
            .Select(static (symbol, _) => symbol!);

        // Rules classes are collected rather than projected one at a time, because a rules class and
        // the attributes on its target have to become one validator: two models with the same
        // ValidatorName collide on hint name, and AddSource throwing fails the whole generator rather
        // than one type. Combining with the compilation is what a rules class needs anyway - its
        // rules live in a method body, so reading them takes a semantic model and not just a symbol.
        var models = candidates
            .Collect()
            .Combine(context.CompilationProvider)
            .Combine(options)
            .Select(static (input, _) => BuildModels(input.Left.Left, input.Left.Right, input.Right));

        context.RegisterSourceOutput(models, static (production, results) => {
            // An IDynamicValidator adapter is only worth emitting for an assembly that actually
            // dispatches dynamically. Registering one per validated type roots every adapter, so
            // ILC cannot trim them - which would charge every consumer for a mode most never use.
            // Emitted for all of this assembly's types once any of them needs it, so that a miss
            // still means "this assembly never registered" rather than "this type had no rules".
            var dispatchesDynamically = results.Any(result =>
                result.Model is { } model
                && model.Properties.Any(property => property.Polymorphism == PolymorphismMode.Runtime));

            foreach (var result in results) {
                foreach (var diagnostic in result.Diagnostics) {
                    production.ReportDiagnostic(diagnostic);
                }

                if (result.Model is { } model) {
                    production.AddSource(
                        HintNameFor(model), new ValidatorEmitter().Emit(model, dispatchesDynamically));
                }

                if (result.Predicates is { } predicates) {
                    production.AddSource(result.PredicateHintName!, predicates);
                }
            }
        });

        var registrationInput = models
            .SelectMany(static (results, _) => results
                .Select(result => result.Model)
                .Where(model => model is not null)
                .Select(model => model!))
            .Collect()
            .Combine(hasDependencyModules)
            .Combine(options)
            .Combine(assemblyNamespace);

        context.RegisterSourceOutput(registrationInput, static (production, input) => {
            var (((collected, hasDm), generatorOptions), ns) = input;

            var mode = generatorOptions.Registration switch {
                "DependencyModules" => RegistrationMode.DependencyModules,
                "ServiceCollection" => RegistrationMode.ServiceCollection,
                "None" => RegistrationMode.None,
                _ => hasDm ? RegistrationMode.DependencyModules : RegistrationMode.ServiceCollection,
            };

            // Ordered by name so the emitted table does not reshuffle between builds, which would
            // otherwise turn every incremental compile into a diff. Namespace first, because the
            // validator name alone is not unique - two namespaces may each declare a Customer.
            var ordered = collected
                .OrderBy(model => model.Namespace, StringComparer.Ordinal)
                .ThenBy(model => model.ValidatorName, StringComparer.Ordinal)
                .ToArray();

            var withAdapters = ordered.Any(model =>
                model.Properties.Any(property => property.Polymorphism == PolymorphismMode.Runtime));

            if (new RegistrationEmitter().Emit(
                    ordered, mode, ns, generatorOptions.Naming, withAdapters) is { } source) {
                production.AddSource("GeneratedValidatorRegistration.g.cs", source);
            }
        });
    }

    /// <summary>
    /// The file name a validator is added under, which Roslyn requires to be unique per generator.
    /// </summary>
    /// <remarks>
    /// Qualified by namespace, because the validator name is not unique on its own: two namespaces
    /// in one assembly may each declare a <c>Customer</c>, and emitting both as
    /// <c>CustomerValidator.g.cs</c> makes <c>AddSource</c> throw - which fails the whole generator,
    /// not just the second type. Versioned model sets make this ordinary rather than exotic:
    /// <c>Api.V1.Customer</c> alongside <c>Api.V2.Customer</c> is the shape §6 of the plan is built
    /// around.
    /// </remarks>
    private static string HintNameFor(ValidatedTypeModel model) =>
        model.Namespace.Length == 0
            ? $"{model.ValidatorName}.g.cs"
            : $"{model.Namespace}.{model.ValidatorName}.g.cs";

    /// <summary>
    /// Reads every candidate, folds each rules class into its target's model, and emits one model per
    /// validated type.
    /// </summary>
    /// <remarks>
    /// The merge is why this is one pass over all candidates rather than a projection per candidate.
    /// A rules class may target a type in this compilation that also carries attributes - in which
    /// case the two sets of rules union onto one validator, §19.7 - or a type from a referenced
    /// assembly, in which case it is the only source of rules that type has. Both fall out of keying
    /// the declarations by target and running the model build once per target.
    /// </remarks>
    private static ImmutableArray<ModelResult> BuildModels(
        ImmutableArray<INamedTypeSymbol> candidates, Compilation compilation, GeneratorOptions options) {

        var results = ImmutableArray.CreateBuilder<ModelResult>();
        var declarations = new List<RulesDeclaration>();
        var rulesFrontEnd = new RulesFrontEnd(options.FieldNamer);
        var plain = new List<INamedTypeSymbol>();
        var subtypes = InvertBaseChains(candidates);

        IReadOnlyList<(INamedTypeSymbol Type, int Depth)> SubtypesOf(INamedTypeSymbol type) =>
            subtypes.TryGetValue(type, out var found)
                ? found
                : Array.Empty<(INamedTypeSymbol, int)>();

        foreach (var candidate in candidates) {
            if (rulesFrontEnd.Build(candidate, compilation) is { } declaration) {
                declarations.Add(declaration);
            } else {
                plain.Add(candidate);
            }
        }

        // Ordinal by name so two rules classes for one type contribute deterministically (§19.7) and
        // the emitted text does not reshuffle between builds.
        declarations.Sort(static (left, right) =>
            string.CompareOrdinal(left.RulesClass.Name, right.RulesClass.Name));

        var byTarget = new Dictionary<INamedTypeSymbol, List<RulesDeclaration>>(SymbolEqualityComparer.Default);

        foreach (var declaration in declarations) {
            if (!byTarget.TryGetValue(declaration.Target, out var list)) {
                byTarget[declaration.Target] = list = new List<RulesDeclaration>();
            }

            list.Add(declaration);

            if (new PredicateEmitter().Emit(declaration) is { } predicates) {
                results.Add(new ModelResult(
                    null,
                    ImmutableArray<Diagnostic>.Empty,
                    predicates,
                    $"{QualifiedName(declaration.RulesClass)}_Rules.g.cs"));
            }
        }

        // Snapshotted before the loop below starts removing from byTarget, because VM0007 asks
        // whether a *nested* type has rules declared anywhere - a question whose answer must not
        // depend on how far through the candidates we happen to be.
        var ruleTargets = new HashSet<INamedTypeSymbol>(byTarget.Keys, SymbolEqualityComparer.Default);
        bool HasRulesClass(INamedTypeSymbol type) => ruleTargets.Contains(type);

        foreach (var candidate in plain) {
            byTarget.TryGetValue(candidate, out var declared);
            byTarget.Remove(candidate);

            if (Build(candidate, declared, compilation, options, HasRulesClass, SubtypesOf) is { } result) {
                results.Add(result);
            }
        }

        // Whatever is left targets a type this compilation does not declare - the case the feature
        // exists for. Its model has no attributes to merge with, only the rules class's own.
        foreach (var pair in byTarget) {
            if (Build((INamedTypeSymbol)pair.Key, pair.Value, compilation, options, HasRulesClass, SubtypesOf) is { } result) {
                results.Add(result);
            }
        }

        results.AddRange(rulesFrontEnd.Diagnostics.Select(static diagnostic =>
            new ModelResult(null, ImmutableArray.Create(diagnostic), null, null)));

        return results.ToImmutable();
    }

    private static ModelResult? Build(
        INamedTypeSymbol target,
        List<RulesDeclaration>? declared,
        Compilation compilation,
        GeneratorOptions options,
        Func<INamedTypeSymbol, bool> hasRulesClass,
        Func<INamedTypeSymbol, IReadOnlyList<(INamedTypeSymbol Type, int Depth)>> subtypesOf) {

        var frontEnd = new AttributeFrontEnd(
            compilation, options.CompileDataAnnotations, options.FieldNamer, options.ResolvedPatternPolicy);

        var model = frontEnd.Build(
            target,
            static type => $"{type.Name}Validator",
            declared?.SelectMany(static declaration => declaration.Rules).ToArray(),
            declared?.SelectMany(static declaration => declaration.AppliedRules).ToArray(),
            hasRulesClass,
            subtypesOf);

        var diagnostics = frontEnd.Diagnostics.ToImmutableArray();

        return model is null && diagnostics.Length == 0
            ? null
            : new ModelResult(model, diagnostics, null, null);
    }

    /// <summary>
    /// Indexes each candidate against every ancestor it has, so that "the subtypes of X" is
    /// answerable without ever enumerating types in a referenced assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walking namespaces across every reference to find derived types would traverse tens of
    /// thousands of symbols per compilation, behind a CompilationProvider that invalidates on every
    /// keystroke. Inverting the chain over the candidates already collected is one pass,
    /// O(types x depth), and multi-level and metadata ancestors both fall out of it for free.
    /// </para>
    /// <para>
    /// Depth is the candidate's own distance from <c>object</c>, not its distance from the ancestor
    /// being indexed against. That is what makes one number order the switch arms correctly when
    /// the dispatch target is an interface: any subtype is strictly deeper than its base, whether
    /// or not the two reach the target by the same route.
    /// </para>
    /// </remarks>
    private static Dictionary<INamedTypeSymbol, List<(INamedTypeSymbol Type, int Depth)>> InvertBaseChains(
        ImmutableArray<INamedTypeSymbol> candidates) {

        var index = new Dictionary<INamedTypeSymbol, List<(INamedTypeSymbol, int)>>(SymbolEqualityComparer.Default);

        foreach (var candidate in candidates) {
            var depth = 0;

            for (INamedTypeSymbol? ancestor = candidate.BaseType;
                 ancestor is not null && ancestor.SpecialType != SpecialType.System_Object;
                 ancestor = ancestor.BaseType) {
                depth++;
            }

            for (INamedTypeSymbol? ancestor = candidate.BaseType;
                 ancestor is not null && ancestor.SpecialType != SpecialType.System_Object;
                 ancestor = ancestor.BaseType) {
                Add(index, ancestor, candidate, depth);
            }

            // Interfaces are dispatch targets too - a [ValidateNested] IPayment is as ordinary as a
            // [ValidateNested] Payment.
            foreach (var contract in candidate.AllInterfaces) {
                Add(index, contract, candidate, depth);
            }
        }

        return index;
    }

    private static void Add(
        Dictionary<INamedTypeSymbol, List<(INamedTypeSymbol, int)>> index,
        INamedTypeSymbol ancestor,
        INamedTypeSymbol candidate,
        int depth) {

        if (!index.TryGetValue(ancestor, out var list)) {
            index[ancestor] = list = new List<(INamedTypeSymbol, int)>();
        }

        list.Add((candidate, depth));
    }

    private static string QualifiedName(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace
            ? type.Name
            : $"{type.ContainingNamespace.ToDisplayString()}.{type.Name}";

    private sealed record ModelResult(
        ValidatedTypeModel? Model,
        ImmutableArray<Diagnostic> Diagnostics,
        string? Predicates,
        string? PredicateHintName);

    private static string SanitizeNamespace(string? assemblyName) {
        if (string.IsNullOrEmpty(assemblyName)) {
            return "Generated";
        }

        var builder = new System.Text.StringBuilder(assemblyName!.Length);

        foreach (var part in assemblyName.Split('.')) {
            if (part.Length == 0) {
                continue;
            }

            if (builder.Length > 0) {
                builder.Append('.');
            }

            if (!char.IsLetter(part[0]) && part[0] != '_') {
                builder.Append('_');
            }

            foreach (var character in part) {
                builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
            }
        }

        return builder.Length == 0 ? "Generated" : builder.ToString();
    }

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private sealed record GeneratorOptions(
        string? Registration, string? Naming, string? DataAnnotations, string? PatternPolicySetting, bool IsAotFacing) {

        /// <summary>
        /// Auto gates on the project's own AOT posture rather than on PublishAot alone. PublishAot
        /// is only ever true in the executable, so a class library holding the models would never
        /// see it - IsAotCompatible is what a library sets when it means to be publishable, and
        /// catching that is the difference between the diagnostic landing on the library's build
        /// and landing on somebody else's publish.
        /// </summary>
        public PatternPolicy ResolvedPatternPolicy => PatternPolicySetting switch {
            "Error" => PatternPolicy.Error,
            "Warn" => PatternPolicy.Warn,
            "Allow" => PatternPolicy.Allow,
            _ => IsAotFacing ? PatternPolicy.Error : PatternPolicy.Allow,
        };

        public bool CompileDataAnnotations =>
            !string.Equals(DataAnnotations, "Ignore", StringComparison.OrdinalIgnoreCase);

        public Func<string, string> FieldNamer => Naming switch {
            "PascalCase" or "AsDeclared" => static name => name,
            "SnakeCase" => SnakeCase,
            _ => CamelCase,
        };

        private static string CamelCase(string name) =>
            name.Length == 0 || !char.IsUpper(name[0])
                ? name
                : char.ToLowerInvariant(name[0]) + name.Substring(1);

        private static string SnakeCase(string name) {
            var builder = new System.Text.StringBuilder(name.Length + 4);

            for (var i = 0; i < name.Length; i++) {
                var character = name[i];

                if (char.IsUpper(character)) {
                    var startsWord = i > 0 &&
                        (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));

                    if (startsWord) {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(character));
                } else {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }
    }
}
