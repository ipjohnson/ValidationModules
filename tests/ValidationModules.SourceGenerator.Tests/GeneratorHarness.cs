using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Runs the generator over a source string and hands back what it produced.
/// </summary>
/// <remarks>
/// Diagnostics are most of what a generator is, and the only way to test one is to drive it - a
/// project that fails to build cannot also be a test project. This is also the harness golden-file
/// tests over emitted text will use.
/// </remarks>
public static class GeneratorHarness {

    public sealed record Result(
        ImmutableArray<Diagnostic> Diagnostics,
        IReadOnlyDictionary<string, string> Sources,
        ImmutableArray<Diagnostic> CompilationErrors);

    public static Result Run(string source, params (string Key, string Value)[] buildProperties) {
        // Touch the types first: GetAssemblies returns only what is already loaded, and nothing in
        // a test has any reason to have loaded the runtime or the regex library before this point.
        // Without them the attributes do not bind, the front end sees an unannotated type, and every
        // assertion fails for a reason that has nothing to do with what is under test.
        var seeds = new[] {
            typeof(global::ValidationModules.Constraints.RequiredAttribute).Assembly,
            typeof(global::ValidationModules.IValidatorFor<>).Assembly,
            typeof(System.Text.RegularExpressions.Regex).Assembly,
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute).Assembly,
            typeof(object).Assembly,

            // System.ComponentModel carries the type-forward for IServiceProvider, which the emitted
            // registration table's factory delegates take. Without it the validators compile and the
            // registration does not, so CompilationErrors reports a missing reference on every run
            // and can never be asserted empty.
            Assembly.Load("System.ComponentModel"),
            Assembly.Load("System.Runtime"),
        };

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Concat(seeds)
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(location => (MetadataReference)MetadataReference.CreateFromFile(location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new ValidationSourceGenerator())
            .WithUpdatedAnalyzerConfigOptions(new OptionsProvider(buildProperties))
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        _ = driver;

        var sources = output.SyntaxTrees
            .Where(tree => tree.FilePath.EndsWith(".g.cs", StringComparison.Ordinal))
            .ToDictionary(
                tree => Path.GetFileName(tree.FilePath),
                tree => tree.ToString());

        // Only errors: the synthetic compilation has no entry point and other benign warnings.
        var compilationErrors = output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return new Result(diagnostics, sources, compilationErrors);
    }

    private sealed class OptionsProvider : AnalyzerConfigOptionsProvider {
        public OptionsProvider((string Key, string Value)[] properties) {
            GlobalOptions = new Options(properties.ToDictionary(
                property => $"build_property.{property.Key}",
                property => property.Value));
        }

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

        private sealed class Options : AnalyzerConfigOptions {
            private readonly Dictionary<string, string> _values;

            public Options(Dictionary<string, string> values) {
                _values = values;
            }

            public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
        }
    }
}
