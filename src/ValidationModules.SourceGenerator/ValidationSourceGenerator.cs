using System.Collections.Immutable;
using CSharpAuthor;
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
            provider.GlobalOptions.TryGetValue("build_property.ValidationModules_FailFast", out var failFast);
            provider.GlobalOptions.TryGetValue("build_property.ValidationModules_CaptureValues", out var captureValues);
            provider.GlobalOptions.TryGetValue(Impl.Emitters.GeneratedCodeStyle.BuildProperty, out var codeStyle);
            provider.GlobalOptions.TryGetValue("build_property.PublishAot", out var publishAot);
            provider.GlobalOptions.TryGetValue("build_property.IsAotCompatible", out var aotCompatible);

            return new GeneratorOptions(registration, naming, dataAnnotations, patternPolicy, failFast,
                IsTrue(publishAot) || IsTrue(aotCompatible), codeStyle, captureValues);
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

        // Language packs ride AdditionalFiles, so the same feature serves every provenance: a
        // file in the project, one delivered by a package's props, or a pack author's own build.
        // Item order is preserved, and it is the layering order.
        var languagePackFiles = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(".validation-messages.json", StringComparison.OrdinalIgnoreCase))
            .Select(static (text, token) => new LanguagePackFile(text.Path, text.GetText(token)?.ToString() ?? string.Empty))
            .Collect();

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

        // The settings the validator stage needs, projected together so the stage caches on
        // the tuple rather than re-running on unrelated option edits. Naming rides along for the
        // DataAnnotations bridge: custom rules can report member names at run time, and the
        // emitted namer has to be the policy the literals were baked with.
        var emitterSettings = options.Select(static (option, _) =>
            (option.EmitFailFast, option.CodeStyle, option.Naming, option.CaptureValues));

        context.RegisterSourceOutput(models.Combine(emitterSettings), static (production, input) => {
            var (results, (emitFailFast, codeStyle, naming, captureValues)) = (input.Left, input.Right);

            // An IDynamicValidator adapter is only worth emitting for an assembly that actually
            // dispatches dynamically. Registering one per validated type roots every adapter, so
            // ILC cannot trim them - which would charge every consumer for a mode most never use.
            // Emitted for all of this assembly's types once any of them needs it, so that a miss
            // still means "this assembly never registered" rather than "this type had no rules".
            var dispatchesDynamically = results.Any(result =>
                result.Model is { } model
                && model.Properties.Any(property => property.Polymorphism == PolymorphismMode.Runtime));

            // Built over every model in the compilation, which this loop already has in hand - so
            // knowing which nested descents come back round costs nothing in incrementality. A
            // validator on a cycle cannot take its nested validator as a constructor dependency
            // without making the container refuse to build, and cannot carry the straight-line
            // IsValid without risking the process on cyclic data.
            var nesting = NestingGraph.Build(
                results.Select(result => result.Model).Where(model => model is not null).Select(model => model!));

            foreach (var result in results) {
                foreach (var diagnostic in result.Diagnostics) {
                    production.ReportDiagnostic(diagnostic);
                }

                if (result.Model is { } model) {
                    production.AddSource(
                        HintNameFor(model),
                        new ValidatorEmitter().Emit(
                            model, dispatchesDynamically, emitFailFast, nesting, codeStyle, naming,
                            captureValues));
                }

                if (result.Predicates is { } predicates) {
                    production.AddSource(result.PredicateHintName!, predicates);
                }
            }
        });

        context.RegisterSourceOutput(
            languagePackFiles.Combine(assemblyNamespace).Combine(emitterSettings),
            static (production, input) => {
                var ((files, ns), settings) = input;

                for (var i = 0; i < files.Length; i++) {
                    var outcome = LanguagePackReader.Read(files[i], i);

                    foreach (var diagnostic in outcome.Diagnostics) {
                        production.ReportDiagnostic(diagnostic);
                    }

                    if (outcome.Model is { } pack) {
                        production.AddSource(
                            pack.HintName, new LanguagePackEmitter().Emit(pack, ns, settings.CodeStyle));
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
            .Combine(assemblyNamespace)
            .Combine(languagePackFiles);

        context.RegisterSourceOutput(registrationInput, static (production, input) => {
            var ((((collected, hasDm), generatorOptions), ns), packFiles) = input;

            // Re-read rather than re-plumbed: the read is deterministic and cheap, and carrying
            // the models through a second provider would double-report their diagnostics.
            var languagePacks = new List<LanguagePackModel>(packFiles.Length);

            for (var i = 0; i < packFiles.Length; i++) {
                if (LanguagePackReader.Read(packFiles[i], i).Model is { } pack) {
                    languagePacks.Add(pack);
                }
            }

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
                    ordered, mode, ns, generatorOptions.Naming, withAdapters,
                    generatorOptions.CodeStyle, languagePacks) is { } source) {
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

        // Pre-scanned before any body is read, so an As over a facet whose rules arrive from a
        // rules class later in the candidate list is not accused of having none.
        var declaredTargets = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var candidate in candidates) {
            var contract = candidate.AllInterfaces.FirstOrDefault(i =>
                i.ConstructedFrom.ToDisplayString() == KnownTypes.ValidationRulesForInterface);

            if (contract is { TypeArguments.Length: 1 } &&
                contract.TypeArguments[0] is INamedTypeSymbol declaredTarget) {
                declaredTargets.Add(declaredTarget);
            }
        }

        var rulesFrontEnd = new RulesFrontEnd(options.FieldNamer, declaredTargets.Contains);
        var plain = new List<INamedTypeSymbol>();
        var subtypes = InvertBaseChains(candidates);

        IReadOnlyList<(INamedTypeSymbol Type, int Depth)> SubtypesOf(INamedTypeSymbol type) =>
            subtypes.TryGetValue(type, out var found)
                ? found
                : Array.Empty<(INamedTypeSymbol, int)>();

        foreach (var candidate in candidates) {
            var declared = rulesFrontEnd.Build(candidate, compilation);

            if (declared.Count > 0) {
                declarations.AddRange(declared);
            } else {
                plain.Add(candidate);
            }
        }

        // Ordinal by name so two rules classes for one type contribute deterministically, with the
        // target as tiebreak - a multi-target class contributes several declarations, and an
        // unstable sort must not reshuffle its regions between builds.
        declarations.Sort(static (left, right) => {
            var byClass = string.CompareOrdinal(left.RulesClass.Name, right.RulesClass.Name);

            return byClass != 0 ? byClass : string.CompareOrdinal(left.Target.Name, right.Target.Name);
        });

        var byTarget = new Dictionary<INamedTypeSymbol, List<RulesDeclaration>>(SymbolEqualityComparer.Default);

        foreach (var declaration in declarations) {
            if (!byTarget.TryGetValue(declaration.Target, out var list)) {
                byTarget[declaration.Target] = list = new List<RulesDeclaration>();
            }

            list.Add(declaration);
        }

        // One companion file per rules class, whatever it targets: a multi-target class becomes
        // one container of Describe overloads, and one hint name.
        foreach (var companion in declarations
                     .GroupBy(static declaration => declaration.RulesClass, SymbolEqualityComparer.Default)) {
            results.Add(new ModelResult(
                null,
                ImmutableArray<Diagnostic>.Empty,
                new RegionEmitter().EmitRegion(companion.ToList(), options.CodeStyle),
                $"{QualifiedName((INamedTypeSymbol)companion.Key!)}_Rules.g.cs"));
        }

        // Fragment containers are shared across every rules class that called into them, so they
        // are emitted once per pass, after every candidate has been read.
        foreach (var container in rulesFrontEnd.FragmentContainers) {
            if (new RegionEmitter().EmitFragments(container, options.CodeStyle) is { } fragments) {
                var hint = container.Namespace.Length == 0
                    ? $"{container.Name}.g.cs"
                    : $"{container.Namespace}.{container.Name}.g.cs";

                results.Add(new ModelResult(null, ImmutableArray<Diagnostic>.Empty, fragments, hint));
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
            // A region's descents merge as nesting-only rules, so the validator grows the injected
            // machinery the region call passes; the walk itself lives in the region's text.
            declared?.SelectMany(static declaration => declaration.Dependencies
                .Select(static dependency => new DeclaredRule(
                    dependency.Property, null,
                    null,
                    dependency.Elements ? Nesting.Elements : Nesting.Object)))
                .ToArray(),
            declared?.SelectMany(static declaration => declaration.AppliedRules).ToArray(),
            hasRulesClass,
            subtypesOf,
            declared?.Select(static declaration => new RegionModel(
                CompanionQualifiedName(declaration.RulesClass),
                "Describe",
                new EquatableArray<string>(ImmutableArray.CreateRange(
                    declaration.Dependencies.Select(static dependency => dependency.AccessorName)))))
                .ToArray());

        var diagnostics = frontEnd.Diagnostics.ToImmutableArray();

        return model is null && diagnostics.Length == 0
            ? null
            : new ModelResult(model, diagnostics, null, null);
    }

    private static string CompanionQualifiedName(INamedTypeSymbol rulesClass) =>
        rulesClass.ContainingNamespace.IsGlobalNamespace
            ? $"global::{RegionEmitter.CompanionFor(rulesClass)}"
            : $"global::{rulesClass.ContainingNamespace.ToDisplayString()}.{RegionEmitter.CompanionFor(rulesClass)}";

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
        string? Registration, string? Naming, string? DataAnnotations, string? PatternPolicySetting,
        string? FailFastSetting, bool IsAotFacing, string? CodeStyleSetting = null,
        string? CaptureValuesSetting = null) {

        /// <summary>
        /// Whether report sites pass the failing member as <c>ValidationError.Value</c>. On by
        /// default - the value is a reference to data the application already holds, and no
        /// library surface renders it. <c>ValidationModules_CaptureValues=false</c> (or
        /// <c>Disabled</c>) makes the emitter pass nothing, so the capture is provably absent from
        /// the compiled binary - the guarantee regulated builds actually want, and one a runtime
        /// switch cannot give. Both spellings accepted for the same reason FailFast takes both.
        /// </summary>
        public bool CaptureValues =>
            !string.Equals(CaptureValuesSetting, "Disabled", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(CaptureValuesSetting, "false", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The brace style generated files are written in, from the shared
        /// <c>GeneratedCodeStyle</c> property. Allman unless the project says otherwise.
        /// </summary>
        public BraceStyle CodeStyle => Impl.Emitters.GeneratedCodeStyle.Parse(CodeStyleSetting);

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

        /// <summary>
        /// Whether generated validators return at the first blocking failure, which is what makes
        /// <c>ValidationStopMode.StopOnFirstError</c> skip work rather than only shorten its answer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On unless the project turns it off, because a validator that cannot stop makes the mode
        /// a filter rather than an optimization - and a consumer who never sets the mode is the one
        /// who would have to notice the difference, which is backwards.
        /// </para>
        /// <para>
        /// Off costs nothing and loses nothing but the skipping: the collector still stops
        /// recording, so <c>ValidateFirst</c> returns the same single error either way. Measured at
        /// 54 bytes per report site on an osx-arm64 Native AOT publish - 27 KB across 500 sites,
        /// 1.1% of that binary - which is the number to weigh when turning it off.
        /// </para>
        /// <para>
        /// Both <c>Disabled</c> and <c>false</c> are accepted, case-insensitively. Taking only one
        /// spelling would let the other pass silently, and silently paying for a feature you asked
        /// to drop is the failure this property exists to avoid.
        /// </para>
        /// </remarks>
        public bool EmitFailFast =>
            !string.Equals(FailFastSetting, "Disabled", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(FailFastSetting, "false", StringComparison.OrdinalIgnoreCase);

        public Func<string, string> FieldNamer => Naming switch {
            "PascalCase" or "AsDeclared" => static name => name,
            "SnakeCase" => SnakeCase,
            _ => CamelCase,
        };

        /// <summary>
        /// <c>JsonNamingPolicy.CamelCase</c>'s algorithm, which is what the runtime's
        /// <c>CamelCaseFieldNamer</c> also implements. The two have to agree exactly: this one
        /// names the field baked into generated code and that one names it for anything reached
        /// through the adapter, so a divergence gives the same property two spellings depending on
        /// which engine found the error.
        /// </summary>
        private static string CamelCase(string name) {
            if (name.Length == 0 || !char.IsUpper(name[0])) {
                return name;
            }

            var characters = name.ToCharArray();

            for (var i = 0; i < characters.Length; i++) {
                if (i == 1 && !char.IsUpper(characters[i])) {
                    break;
                }

                var hasNext = i + 1 < characters.Length;

                if (i > 0 && hasNext && !char.IsUpper(characters[i + 1])) {
                    if (characters[i + 1] == ' ') {
                        characters[i] = char.ToLowerInvariant(characters[i]);
                    }

                    break;
                }

                characters[i] = char.ToLowerInvariant(characters[i]);
            }

            return new string(characters);
        }

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
