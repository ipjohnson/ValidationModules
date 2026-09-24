using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Pins that a nested type's generated names carry its containing types, so two nested types that
/// share a simple name in one namespace each get their own.
/// </summary>
/// <remarks>
/// Every generated name used to be the simple name plus a suffix. <c>Order.Item</c> and
/// <c>Invoice.Item</c> both became <c>ItemValidator</c>: the second <c>AddSource</c> threw on the
/// duplicate hint name, VM5002 failed the build, and the registration bound <c>ItemValidator</c> to
/// the other type's interface as CS0311. The same held for a rules class's companion, a fragment
/// container and the <c>IDynamicValidator</c> adapter.
/// </remarks>
public class NestedTypeNameTests
{
    private const string TwoNestedItems = """
        using ValidationModules.Constraints;

        namespace Shop;

        public class Order {
            public sealed class Item {
                [Required] public string? Sku { get; init; }
            }
        }

        public class Invoice {
            public sealed class Item {
                [Required] public string? Description { get; init; }
            }
        }
        """;

    [Fact]
    public void TwoNestedTypesWithOneName_EachGetsAValidatorNamedForItsContainer()
    {
        var result = GeneratorHarness.Run(TwoNestedItems);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "class Order_ItemValidator",
            result.Sources["Shop.Order_ItemValidator.g.cs"]
        );
        Assert.Contains(
            "class Invoice_ItemValidator",
            result.Sources["Shop.Invoice_ItemValidator.g.cs"]
        );
    }

    [Fact]
    public void TwoNestedTypesWithOneName_AreRegisteredEachAgainstItsOwnType()
    {
        var registration = GeneratorHarness.Run(TwoNestedItems).Sources[
            "GeneratedValidatorRegistration.g.cs"
        ];

        Assert.Contains(
            "global::ValidationModules.IValidatorFor<global::Shop.Order.Item>, global::Shop.Order_ItemValidator>",
            registration
        );
        Assert.Contains(
            "global::ValidationModules.IValidatorFor<global::Shop.Invoice.Item>, global::Shop.Invoice_ItemValidator>",
            registration
        );
    }

    [Fact]
    public void DeeperNesting_JoinsEveryContainingType()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            public class Outer {
                public class Middle {
                    public sealed class Inner {
                        [Required] public string? Name { get; init; }
                    }
                }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Outer_Middle_InnerValidator.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void ATopLevelType_KeepsItsName()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            public sealed class Customer {
                [Required] public string? Name { get; init; }
            }
            """
        );

        Assert.Contains("class CustomerValidator", result.Sources["CustomerValidator.g.cs"]);
    }

    /// <summary>
    /// A descent names the nested type's validator as its standalone fallback, and a compile-time
    /// dispatch holds one field per subtype validator, so both have to use the nested name.
    /// </summary>
    [Fact]
    public void DescentsAndDispatch_NameTheNestedValidator()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Shop;

            public class Order {
                public class Item {
                    [Required] public string? Sku { get; init; }
                }

                public sealed class Bundle : Item {
                    [Required] public string? Name { get; init; }
                }

                [ValidateNested] public Item? Single { get; init; }

                [ValidateNested(Polymorphism.CompileTime)] public Item? Dispatched { get; init; }
            }

            public class Invoice {
                public sealed class Item {
                    [Required] public string? Description { get; init; }
                }

                [ValidateNested] public Item? Line { get; init; }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);

        var order = result.Sources["Shop.OrderValidator.g.cs"];

        Assert.Contains("new global::Shop.Order_ItemValidator()", order);
        Assert.Contains("global::Shop.Order_BundleValidator", order);
        Assert.Contains(
            "new global::Shop.Invoice_ItemValidator()",
            result.Sources["Shop.InvoiceValidator.g.cs"]
        );
    }

    /// <summary>
    /// The <c>IDynamicValidator</c> adapter takes the nested name too. The message a runtime descent
    /// throws still names the owning type as it is written, <c>Invoice.Item</c>.
    /// </summary>
    [Fact]
    public void RuntimeDispatch_GivesEachNestedTypeItsOwnAdapter()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Shop;

            public class Order {
                public class Item {
                    [Required] public string? Sku { get; init; }
                }
            }

            public class Invoice {
                public class Item {
                    [Required] public string? Description { get; init; }

                    [ValidateNested(Polymorphism.Runtime)] public Order.Item? Line { get; init; }
                }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "class Order_ItemDynamicValidator",
            result.Sources["Shop.Order_ItemValidator.g.cs"]
        );
        Assert.Contains(
            "class Invoice_ItemDynamicValidator",
            result.Sources["Shop.Invoice_ItemValidator.g.cs"]
        );
        Assert.Contains("\"Invoice.Item\")", result.Sources["Shop.Invoice_ItemValidator.g.cs"]);
    }

    /// <summary>
    /// A rules class's region is emitted into a companion named after the rules class, so two nested
    /// rules classes that share a name collided in the same way.
    /// </summary>
    [Fact]
    public void NestedRulesClassesWithOneName_EachGetACompanion()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules;

            namespace Shop;

            public sealed class Order {
                public string? Number { get; init; }

                public sealed class Rules : IValidationRulesFor<Order> {
                    public static void Describe(ValidationRules<Order> rules, Order x) => rules.Require(x.Number);
                }
            }

            public sealed class Invoice {
                public string? Number { get; init; }

                public sealed class Rules : IValidationRulesFor<Invoice> {
                    public static void Describe(ValidationRules<Invoice> rules, Invoice x) => rules.Require(x.Number);
                }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Shop.Order_Rules_Rules.g.cs", result.Sources.Keys);
        Assert.Contains("Shop.Invoice_Rules_Rules.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void NestedFragmentContainersWithOneName_EachGetAContainer()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules;

            namespace Shop;

            public sealed class Order {
                public string? Number { get; init; }

                public static class Shared {
                    public static void Numbered(ValidationRules<Order> rules, Order x) => rules.Require(x.Number);
                }
            }

            public sealed class Invoice {
                public string? Number { get; init; }

                public static class Shared {
                    public static void Numbered(ValidationRules<Invoice> rules, Invoice x) => rules.Require(x.Number);
                }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) => Order.Shared.Numbered(rules, x);
            }

            public sealed class InvoiceRules : IValidationRulesFor<Invoice> {
                public static void Describe(ValidationRules<Invoice> rules, Invoice x) => Invoice.Shared.Numbered(rules, x);
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("Shop.Order_Shared_Fragments.g.cs", result.Sources.Keys);
        Assert.Contains("Shop.Invoice_Shared_Fragments.g.cs", result.Sources.Keys);
    }

    [Fact]
    public void ANestedFacet_IsReachedByItsNestedValidatorName()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Shop;

            public static class Contracts {
                [GenerateValidator]
                public interface IAudited {
                    [Required] string? CreatedBy { get; }
                }
            }

            public sealed class Order : Contracts.IAudited {
                public string? CreatedBy { get; init; }
                public string? Number { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.Require(x.Number);
                    rules.As<Contracts.IAudited>(x);
                }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "new global::Shop.Contracts_IAuditedValidator()",
            result.Sources["Shop.OrderRules_Rules.g.cs"]
        );
    }

    /// <summary>
    /// An underscore is legal in a type name, so a top-level <c>Order_Item</c> and a nested
    /// <c>Order.Item</c> still share a name. That is reported naming both types, rather than as
    /// VM5002 from a duplicate hint name.
    /// </summary>
    [Fact]
    public void ATopLevelTypeSpelledLikeANestedOne_IsVM1013NamingBoth()
    {
        var result = GeneratorHarness.Run(
            """
            using ValidationModules.Constraints;

            namespace Shop;

            public class Order {
                public sealed class Item {
                    [Required] public string? Sku { get; init; }
                }
            }

            public sealed class Order_Item {
                [Required] public string? Sku { get; init; }
            }
            """
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1013");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Shop.Order.Item", diagnostic.GetMessage());
        Assert.Contains("Shop.Order_Item", diagnostic.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM5002");
    }
}
