using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The <c>ValidationModules_*</c> properties that take one of a fixed set of values: matched
/// without regard to case, and VM5004 for a value that matches none of them.
/// </summary>
/// <remarks>
/// An unreadable value still has a default to fall back to, so the build succeeds either way.
/// Without VM5004, <c>snake_case</c> gives camelCase field names and nothing says why.
/// </remarks>
public class BuildPropertyValueTests
{
    private const string Source = """
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed record Profile {
            [Required]
            public string? DisplayName { get; init; }
        }

        public sealed record Voucher {
            [Pattern("^[A-Z]+$")]
            public string? Code { get; init; }
        }
        """;

    [Theory]
    [InlineData("snakecase")]
    [InlineData("SNAKECASE")]
    [InlineData(" SnakeCase ")]
    public void FieldNaming_IgnoresCase(string setting)
    {
        var result = GeneratorHarness.Run(Source, ("ValidationModules_FieldNaming", setting));

        Assert.Contains("\"display_name\"", result.Sources["Sample.ProfileValidator.g.cs"]);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5004");
    }

    /// <summary>
    /// The registration gets the setting in its documented spelling, so the namer it registers is
    /// the policy the literals were named with.
    /// </summary>
    [Fact]
    public void FieldNaming_InAnotherCase_RegistersTheMatchingNamer()
    {
        var result = GeneratorHarness.Run(Source, ("ValidationModules_FieldNaming", "snakecase"));

        Assert.Contains(
            "global::ValidationModules.Naming.SnakeCaseFieldNamer.Instance",
            result.Sources["GeneratedValidatorRegistration.g.cs"]
        );
    }

    [Theory]
    [InlineData("error")]
    [InlineData("ERROR")]
    public void PatternPolicy_IgnoresCase(string setting)
    {
        var result = GeneratorHarness.Run(Source, ("ValidationModules_PatternPolicy", setting));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1301");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Registration_IgnoresCase()
    {
        var none = GeneratorHarness.Run(Source, ("ValidationModules_Registration", "none"));
        var modules = GeneratorHarness.Run(
            Source,
            ("ValidationModules_Registration", "dependencymodules")
        );

        Assert.DoesNotContain("GeneratedValidatorRegistration.g.cs", none.Sources.Keys);
        Assert.Contains(
            "IDependencyModule",
            modules.Sources["GeneratedValidatorRegistration.g.cs"]
        );
    }

    [Theory]
    [InlineData(
        "ValidationModules_Registration",
        "Services",
        "Auto",
        "Auto, DependencyModules, ServiceCollection or None"
    )]
    [InlineData(
        "ValidationModules_FieldNaming",
        "snake_case",
        "CamelCase",
        "CamelCase, PascalCase, AsDeclared or SnakeCase"
    )]
    [InlineData("ValidationModules_PatternPolicy", "Strict", "Auto", "Auto, Error, Warn or Allow")]
    [InlineData("ValidationModules_DataAnnotations", "Skip", "Compile", "Compile or Ignore")]
    [InlineData("ValidationModules_FailFast", "Off", "Enabled", "Enabled, Disabled, true or false")]
    [InlineData(
        "ValidationModules_CaptureValues",
        "No",
        "Enabled",
        "Enabled, Disabled, true or false"
    )]
    public void AnUnrecognisedValue_IsVM5004(
        string property,
        string setting,
        string fallback,
        string accepted
    )
    {
        var result = GeneratorHarness.Run(Source, (property, setting));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM5004");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(Location.None, diagnostic.Location);
        Assert.Equal(
            $"{property} is '{setting}', which is not a value it accepts, so the generator uses "
                + $"the default, {fallback}. Set it to {accepted}",
            diagnostic.GetMessage()
        );
    }

    [Fact]
    public void AnUnrecognisedFieldNaming_MeansCamelCase()
    {
        var result = GeneratorHarness.Run(Source, ("ValidationModules_FieldNaming", "snake_case"));

        Assert.Contains("\"displayName\"", result.Sources["Sample.ProfileValidator.g.cs"]);
        Assert.Contains(
            "global::ValidationModules.Naming.CamelCaseFieldNamer.Instance",
            result.Sources["GeneratedValidatorRegistration.g.cs"]
        );
    }

    [Fact]
    public void AnUnrecognisedPatternPolicy_MeansAuto()
    {
        var result = GeneratorHarness.Run(
            Source,
            ("PublishAot", "true"),
            ("ValidationModules_PatternPolicy", "Strict")
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1301");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// Reported from the options alone, so a project with nothing to validate still hears about a
    /// setting that would change what it generates once it has.
    /// </summary>
    [Fact]
    public void AnUnrecognisedValue_IsReportedWithNothingToValidate()
    {
        var result = GeneratorHarness.Run(
            """
            namespace Sample;

            public sealed record Profile {
                public string? DisplayName { get; init; }
            }
            """,
            ("ValidationModules_Registration", "Services")
        );

        Assert.DoesNotContain("GeneratedValidatorRegistration.g.cs", result.Sources.Keys);
        Assert.Single(result.Diagnostics, d => d.Id == "VM5004");
    }

    [Fact]
    public void EachUnrecognisedProperty_IsReportedOnce()
    {
        var result = GeneratorHarness.Run(
            Source,
            ("ValidationModules_FieldNaming", "snake_case"),
            ("ValidationModules_FailFast", "Off")
        );

        var messages = result
            .Diagnostics.Where(d => d.Id == "VM5004")
            .Select(d => d.GetMessage())
            .ToList();

        Assert.Equal(2, messages.Count);
        Assert.Contains(
            messages,
            m =>
                m.StartsWith(
                    "ValidationModules_FieldNaming is 'snake_case'",
                    StringComparison.Ordinal
                )
        );
        Assert.Contains(
            messages,
            m => m.StartsWith("ValidationModules_FailFast is 'Off'", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// MSBuild hands an unset <c>CompilerVisibleProperty</c> over as an empty string, so empty has
    /// to read as unset.
    /// </summary>
    [Theory]
    [InlineData("ValidationModules_Registration", "AUTO")]
    [InlineData("ValidationModules_Registration", "servicecollection")]
    [InlineData("ValidationModules_Registration", "")]
    [InlineData("ValidationModules_FieldNaming", "camelcase")]
    [InlineData("ValidationModules_FieldNaming", "asdeclared")]
    [InlineData("ValidationModules_FieldNaming", "   ")]
    [InlineData("ValidationModules_PatternPolicy", "auto")]
    [InlineData("ValidationModules_PatternPolicy", "Allow")]
    [InlineData("ValidationModules_DataAnnotations", "compile")]
    [InlineData("ValidationModules_DataAnnotations", "IGNORE")]
    [InlineData("ValidationModules_FailFast", "Enabled")]
    [InlineData("ValidationModules_FailFast", "TRUE")]
    [InlineData("ValidationModules_CaptureValues", "disabled")]
    [InlineData("ValidationModules_CaptureValues", "False")]
    public void AnAcceptedOrEmptyValue_IsNotReported(string property, string setting)
    {
        var result = GeneratorHarness.Run(Source, (property, setting));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5004");
    }
}
