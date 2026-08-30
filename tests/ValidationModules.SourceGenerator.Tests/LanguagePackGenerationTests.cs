using ValidationModules.SourceGenerator.Impl.FrontEnds;
using ValidationModules.SourceGenerator.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The language-pack pipeline: JSON in <c>AdditionalFiles</c>, a sealed
/// <c>IValidationLanguagePack</c> out, registration folded into the assembly extension - and the
/// diagnostic suite that is the reason packs compile instead of load.
/// </summary>
public class LanguagePackGenerationTests {

    private const string Model = """
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed record Pet {
            [Required]
            [StringLength(min: 1, max: 100)]
            public string? Name { get; init; }
        }
        """;

    private const string FullFrench = """
        {
            "culture": "fr",
            "templates": {
                "required": "{field} est obligatoire.",
                "string_length.between": "{field} doit contenir entre {0} et {1} caractères.",
                "date_order": "la date de fin doit suivre la date de début."
            }
        }
        """;

    [Fact]
    public void APackFile_BecomesAClassAndARegistration() {
        var result = GeneratorHarness.RunWithFiles(Model, [("packs/fr.validation-messages.json", FullFrench)]);

        Assert.Empty(result.CompilationErrors);

        var pack = result.Sources["LanguagePack.fr.0.g.cs"];

        Assert.Contains("internal sealed class FrLanguagePack0 : global::ValidationModules.IValidationLanguagePack", pack);
        Assert.Contains("new(\"string_length.between\", \"{field} doit contenir entre {0} et {1} caractères.\")", pack);
        Assert.Contains("Templates => Entries;", pack);

        var registration = result.Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.Contains("AddSingleton<global::ValidationModules.IValidationLanguagePack, global::GeneratorTests.FrLanguagePack0>", registration);
        Assert.Contains("TryAddSingleton<global::ValidationModules.ValidationMessageFormatter>", registration);
        Assert.Contains("new global::ValidationModules.LanguagePackFormatter", registration);
    }

    [Fact]
    public void APackOnlyAssembly_StillGetsItsRegistration() {
        var result = GeneratorHarness.RunWithFiles(
            "namespace Sample { public class Nothing { } }",
            [("fr.validation-messages.json", FullFrench)]);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("AddGeneratorTestsValidators", result.Sources["GeneratedValidatorRegistration.g.cs"]);
    }

    [Fact]
    public void MalformedJson_IsVM0100_AndTheFileIsSkipped() {
        var result = GeneratorHarness.RunWithFiles(Model, [("fr.validation-messages.json", "{ not json")]);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0100");

        Assert.Contains("fr.validation-messages.json", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Sources.Keys, name => name.StartsWith("LanguagePack."));
    }

    [Fact]
    public void MissingCulture_IsVM0100() {
        var result = GeneratorHarness.RunWithFiles(
            Model, [("fr.validation-messages.json", """{ "templates": { "required": "x" } }""")]);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0100" && d.GetMessage().Contains("culture"));
    }

    [Fact]
    public void AMisspelledShapeKey_IsVM0101_WithTheNearestMatch() {
        var result = GeneratorHarness.RunWithFiles(Model, [("fr.validation-messages.json", """
            { "culture": "fr", "templates": { "string_length.atmost": "{field} : {0} max." } }
            """)]);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0101");

        Assert.Contains("string_length.at_most", diagnostic.GetMessage());
        Assert.DoesNotContain("atmost", result.Sources["LanguagePack.fr.0.g.cs"]);
    }

    [Fact]
    public void AHoleBeyondTheShapesArguments_IsVM0102_AndTheEntryIsSkipped() {
        var result = GeneratorHarness.RunWithFiles(Model, [("fr.validation-messages.json", """
            { "culture": "fr", "templates": { "string_length.at_most": "{field} doit … {1}." } }
            """)]);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0102");

        Assert.Contains("{1}", diagnostic.GetMessage());
        Assert.Contains("1 argument", diagnostic.GetMessage());
    }

    [Fact]
    public void ADuplicateKey_IsVM0103() {
        var result = GeneratorHarness.RunWithFiles(Model, [("fr.validation-messages.json", """
            { "culture": "fr", "templates": { "required": "a", "required": "b" } }
            """)]);

        Assert.Single(result.Diagnostics, d => d.Id == "VM0103");
        Assert.Contains("new(\"required\", \"a\")", result.Sources["LanguagePack.fr.0.g.cs"]);
        Assert.DoesNotContain(", \"b\")", result.Sources["LanguagePack.fr.0.g.cs"]);
    }

    [Fact]
    public void AFileNamedForOneCulture_DeclaringAnother_IsVM0104_AndTheBodyWins() {
        var result = GeneratorHarness.RunWithFiles(Model, [("packs/de.validation-messages.json", FullFrench)]);

        Assert.Single(result.Diagnostics, d => d.Id == "VM0104");
        Assert.Contains("Culture => \"fr\";", result.Sources["LanguagePack.fr.0.g.cs"]);
    }

    [Fact]
    public void PartialCoverage_IsAnInfo_NamingWhatIsMissing() {
        var result = GeneratorHarness.RunWithFiles(Model, [("fr.validation-messages.json", FullFrench)]);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM0105");

        Assert.Contains("2 of 34", diagnostic.GetMessage());
        Assert.Contains("array_bounds.at_least", diagnostic.GetMessage());
    }

    [Fact]
    public void UserCodes_CompileSilently_TypoHeuristicUntouched() {
        var result = GeneratorHarness.RunWithFiles(Model, [("fr.validation-messages.json", """
            { "culture": "fr", "templates": { "date_order": "la date de fin doit suivre la date de début." } }
            """)]);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0101");
        Assert.Contains("date_order", result.Sources["LanguagePack.fr.0.g.cs"]);
    }

    [Fact]
    public void TheImplInventory_MatchesTheRuntimeVocabulary_KeysAndArities() {
        // The generator validates against its own mirror of the runtime's key inventory; this is
        // the pin that stops the two drifting. Arity ground truth is the runtime template itself -
        // the highest hole each one carries.
        var runtime = global::ValidationModules.ValidationMessageTemplates.TemplatesByKey;

        Assert.Equal(
            runtime.Keys.OrderBy(k => k, StringComparer.Ordinal),
            LanguagePackReader.ShapeInventory.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var pair in runtime) {
            var arity = 0;

            for (var hole = 0; hole <= 9; hole++) {
                if (pair.Value.Contains($"{{{hole}}}", StringComparison.Ordinal)) {
                    arity = hole + 1;
                }
            }

            Assert.Equal(arity, LanguagePackReader.ShapeInventory[pair.Key]);
        }
    }
}
