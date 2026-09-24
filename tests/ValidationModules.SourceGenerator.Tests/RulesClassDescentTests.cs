using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>rules.Nested</c> and <c>rules.Each</c>: which descents a rules class can declare, and what
/// happens to one that has nothing to call.
/// </summary>
/// <remarks>
/// A rules-class descent is written into the region as a walk over an injected validator array, and
/// that array names the target's validator. So a descent the generator cannot call has to be
/// refused or dropped before the walk is written. Each case below would otherwise reach the
/// emitter, where it either throws (VM5002) or produces generated code that does not compile.
/// </remarks>
public class RulesClassDescentTests
{
    private static string Source(string body) =>
        $$"""
            using System.Collections.Generic;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            {{body}}
            """;

    private const string LineWithRules = """
        public sealed class Line {
            public string? Sku { get; init; }
        }

        public sealed class LineRules : IValidationRulesFor<Line> {
            public static void Describe(ValidationRules<Line> rules, Line x) => rules.Require(x.Sku);
        }
        """;

    private static string OrderRules(string statement) =>
        Source(
            LineWithRules
                + $$"""

                public sealed class Order {
                    public IReadOnlyList<Line>? Lines { get; init; }
                    public IReadOnlyDictionary<string, Line>? ByCode { get; init; }
                    public Line? First { get; init; }
                }

                public sealed class OrderRules : IValidationRulesFor<Order> {
                    public static void Describe(ValidationRules<Order> rules, Order x) {
                        {{statement}}
                    }
                }
                """
        );

    private static int Count(string text, string fragment) =>
        Regex.Matches(text, Regex.Escape(fragment)).Count;

    private static string AllSources(GeneratorHarness.Result result) =>
        string.Concat(result.Sources.Values);

    [Fact]
    public void NestedOverACollection_IsVM3001NamingEach()
    {
        var result = GeneratorHarness.Run(OrderRules("rules.Nested(x.Lines);"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3001");

        Assert.Contains("rules.Each(x.Lines)", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void NestedOverADictionary_IsVM3001NamingValidateNested()
    {
        // Each takes a list, so the advice for a dictionary is the attribute, which walks values.
        var result = GeneratorHarness.Run(OrderRules("rules.Nested(x.ByCode);"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3001");

        Assert.Contains("[ValidateNested]", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("rules.Each(x.Lines).Nested();", "a descent chained after element rules")]
    [InlineData("rules.Each(x.Lines).Each();", "a second Each chained after element rules")]
    [InlineData("rules.Nested(x.First).Nested();", "a descent chained after another descent")]
    public void ASecondDescentInOneChain_IsVM3001(string statement, string refusal)
    {
        // An Each over objects leaves the collection as the chain's anchor, so a second descent
        // chained after it would walk the elements twice, or walk the list as one object.
        var result = GeneratorHarness.Run(OrderRules(statement));

        Assert.Contains(
            refusal,
            Assert.Single(result.Diagnostics, d => d.Id == "VM3001").GetMessage()
        );
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("rules.Nested(x.First);")]
    [InlineData("rules.Each(x.Lines);")]
    [InlineData("rules.Count(x.Lines, 1, 50).Each();")]
    [InlineData("rules.Each(x.Lines).Count(1, 50);")]
    public void OneDescentPerChain_StillDescends(string statement)
    {
        var result = GeneratorHarness.Run(OrderRules(statement));

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("LineValidator", AllSources(result));
    }

    [Fact]
    public void EachOverElementsThatCanNeverHaveAValidator_IsVM1502AndBuildsClean()
    {
        // The element type is List<int>, a constructed generic no validator can exist for. Its
        // name would reach EmitterOutput.TypeRef, which throws on a generic name.
        var result = GeneratorHarness.Run(
            Source(
                """
                public sealed class Grid {
                    public IReadOnlyList<List<int>>? Rows { get; init; }
                    public string? Title { get; init; }
                }

                public sealed class GridRules : IValidationRulesFor<Grid> {
                    public static void Describe(ValidationRules<Grid> rules, Grid x) {
                        rules.Require(x.Title);
                        rules.Each(x.Rows);
                    }
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1502");

        Assert.Contains("rules.Each on 'Rows'", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id is "VM5002" or "VM1503");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Sample.GridValidator.g.cs", result.Sources.Keys);
    }

    [Theory]
    [InlineData("rules.Nested(x.Home);", "rules.Nested")]
    [InlineData("rules.Each(x.Homes);", "rules.Each")]
    public void ADescentIntoATypeWithNoRules_IsVM1501AndBuildsClean(
        string statement,
        string construct
    )
    {
        // The warning says the descent is dropped. A region that kept it would name a
        // NoRulesValidator that is never generated, which fails with CS0400.
        var result = GeneratorHarness.Run(
            Source(
                $$"""
                public sealed class NoRules {
                    public string? S { get; init; }
                }

                public sealed class Q {
                    public NoRules? Home { get; init; }
                    public IReadOnlyList<NoRules>? Homes { get; init; }
                    public string? T { get; init; }
                }

                public sealed class QRules : IValidationRulesFor<Q> {
                    public static void Describe(ValidationRules<Q> rules, Q x) {
                        rules.Require(x.T);
                        {{statement}}
                    }
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1501");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains($"{construct} on '", diagnostic.GetMessage());
        Assert.DoesNotContain("[ValidateNested]", diagnostic.GetMessage());
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain("NoRulesValidator", AllSources(result));
        Assert.Contains("Sample.QValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void ADescentIntoATypeFromAnotherAssemblyWithNoValidator_IsVM1505()
    {
        var result = GeneratorHarness.Run(
            Source(
                """
                public sealed class Document {
                    public System.Text.StringBuilder? Body { get; init; }
                }

                public sealed class DocumentRules : IValidationRulesFor<Document> {
                    public static void Describe(ValidationRules<Document> rules, Document x) =>
                        rules.Nested(x.Body);
                }
                """
            )
        );

        Assert.Contains(
            "rules.Nested on 'Body'",
            Assert.Single(result.Diagnostics, d => d.Id == "VM1505").GetMessage()
        );
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ADescentIntoTheSecondTargetOfAMultiTargetRulesClass_Descends()
    {
        // BothRules declares Right's rules through its second contract, so Right has a validator.
        var result = GeneratorHarness.Run(
            Source(
                """
                public sealed class Left {
                    public string? A { get; init; }
                }

                public sealed class Right {
                    public string? B { get; init; }
                }

                public sealed class BothRules : IValidationRulesFor<Left>, IValidationRulesFor<Right> {
                    public static void Describe(ValidationRules<Left> rules, Left x) => rules.Require(x.A);

                    public static void Describe(ValidationRules<Right> rules, Right x) => rules.Require(x.B);
                }

                public sealed class Holder {
                    public Right? R { get; init; }
                }

                public sealed class HolderRules : IValidationRulesFor<Holder> {
                    public static void Describe(ValidationRules<Holder> rules, Holder x) => rules.Nested(x.R);
                }
                """
            )
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1501");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("RightValidator", result.Sources["Sample.HolderValidator.g.cs"]);
    }

    [Theory]
    [InlineData(
        "[ValidateNested] public Address? Home { get; init; }",
        "rules.Nested(x.Home);",
        "Push(\"home\")"
    )]
    [InlineData(
        "[ValidateNested] public IReadOnlyList<Address>? Homes { get; init; }",
        "rules.Each(x.Homes);",
        "PushIndex(\"homes\""
    )]
    public void ADescentThatRepeatsValidateNested_IsVM3106AndDescendsOnce(
        string property,
        string statement,
        string push
    )
    {
        // Attributes and a rules class merge onto one validator, so keeping both descents would
        // report every nested error twice.
        var result = GeneratorHarness.Run(
            Source(
                $$"""
                public sealed class Address {
                    [Required] public string? Postcode { get; init; }
                }

                public sealed class Person {
                    {{property}}
                }

                public sealed class PersonRules : IValidationRulesFor<Person> {
                    public static void Describe(ValidationRules<Person> rules, Person x) {
                        {{statement}}
                    }
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3106");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Empty(result.CompilationErrors);
        Assert.Equal(1, Count(AllSources(result), push));
    }

    /// <summary>
    /// A rules-class descent runs the validators for the declared type, and takes no
    /// <c>Polymorphism</c>, so VM1503's advice does not apply to it. It is reported at the call,
    /// with advice the author can follow.
    /// </summary>
    [Theory]
    [InlineData("rules.Nested(x.ShipTo)", "rules.Nested", "ShipTo")]
    [InlineData("rules.For(x.ShipTo).Nested()", "rules.Nested", "ShipTo")]
    [InlineData("rules.Each(x.Stops)", "rules.Each", "Stops")]
    [InlineData("rules.Count(x.Stops, 1, 5).Each()", "rules.Each", "Stops")]
    public void ADescentIntoATypeThatIsNotSealed_IsVM3111AtTheCall(
        string call,
        string construct,
        string property
    )
    {
        var result = GeneratorHarness.Run(
            Source(
                $$"""
                public class Address {
                    [Required] public string? Street { get; init; }
                }

                public sealed record Order {
                    public Address? ShipTo { get; init; }
                    public IReadOnlyList<Address>? Stops { get; init; }
                }

                public sealed class OrderRules : IValidationRulesFor<Order> {
                    public static void Describe(ValidationRules<Order> rules, Order x) {
                        {{call}};
                    }
                }
                """
            )
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM3111");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            $"'Address' is not sealed, so a value of a more derived type may reach '{property}'. "
                + $"{construct} checks it against the rules for 'Address' only. Seal 'Address', "
                + $"or replace {construct} with [ValidateNested(Polymorphism.CompileTime)] on "
                + $"'{property}' to run the rules for its actual type. To keep checking 'Address' "
                + "only, suppress this warning at the call",
            diagnostic.GetMessage()
        );
        Assert.Equal(
            call,
            diagnostic
                .Location.SourceTree!.GetText(TestContext.Current.CancellationToken)
                .ToString(diagnostic.Location.SourceSpan)
        );
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM1503");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("AddressValidator", AllSources(result));
    }

    [Fact]
    public void ADescentIntoAnInterface_IsVM3111WithoutTheAdviceToSealIt()
    {
        var result = GeneratorHarness.Run(
            Source(
                """
                public interface IStop {
                    [Required] string? Code { get; }
                }

                public sealed class Route {
                    public IReadOnlyList<IStop>? Stops { get; init; }
                }

                public sealed class RouteRules : IValidationRulesFor<Route> {
                    public static void Describe(ValidationRules<Route> rules, Route x) =>
                        rules.Each(x.Stops);
                }
                """
            )
        );

        // An interface cannot be sealed, so that advice is left out.
        Assert.Equal(
            "'IStop' is not sealed, so a value of a more derived type may reach 'Stops'. rules.Each "
                + "checks it against the rules for 'IStop' only. Replace rules.Each with "
                + "[ValidateNested(Polymorphism.CompileTime)] on 'Stops' to run the rules for its "
                + "actual type. To keep checking 'IStop' only, suppress this warning at the call",
            Assert.Single(result.Diagnostics, d => d.Id == "VM3111").GetMessage()
        );
        Assert.Empty(result.CompilationErrors);
    }
}
