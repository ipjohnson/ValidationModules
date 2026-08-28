using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>field:</c> renames one error. It must not rename the property.
/// </summary>
/// <remarks>
/// A rule is emitted inside its anchored property's chain so that both engines agree on ordering
/// (§4.2), and the anchor is picked implicitly from the first property the predicate reads. So the
/// property a rename lands on is not one the author chose - reordering the operands of an
/// <c>a || b</c> moves it. <c>ConstraintModel.Field</c> exists to keep the rename on the constraint
/// for exactly that reason; the front end was also assigning it to the property.
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
            public void Describe(ValidationRules<Order> rules) {
                rules.Ensure(x => x.Ship != null, field: "shipping_address");
            }
        }
        """;

    [Fact]
    public void ExplicitField_DoesNotRenameTheNestedDescent() {
        // The reported corruption: an unrelated nested error moved from `ship.zone.city` to
        // `shipping_address.zone.city`, because the descent pushes the property's field name and
        // the rename had been promoted onto the property.
        var result = GeneratorHarness.Run(Source);

        var emitted = result.Sources["Sample.OrderValidator.g.cs"];

        Assert.Contains("ctx.Push(\"ship\")", emitted);
        Assert.DoesNotContain("ctx.Push(\"shipping_address\")", emitted);
    }

    [Fact]
    public void ExplicitField_StillRenamesItsOwnError() {
        // The complement: the rename has to survive on the constraint it was written on.
        var result = GeneratorHarness.Run(Source);

        Assert.Contains("\"shipping_address\"", result.Sources["Sample.OrderValidator.g.cs"]);
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
                public void Describe(ValidationRules<Order> rules) {
                    rules.Ensure(x => x.Reference != "void", field: "reference_state");
                }
            }
            """;

        var result = GeneratorHarness.Run(source);

        var emitted = result.Sources["Sample.OrderValidator.g.cs"];

        // The [Required] on the same property keeps its own name.
        Assert.Contains("ReportRequired(\"reference\"", emitted);
        Assert.Contains("\"reference_state\"", emitted);
    }

    [Fact]
    public void ExplicitField_IsNotPutThroughTheFieldNamer() {
        // A rename is the author's literal string. It is the one field name the namer must leave
        // alone - which is also why it must not become the property's name, where the namer's
        // output is what every other site expects.
        var result = GeneratorHarness.Run(Source, ("ValidationModules_FieldNaming", "SnakeCase"));

        Assert.Contains("\"shipping_address\"", result.Sources["Sample.OrderValidator.g.cs"]);
    }
}
