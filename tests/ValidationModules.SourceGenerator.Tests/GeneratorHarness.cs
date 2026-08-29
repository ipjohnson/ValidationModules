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

    public static Result Run(string source, params (string Key, string Value)[] buildProperties) =>
        Run(source, assemblyName: "GeneratorTests", buildProperties);

    /// <summary>
    /// Compiles <paramref name="referencedSource"/> to an assembly, then runs the generator over
    /// <paramref name="source"/> with that assembly referenced as metadata.
    /// </summary>
    /// <remarks>
    /// Two source files in one compilation are not a test of anything cross-assembly: symbols from
    /// the same compilation carry their syntax with them, so a walk that only works because it can
    /// reach a declaration would still pass. Going through metadata is the whole point - a base
    /// type from a NuGet package is the case that matters, and it is reached through
    /// <c>MetadataReference</c> and nothing else.
    /// </remarks>
    public static Result RunWithReference(
        string referencedSource,
        string source,
        string referencedAssemblyName = "ReferencedTypes",
        params (string Key, string Value)[] buildProperties) {

        var reference = CompileToReference(referencedSource, referencedAssemblyName);

        return Run(
            source,
            "GeneratorTests",
            OutputKind.DynamicallyLinkedLibrary,
            new[] { reference },
            buildProperties);
    }

    /// <summary>
    /// Builds <paramref name="source"/> into an in-memory assembly and hands back a reference to
    /// it. Throws rather than returning diagnostics: a broken fixture is a broken test, not a
    /// result worth asserting on.
    /// </summary>
    public static MetadataReference CompileToReference(string source, string assemblyName) {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            BaseReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        if (!result.Success) {
            var errors = string.Join(
                Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

            throw new InvalidOperationException(
                $"The referenced fixture assembly '{assemblyName}' did not compile:{Environment.NewLine}{errors}");
        }

        stream.Position = 0;
        return MetadataReference.CreateFromStream(stream);
    }

    /// <param name="assemblyName">
    /// The compilation's assembly name, which the registration emitter derives its namespace from.
    /// Worth varying: an assembly name is not necessarily a valid namespace.
    /// </param>
    public static Result Run(
        string source, string assemblyName, params (string Key, string Value)[] buildProperties) =>
        Run(source, assemblyName, OutputKind.DynamicallyLinkedLibrary, buildProperties);

    /// <param name="outputKind">
    /// <c>ConsoleApplication</c> for a source with top-level statements, which is what the
    /// documentation samples mostly are. A library compilation reports CS8805 for those, and a
    /// console one reports CS5001 for a source that only declares types, so the caller picks.
    /// </param>
    public static Result Run(
        string source,
        string assemblyName,
        OutputKind outputKind,
        params (string Key, string Value)[] buildProperties) =>
        Run(source, assemblyName, outputKind, Array.Empty<MetadataReference>(), buildProperties);

    /// <param name="extraReferences">
    /// Assemblies to reference beyond the ambient set - the cross-assembly cases build these with
    /// <see cref="CompileToReference"/>.
    /// </param>
    /// <summary>
    /// Runs the generator with additional files beside the source - the language-pack pipeline's
    /// input. Paths are as given, so tests exercise the name conventions too.
    /// </summary>
    public static Result RunWithFiles(
        string source,
        IReadOnlyCollection<(string Path, string Content)> additionalFiles,
        params (string Key, string Value)[] buildProperties) =>
        Run(source, "GeneratorTests", OutputKind.DynamicallyLinkedLibrary,
            Array.Empty<MetadataReference>(), buildProperties, additionalFiles);

    public static Result Run(
        string source,
        string assemblyName,
        OutputKind outputKind,
        IReadOnlyCollection<MetadataReference> extraReferences,
        params (string Key, string Value)[] buildProperties) =>
        Run(source, assemblyName, outputKind, extraReferences, buildProperties, additionalFiles: null);

    private static Result Run(
        string source,
        string assemblyName,
        OutputKind outputKind,
        IReadOnlyCollection<MetadataReference> extraReferences,
        (string Key, string Value)[] buildProperties,
        IReadOnlyCollection<(string Path, string Content)>? additionalFiles) {
        var references = BaseReferences();
        references.AddRange(extraReferences);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(outputKind, nullableContextOptions: NullableContextOptions.Enable));

        var texts = (additionalFiles ?? Array.Empty<(string, string)>())
            .Select(static file => (AdditionalText)new InMemoryAdditionalText(file.Item1, file.Item2))
            .ToImmutableArray();

        var driver = CSharpGeneratorDriver
            .Create(new ValidationSourceGenerator())
            .AddAdditionalTexts(texts)
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

    /// <summary>
    /// The ambient reference set plus <paramref name="extra"/>, for tests driving Roslyn directly
    /// rather than through <see cref="Run(string, ValueTuple{string, string}[])"/>.
    /// </summary>
    public static List<MetadataReference> ReferencesIncluding(params MetadataReference[] extra) {
        var references = BaseReferences();
        references.AddRange(extra);
        return references;
    }

    private static List<MetadataReference> BaseReferences() {
        // Touch the types first: GetAssemblies returns only what is already loaded, and nothing in
        // a test has any reason to have loaded the runtime or the regex library before this point.
        // Without them the attributes do not bind, the front end sees an unannotated type, and every
        // assertion fails for a reason that has nothing to do with what is under test.
        var seeds = new[] {
            typeof(global::ValidationModules.Constraints.RequiredAttribute).Assembly,
            typeof(global::ValidationModules.IValidatorFor<>).Assembly,
            typeof(System.Text.RegularExpressions.Regex).Assembly,
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute).Assembly,

            // [JsonPropertyName] overrides the derived field name, so the field-naming tests need
            // it bound rather than reported as a missing type.
            typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly,

            // The emitted registration is an IServiceCollection extension calling TryAddSingleton,
            // so the abstractions have to be bound or every registration test reports a missing
            // type rather than whatever it was actually asserting.
            typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly,

            // The concrete container, not just the abstractions: the documentation samples build a
            // provider and resolve from it, which is what a reader is going to do. Named through
            // ServiceCollectionContainerBuilderExtensions rather than ServiceCollection, because
            // the latter lives in the abstractions and BuildServiceProvider does not.
            typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions).Assembly,
            typeof(object).Assembly,

            // System.ComponentModel carries the type-forward for IServiceProvider, which the emitted
            // registration table's factory delegates take. Without it the validators compile and the
            // registration does not, so CompilationErrors reports a missing reference on every run
            // and can never be asserted empty.
            Assembly.Load("System.ComponentModel"),
            Assembly.Load("System.Runtime"),
        };

        return AppDomain.CurrentDomain.GetAssemblies()
            .Concat(seeds)
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly => assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(location => (MetadataReference)MetadataReference.CreateFromFile(location))
            .ToList();
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

    /// <summary>An additional file that exists only in the test, path and all.</summary>
    private sealed class InMemoryAdditionalText : AdditionalText {
        private readonly Microsoft.CodeAnalysis.Text.SourceText _text;

        public InMemoryAdditionalText(string path, string content) {
            Path = path;
            _text = Microsoft.CodeAnalysis.Text.SourceText.From(content);
        }

        public override string Path { get; }

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}
