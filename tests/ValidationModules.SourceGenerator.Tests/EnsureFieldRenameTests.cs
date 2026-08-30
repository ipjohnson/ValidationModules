using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>field:</c> renames one error. It must not rename the property.
/// </summary>
/// <remarks>
/// An Ensure is anchored to the first property its condition reads - a property the author did not
/// choose, since reordering the operands of an <c>a || b</c> moves it. The rename therefore rides
/// on the one rule it was written on: the descent a property declares elsewhere keeps pushing the
/// property's own wire name, and so does every other rule anchored there.
/// </remarks>
public class EnsureFieldRenameTests {

    private const string Source = """
        using ValidationModules;
        using ValidationModules.Constraints;

        namespace Sample;

        public sealed record Zone {
            [Required] public string? City { get; init; }
        }

        public sealed record Ship {
            [Required] public string? Carrier { get; init; }
            [ValidateNested] public Zone? Zone { get; init; }
        }

        public sealed record Order {
            [ValidateNested] public Ship? Ship { get; init; }
        }

        public sealed class OrderRules : IValidationRulesFor<Order> {
            public static void Describe(ValidationRules<Order> rules, Order x) {
                rules.Ensure(x.Ship != null, field: "shipping_address");
            }
        }
        """;

    [Fact]
    public void ExplicitField_DoesNotRenameTheNestedDescent() {
        // The reported corruption this pins: an unrelated nested error moved from `ship.zone.city`
        // to `shipping_address.zone.city`, because the descent pushes the property's field name and
        // the rename had been promoted onto the property.
        var result = GeneratorHarness.Run(Source);

        var emitted = result.Sources["Sample.OrderValidator.g.cs"];

        Assert.Contains("ctx.Push(\"ship\")", emitted);
        Assert.DoesNotContain("ctx.Push(\"shipping_address\")", emitted);
    }

    [Fact]
    public void ExplicitField_StillRenamesItsOwnError() {
        // The complement: the rename has to survive on the rule it was written on, which now lives
        // in the region companion.
        var result = GeneratorHarness.Run(Source);

        Assert.Contains("\"shipping_address\"", result.Sources["Sample.OrderRules_Rules.g.cs"]);
    }

    [Fact]
    public void ExplicitField_DoesNotRenameOtherConstraintsOnTheAnchor() {
        var source = """
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Order {
                [Required] public string? Reference { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.Ensure(x.Reference != "void", field: "reference_state");
                }
            }
            """;

        var result = GeneratorHarness.Run(source);

        // The [Required] on the same property keeps its own name in the attribute region.
        Assert.Contains("ReportRequired(ctx, \"reference\"", result.Sources["Sample.OrderValidator.g.cs"]);
        Assert.Contains("\"reference_state\"", result.Sources["Sample.OrderRules_Rules.g.cs"]);
    }

    [Fact]
    public void ExplicitField_IsNotPutThroughTheFieldNamer() {
        // A rename is the author's literal string. It is the one field name the namer must leave
        // alone - which is also why it must not become the property's name, where the namer's
        // output is what every other site expects.
        var result = GeneratorHarness.Run(Source, ("ValidationModules_FieldNaming", "SnakeCase"));

        Assert.Contains("\"shipping_address\"", result.Sources["Sample.OrderRules_Rules.g.cs"]);
    }

    [Fact]
    public void FieldFromNameof_TakesTheWireName() {
        // nameof through the subject is the one field: spelling that names a member rather than
        // choosing a string, and transcribed code already rewrites the same spelling to the wire
        // path. Before this, one property could reach a client under two keys: 'AccountNumber'
        // from `field: nameof(x.AccountNumber)` and 'accountNumber' from everything else.
        const string source = """
            using ValidationModules;

            namespace Sample;

            public sealed record Order {
                public string? AccountNumber { get; init; }
                public string? Reference { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.Ensure(x.Reference != null, field: nameof(x.AccountNumber), code: "window");
                }
            }
            """;

        var result = GeneratorHarness.Run(source);

        var region = result.Sources["Sample.OrderRules_Rules.g.cs"];

        Assert.Contains("\"accountNumber\"", region);
        Assert.DoesNotContain("\"AccountNumber\"", region);
    }
}
