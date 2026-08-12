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

            return new GeneratorOptions(registration, naming, dataAnnotations);
        });

        // Probed once. An IncrementalValueProvider<bool> so downstream stages invalidate only when
        // the answer flips, rather than on every edit to the compilation.
        var hasDependencyModules = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.GetTypeByMetadataName(KnownTypes.DependencyModule) is not null);

        var assemblyNamespace = context.CompilationProvider.Select(static (compilation, _) =>
            string.IsNullOrEmpty(compilation.AssemblyName) ? "ValidationModules.Generated" : compilation.AssemblyName!);

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
                production.AddSource($"{model.ValidatorName}.g.cs", new ValidatorEmitter().Emit(model));
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
            // otherwise turn every incremental compile into a diff.
            var ordered = collected.OrderBy(model => model.ValidatorName, StringComparer.Ordinal).ToArray();

            if (new RegistrationEmitter().Emit(ordered, mode, ns) is { } source) {
                production.AddSource("GeneratedValidatorRegistration.g.cs", source);
            }
        });
    }

    private static ModelResult BuildModel(INamedTypeSymbol symbol, GeneratorOptions options) {
        var frontEnd = new AttributeFrontEnd(options.CompileDataAnnotations, options.FieldNamer);
        var model = frontEnd.Build(symbol, static type => $"{type.Name}Validator");

        return new ModelResult(model, frontEnd.Diagnostics.ToImmutableArray());
    }

    private sealed record ModelResult(ValidatedTypeModel? Model, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record GeneratorOptions(string? Registration, string? Naming, string? DataAnnotations) {

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
