using ValidationModules.SourceGenerator.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The <c>GeneratedCodeStyle</c> build property: Allman unless the project says otherwise.
/// </summary>
/// <remarks>
/// The property is deliberately unprefixed - DependencyModules reads the same one - so one csproj
/// line styles every generator's output. The readings must match, which is why the accepted
/// values and the silent fallback are pinned here rather than left to prose.
/// </remarks>
public class GeneratedCodeStyleTests
{
    private const string Source = """
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed record Pet {
            [Required] public string? Name { get; init; }
        }
        """;

    private const string Declaration =
        "public sealed partial class PetValidator : global::ValidationModules.IValidatorFor<global::Sample.Pet>";

    private static string Validator(params (string Key, string Value)[] properties)
    {
        var result = GeneratorHarness.Run(Source, properties);

        Assert.Empty(result.CompilationErrors);

        return result.Sources["Sample.PetValidator.g.cs"];
    }

    [Fact]
    public void Default_IsAllman()
    {
        Assert.Contains($"{Declaration}\n{{", Validator());
    }

    [Theory]
    [InlineData("KAndR")]
    [InlineData("kandr")]
    [InlineData("K&R")]
    [InlineData(" k&r ")]
    public void KAndR_PutsTheBraceOnTheDeclarationLine(string value)
    {
        Assert.Contains($"{Declaration} {{", Validator(("GeneratedCodeStyle", value)));
    }

    /// <summary>
    /// Falling back rather than diagnosing matches DependencyModules' reading of the shared
    /// property, and the value only moves braces - a typo cannot change what the code does.
    /// </summary>
    [Fact]
    public void UnknownValue_FallsBackToAllman()
    {
        Assert.Contains($"{Declaration}\n{{", Validator(("GeneratedCodeStyle", "Whitesmiths")));
    }

    /// <summary>
    /// One property styles every file the generator writes, not just the validators.
    /// </summary>
    [Fact]
    public void TheRegistrationAndTheValidator_AgreeOnTheStyle()
    {
        var result = GeneratorHarness.Run(Source, ("GeneratedCodeStyle", "KAndR"));

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "namespace Microsoft.Extensions.DependencyInjection {",
            result.Sources["GeneratedValidatorRegistration.g.cs"]
        );
    }

    private const string FrenchPack = """
        { "culture": "fr", "templates": { "required": "{field} est obligatoire." } }
        """;

    private const string EntriesDeclaration =
        "private static readonly global::System.Collections.Generic.KeyValuePair<string,string>[] "
        + "Entries = new global::System.Collections.Generic.KeyValuePair<string,string>";

    /// <summary>
    /// A language pack's entries initializer used to be fixed text with its braces on their own
    /// lines, whatever the style said.
    /// </summary>
    [Theory]
    [InlineData(
        "Allman",
        "[]\n        {\n            new(\"required\", \"{field} est obligatoire.\"),\n        };\n"
    )]
    [InlineData(
        "KAndR",
        "[] {\n            new(\"required\", \"{field} est obligatoire.\"),\n        };\n"
    )]
    public void TheLanguagePackInitializer_FollowsTheStyle(string style, string layout)
    {
        var result = GeneratorHarness.RunWithFiles(
            Source,
            [("fr.validation-messages.json", FrenchPack)],
            ("GeneratedCodeStyle", style)
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(EntriesDeclaration + layout, result.Sources["LanguagePack.fr.0.g.cs"]);
    }

    private const string Rules = """
        using ValidationModules;

        namespace Sample;

        public record Pet { public string? Name { get; init; } public int Age { get; init; } }

        public sealed class PetRules : IValidationRulesFor<Pet>
        {
            public static void Describe(ValidationRules<Pet> rules, Pet x)
            {
                if (x.Age > 1) { rules.Require(x.Name); }
            }
        }
        """;

    private const string AllmanRegion = """
                if (x.Age > 1)
                {
                    if (string.IsNullOrWhiteSpace(x.Name) && global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, "name").ShouldStop)
                    {
                        return global::ValidationModules.ValidationFlow.Stop;
                    }
                }
                return global::ValidationModules.ValidationFlow.Continue;
        """;

    private const string KAndRRegion = """
                if (x.Age > 1) {
                    if (string.IsNullOrWhiteSpace(x.Name) && global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, "name").ShouldStop) {
                        return global::ValidationModules.ValidationFlow.Stop;
                    }
                }
                return global::ValidationModules.ValidationFlow.Continue;
        """;

    /// <summary>
    /// A rules class's region used to be written as lines of text with their own braces, so its
    /// blocks were K&amp;R whatever the style said.
    /// </summary>
    [Theory]
    [InlineData(null, AllmanRegion)]
    [InlineData("KAndR", KAndRRegion)]
    public void TheRegion_FollowsTheStyle(string? style, string layout)
    {
        var result = style is null
            ? GeneratorHarness.Run(Rules)
            : GeneratorHarness.Run(Rules, ("GeneratedCodeStyle", style));

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(layout, result.Sources["Sample.PetRules_Rules.g.cs"]);
    }

    /// <summary>
    /// Every statement form the reader transcribes, with islands in its branches. The local
    /// function is copied whole, so its body carries statements the reader itself refuses.
    /// </summary>
    private const string EveryForm = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using ValidationModules;

        namespace Sample;

        public record Pet
        {
            public string? Name { get; init; }
            public int Age { get; init; }
            public List<string>? Tags { get; init; }
        }

        public sealed class PetRules : IValidationRulesFor<Pet>
        {
            public static void Describe(ValidationRules<Pet> rules, Pet x)
            {
                var count = 0;

                if (x.Age > 1) { rules.Require(x.Name).Length(2, 40); }
                else if (x.Age < 0) rules.Range(x.Age, 0, 30);
                else { count++; }

                switch (x.Age)
                {
                    case 1:
                    case 2:
                        rules.Require(x.Name);
                        break;
                    default:
                        count--;
                        break;
                }

                for (var i = 0; i < 3; i++) { count += i; }
                foreach (string tag in x.Tags ?? new List<string>()) { rules.Context.Report("tags", "tag", tag); }
                while (count > 10) count--;
                do { count++; } while (count < 2);
                { var copy = count; count = copy + 1; }

                int Twice(int value)
                {
                    if (value > 100) return 0;
                    using (var reader = new StringReader(x.Name ?? ""))
                    {
                        value += reader.Read();
                    }
                    try { checked { value *= 2; } }
                    catch (OverflowException) { value = 0; }
                    finally { count++; }
                    return value;
                }

                if (x.Tags?.Any(tag => { return tag.Length > Twice(count); }) == true) { rules.Require(x.Name); }
                rules.Count(x.Tags, 0, 5).Each().Length(1, 20);
                rules.Ensure(x.Age >= count, code: "age_count");
            }
        }
        """;

    private static string EveryFormRegion(params (string Key, string Value)[] properties)
    {
        var result = GeneratorHarness.Run(EveryForm, properties);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
        );

        return result.Sources["Sample.PetRules_Rules.g.cs"];
    }

    [Fact]
    public void EveryTranscribedForm_IsAllmanByDefault()
    {
        Snapshot.Match(EveryFormRegion());
    }

    [Fact]
    public void EveryTranscribedForm_FollowsKAndR()
    {
        Snapshot.Match(EveryFormRegion(("GeneratedCodeStyle", "KAndR")));
    }

    /// <summary>
    /// A line break inside a verbatim string is part of its value, so no indent is written after
    /// it. A transcribed declaration used to have one written there.
    /// </summary>
    [Fact]
    public void ALineBreakInsideAString_GetsNoIndent()
    {
        var result = GeneratorHarness.Run(
            Rules.Replace(
                "if (x.Age > 1) { rules.Require(x.Name); }",
                "var note = @\"first\nsecond\";\n        if (note.Length > x.Age) { rules.Require(x.Name); }"
            )
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "        var note = @\"first\nsecond\";\n",
            result.Sources["Sample.PetRules_Rules.g.cs"]
        );
    }

    /// <summary>
    /// A local function is split into a block and its statements, except one holding a directive,
    /// whose <c>#if</c> and <c>#endif</c> can sit on tokens that land in different statements.
    /// </summary>
    [Fact]
    public void ALocalFunctionHoldingADirective_IsCopiedWhole()
    {
        var result = GeneratorHarness.Run(
            Rules.Replace(
                "if (x.Age > 1) { rules.Require(x.Name); }",
                """
                int Limit()
                        {
                #if DEBUG
                            return 1;
                #else
                            return 2;
                #endif
                        }
                        if (x.Age > Limit()) { rules.Require(x.Name); }
                """
            ),
            ("GeneratedCodeStyle", "KAndR")
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("#endif", result.Sources["Sample.PetRules_Rules.g.cs"]);
    }
}
