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

        var models = candidates
            .Combine(options)
            .Select(static (pair, _) => BuildModel(pair.Left, pair.Right))
            .Where(static result => result.Model is not null || result.Diagnostics.Length > 0);

        context.RegisterSourceOutput(models, static (production, result) => {
            foreach (var diagnostic in result.Diagnostics) {
                production.ReportDiagnostic(diagnostic);
            }

            if (result.Model is { } model) {
                production.AddSource(HintNameFor(model), new ValidatorEmitter().Emit(model));
            }
        });

        var registrationInput = models
            .Select(static (result, _) => result.Model)
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
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

            if (new RegistrationEmitter().Emit(ordered, mode, ns) is { } source) {
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

    private static ModelResult BuildModel(INamedTypeSymbol symbol, GeneratorOptions options) {
        var frontEnd = new AttributeFrontEnd(options.CompileDataAnnotations, options.FieldNamer, options.ResolvedPatternPolicy);
        var model = frontEnd.Build(symbol, static type => $"{type.Name}Validator");

        return new ModelResult(model, frontEnd.Diagnostics.ToImmutableArray());
    }

    private sealed record ModelResult(ValidatedTypeModel? Model, ImmutableArray<Diagnostic> Diagnostics);

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
