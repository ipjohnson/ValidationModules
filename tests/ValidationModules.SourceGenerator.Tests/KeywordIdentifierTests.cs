using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Types and properties whose names collide with C# keywords.
/// </summary>
/// <remarks>
/// <para>
/// A generator writes C# text, so every identifier it interpolates has to survive being parsed
/// again. A property declared <c>@object</c> is an ordinary property with the CLR name
/// <c>object</c>, and emitting <c>value.object</c> for it is a syntax error inside generated code -
/// which is the worst place for a consumer to meet one, because the file they are told to look at
/// is not one they wrote.
/// </para>
/// <para>
/// <b>The camel-cased case does not need a verbatim identifier in the source to reach.</b> The
/// emitted constructor names its parameters after the properties they serve, lower-cased, so an
/// ordinary <c>Object</c>, <c>Event</c> or <c>Default</c> property - no <c>@</c> anywhere in the
/// consumer's source - produces a parameter named <c>object</c>, <c>event</c> or <c>default</c>.
/// That one is reachable by writing perfectly normal C#, so it is the likelier of the two.
/// </para>
/// </remarks>
public class KeywordIdentifierTests {

    [Fact]
    public void Generate_PropertyNamedWithAKeyword_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Payload {
                [Required, StringLength(min: 1, max: 10)]
                public string? @object { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("@object")]
    [InlineData("@class")]
    [InlineData("@int")]
    [InlineData("@string")]
    [InlineData("@event")]
    [InlineData("@return")]
    [InlineData("@default")]
    [InlineData("@null")]
    [InlineData("@base")]
    [InlineData("@this")]
    [InlineData("@new")]
    [InlineData("@lock")]
    [InlineData("@params")]
    [InlineData("@operator")]
    [InlineData("@namespace")]
    public void Generate_EveryKeywordAsAScalarProperty_EmitsCompilableCode(string property) {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Api;

            public record Payload {
                [Required]
                public string? {{property}} { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The escape belongs to the code, not to the wire. <c>@</c> is C# syntax for "this identifier
    /// is not a keyword"; it is not part of the name, so a client parsing the response must see
    /// <c>object</c>. Getting this backwards is the obvious way to fix the compile error and would
    /// silently change every payload the property appears in.
    /// </summary>
    [Fact]
    public void Generate_KeywordProperty_KeepsTheAtSignOutOfTheFieldName() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Payload {
                [Required]
                public string? @object { get; init; }
            }
            """);

        var source = result.Sources["Api.PayloadValidator.g.cs"];

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("\"object\"", source);
        Assert.DoesNotContain("\"@object\"", source);
    }

    /// <summary>
    /// Reachable without a verbatim identifier anywhere in the consumer's source: the emitted
    /// constructor parameter is the property name camel-cased, and <c>Object</c> camel-cases onto a
    /// keyword. Nested, because that is what makes the validator take constructor parameters at all.
    /// </summary>
    [Theory]
    [InlineData("Object")]
    [InlineData("Event")]
    [InlineData("Default")]
    [InlineData("Base")]
    [InlineData("Operator")]
    [InlineData("Namespace")]
    [InlineData("Lock")]
    [InlineData("Params")]
    public void Generate_NestedPropertyThatCamelCasesOntoAKeyword_EmitsCompilableCode(string property) {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Api;

            public record Address {
                [Required] public string? Postcode { get; init; }
            }

            public record Payload {
                [ValidateNested]
                public Address? {{property}} { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Generate_NestedPropertyNamedWithAKeyword_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Address {
                [Required] public string? Postcode { get; init; }
            }

            public record Payload {
                [ValidateNested]
                public Address? @object { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Generate_CollectionPropertyNamedWithAKeyword_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Api;

            public record Line {
                [Required] public string? Sku { get; init; }
            }

            public record Payload {
                [ItemCount(min: 1, max: 10), ValidateNested]
                public IReadOnlyList<Line> @event { get; init; } = [];
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Generate_DictionaryPropertyNamedWithAKeyword_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Api;

            public record Line {
                [Required] public string? Sku { get; init; }
            }

            public record Payload {
                [ValidateNested]
                public Dictionary<string, Line> @class { get; init; } = new();
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Generate_ValidatedTypeNamedWithAKeyword_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record @object {
                [Required] public string? Name { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Generate_NamespaceSegmentNamedWithAKeyword_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace @object.Models;

            public record Payload {
                [Required] public string? Name { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// A nested type in a keyword-named namespace, so the emitted validator has to qualify a type
    /// across the escape rather than only declare one inside it.
    /// </summary>
    [Fact]
    public void Generate_NestedTypeInAKeywordNamespace_EmitsCompilableCode() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace @object.Models;

            public record Address {
                [Required] public string? Postcode { get; init; }
            }

            public record Payload {
                [ValidateNested] public Address? Home { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// Contextual keywords are legal identifiers unescaped, so they must keep working - a fix that
    /// escapes every identifier that <i>looks</i> keyword-ish would still compile, but it would
    /// churn the emitted text for names that never needed it.
    /// </summary>
    [Theory]
    [InlineData("value")]
    [InlineData("record")]
    [InlineData("var")]
    [InlineData("async")]
    [InlineData("nameof")]
    public void Generate_ContextualKeywordAsAProperty_EmitsCompilableCode(string property) {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Api;

            public record Payload {
                [Required]
                public string? {{property}} { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The registration extension names itself after the assembly, so a keyword-shaped assembly name
    /// reaches the same problem from the other side.
    /// </summary>
    [Fact]
    public void Generate_KeywordShapedAssemblyName_EmitsCompilableRegistration() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Api;

            public record Payload {
                [Required] public string? Name { get; init; }
            }
            """, assemblyName: "object");

        Assert.Empty(result.CompilationErrors);
    }
}
