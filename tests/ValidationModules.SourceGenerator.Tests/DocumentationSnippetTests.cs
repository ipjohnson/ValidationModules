using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Compiles the documentation's C# samples against the generator that is actually in the tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The registration emitter was rewritten - a table of
/// <c>ValidatorRegistration</c> records became an <c>IServiceCollection</c> extension - and the
/// generated <c>static Instance</c> field was dropped. The docs kept teaching both. Nine of
/// eighteen pages told a new reader to write two statements, and both were compile errors: the
/// first two statements anybody writes.
/// </para>
/// <para>
/// Nothing caught it because documentation is prose to a build. Running the real generator over the
/// samples makes them code, so the next emitter change either updates them or fails here.
/// </para>
/// <para>
/// <b>Opt-in, by a marker above the fence.</b> Plenty of samples cannot compile and should not:
/// emitted output, deliberately-wrong code next to the diagnostic it raises, fragments of a method
/// body. Verifying everything would mean contorting those to satisfy a compiler, so a sample is
/// checked only when it says it can be:
/// </para>
/// <code>
/// &lt;!-- verify --&gt;          self-contained; usings are supplied
/// &lt;!-- verify:models --&gt;   the same, plus the Pet/Address/Toy models the guide narrates
/// &lt;!-- verify:bare --&gt;     without ValidationModules.Constraints, for a sample that imports
///                          System.ComponentModel.DataAnnotations instead
/// </code>
/// </remarks>
public class DocumentationSnippetTests {

    /// <summary>The usings every sample gets, so a page can show the interesting line alone.</summary>
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Text.RegularExpressions;
        using Microsoft.Extensions.DependencyInjection;
        using ValidationModules;
        using ValidationModules.Naming;

        """;

    /// <summary>
    /// The constraints namespace, which most samples want and a few must not have.
    /// </summary>
    /// <remarks>
    /// Kept out of <see cref="Preamble"/> and added by the marker, because five constraint names
    /// collide with <c>System.ComponentModel.DataAnnotations</c> - <c>Required</c>,
    /// <c>StringLength</c>, <c>Range</c>, <c>AllowedValues</c> and the length family. That collision
    /// is the reason the constraints live in their own namespace at all, and a sample on the
    /// DataAnnotations page has to be able to import the other one alone. <c>verify:bare</c> is how
    /// it says so.
    /// </remarks>
    private const string ConstraintsUsing = "using ValidationModules.Constraints;\n\n";

    /// <summary>
    /// The models the guide narrates, appended for <c>verify:models</c> samples.
    /// </summary>
    /// <remarks>
    /// Kept here rather than repeated in every page, because a sample that has to redeclare Pet to
    /// be checkable is a worse sample. The names match what the guide uses throughout.
    /// </remarks>
    private const string Models = """

        public record Pet {
            [Required, StringLength(min: 1, max: 100)] public string? Name { get; init; }
            [Range(0, 30)] public int Age { get; init; }
            [Pattern("^[A-Z]{3}$")] public string? Sku { get; init; }
            [AllowedValues("available", "pending", "sold")] public string? Status { get; init; }
            [ValidateNested] public Address? Home { get; init; }
            [ItemCount(min: 1, max: 10), ValidateNested] public IReadOnlyList<Toy> Toys { get; init; } = [];
        }

        public record Address {
            [Required] public string? PostalCode { get; init; }
        }

        public record Toy {
            [Required] public string? Name { get; init; }
        }
        """;

    private static readonly Regex Fence = new(
        @"<!--\s*verify(?<mode>:models|:bare)?\s*-->\s*\r?\n```csharp\r?\n(?<code>.*?)\r?\n```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static TheoryData<string, string, string> Snippets() {
        var data = new TheoryData<string, string, string>();

        foreach (var file in Directory.EnumerateFiles(WebsiteRoot, "*.md", SearchOption.AllDirectories)) {
            if (file.Contains("node_modules", StringComparison.Ordinal)) {
                continue;
            }

            var text = File.ReadAllText(file);
            var page = Path.GetRelativePath(WebsiteRoot, file);
            var index = 0;

            foreach (Match match in Fence.Matches(text)) {
                data.Add($"{page}#{index++}", match.Groups["code"].Value, match.Groups["mode"].Value);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Snippets))]
    public void EverySampleMarkedVerifiable_Compiles(string where, string code, string mode) {
        var source = new StringBuilder(Preamble);

        if (mode != ":bare") {
            source.Append(ConstraintsUsing);
        }

        source.Append(code);

        if (mode == ":models") {
            source.AppendLine().Append(Models);
        }

        // Both output kinds, because a sample is either statements or declarations and the two
        // disagree about which is an error: a library reports CS8805 for top-level statements, a
        // console application reports CS5001 for a source that only declares types. Passing under
        // either means the sample is valid C# in the shape it is written.
        var asProgram = Errors(source.ToString(), OutputKind.ConsoleApplication);

        if (asProgram.Count == 0) {
            return;
        }

        var asLibrary = Errors(source.ToString(), OutputKind.DynamicallyLinkedLibrary);

        if (asLibrary.Count == 0) {
            return;
        }

        var errors = asProgram.Count <= asLibrary.Count ? asProgram : asLibrary;

        Assert.Fail($"{where} does not compile:\n  {string.Join("\n  ", errors)}");
    }

    private static List<string> Errors(string source, OutputKind outputKind) {
        var result = GeneratorHarness.Run(source, "Sample", outputKind);

        return result.CompilationErrors
            .Concat(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void TheDocumentationHasSamplesUnderVerification() {
        // A marker typo would silently empty the theory and turn this whole file green, which is the
        // failure mode a guard like this actually has.
        Assert.True(Snippets().Count >= 12, $"only {Snippets().Count} samples are being verified");
    }

    private static string WebsiteRoot { get; } = ResolveWebsiteRoot();

    private static string ResolveWebsiteRoot() {
        var configured = typeof(DocumentationSnippetTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryRoot")?.Value;

        if (configured is null || !Directory.Exists(Path.Combine(configured, "website"))) {
            throw new InvalidOperationException(
                "RepositoryRoot assembly metadata is missing or does not contain website/.");
        }

        return Path.Combine(configured, "website");
    }
}
