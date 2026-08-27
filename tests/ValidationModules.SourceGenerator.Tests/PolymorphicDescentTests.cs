using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>[ValidateNested]</c> dispatching over the subtypes of its declared type.
/// </summary>
/// <remarks>
/// <para>
/// The defect behind this: a descent dispatches on the <i>declared</i> type, so a
/// <c>Checkout { Payment = new Card { Pan = "123" } }</c> validated clean - the subtype's rules were
/// never reached, and no diagnostic said so either.
/// </para>
/// <para>
/// Dispatch is always asked for by name. Doing it automatically over whatever subtypes the
/// generator happened to see would make coverage depend on assembly layout, shrinking silently the
/// day a type moved to a package - and unearned confidence is worse than no feature.
/// </para>
/// </remarks>
public class PolymorphicDescentTests {

    private const string Hierarchy = """
        public abstract record Payment {
            [Required]
            public string? Currency { get; init; }
        }

        public record Card : Payment {
            [StringLength(16, 16)]
            public string? Pan { get; init; }
        }

        public sealed record Premium : Card {
            [Required]
            public string? Concierge { get; init; }
        }

        public sealed record Bank : Payment {
            [Required]
            public string? Iban { get; init; }
        }
        """;

    private static GeneratedResult Run(string nested) {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Sample;

            {{Hierarchy}}

            public sealed record Checkout {
                {{nested}}
                public Payment? Payment { get; init; }
            }
            """);

        return new GeneratedResult(result);
    }

    private sealed record GeneratedResult(GeneratorHarness.Result Result) {
        public string Checkout {
            get {
                // The strongest assertion in the file, and it is this one: emitting the switch arms
                // in the wrong order is CS8120 - "the switch case has already been handled by a
                // previous case" - inside a generated file, which is exactly the class of error
                // the no-emit-after-diagnostic work exists to prevent.
                Assert.Empty(Result.CompilationErrors);

                return Result.Sources.Single(source => source.Key.Contains("CheckoutValidator")).Value;
            }
        }
    }

    // -- modes ---------------------------------------------------------------------------------

    [Fact]
    public void CompileTime_EmitsATypeSwitchOverTheSubtypes() {
        var body = Run("[ValidateNested(Polymorphism.CompileTime)]").Checkout;

        Assert.Contains("switch (nestedPayment) {", body);
        Assert.Contains("case global::Sample.Premium __typed:", body);
        Assert.Contains("case global::Sample.Card __typed:", body);
        Assert.Contains("case global::Sample.Bank __typed:", body);
    }

    /// <summary>
    /// A type pattern matches derived types, so <c>case Card</c> ahead of <c>case Premium : Card</c>
    /// makes the second arm unreachable. Sorting by inheritance depth descending is what prevents
    /// it, and getting it wrong is a compile error rather than a wrong answer.
    /// </summary>
    [Fact]
    public void SwitchArms_AreOrderedMostDerivedFirst() {
        var body = Run("[ValidateNested(Polymorphism.CompileTime)]").Checkout;

        Assert.True(
            body.IndexOf("case global::Sample.Premium", StringComparison.Ordinal)
            < body.IndexOf("case global::Sample.Card", StringComparison.Ordinal),
            "Premium derives from Card, so its arm has to come first or the Card arm swallows it");
    }

    /// <summary>
    /// The declared type's validators belong in <c>default</c> rather than after the switch. Each
    /// subtype validator already checks everything it inherits, so running both would report the
    /// base's failures twice - which is why inherited constraint collection is a prerequisite for
    /// dispatch rather than a companion to it.
    /// </summary>
    [Fact]
    public void DeclaredTypeValidators_RunOnlyInTheDefaultArm() {
        var body = Run("[ValidateNested(Polymorphism.CompileTime)]").Checkout;

        var switchStart = body.IndexOf("switch (nestedPayment)", StringComparison.Ordinal);
        var defaultArm = body.IndexOf("default: {", switchStart, StringComparison.Ordinal);
        var loop = body.IndexOf("validatorsPayment[vi].Validate", StringComparison.Ordinal);

        Assert.True(defaultArm > switchStart, "the switch should have a default arm");
        Assert.True(loop > defaultArm, "the declared-type descent belongs inside default");
        Assert.Equal(1, Occurrences(body, "validatorsPayment[vi].Validate"));
    }

    [Fact]
    public void DeclaredOnly_EmitsNoSwitchAtAll() {
        var body = Run("[ValidateNested(Polymorphism.DeclaredOnly)]").Checkout;

        Assert.DoesNotContain("switch (", body);
        Assert.Contains("validatorsPayment[vi].Validate", body);
    }

    [Fact]
    public void NoModeAtAll_BehavesAsDeclaredOnly() {
        var body = Run("[ValidateNested]").Checkout;

        Assert.DoesNotContain("switch (", body);
    }

    /// <summary>
    /// Lazily created, because eager construction would allocate on every branch never taken and a
    /// validator costs 24 bytes to build.
    /// </summary>
    [Fact]
    public void SubtypeValidators_AreHeldInLazyFields() {
        var body = Run("[ValidateNested(Polymorphism.CompileTime)]").Checkout;

        Assert.Contains("private global::Sample.PremiumValidator? _dispatch", body);
        Assert.Contains("??= new()).Validate(ref ctxPayment, __typed)", body);
    }

    [Fact]
    public void IsValid_MirrorsTheSwitch() {
        var body = Run("[ValidateNested(Polymorphism.CompileTime)]").Checkout;
        var isValid = body[body.IndexOf("public bool IsValid", StringComparison.Ordinal)..];

        Assert.Contains("switch (nestedPayment) {", isValid);
        Assert.Contains("IsValid(__typed)) return false;", isValid);
    }

    // -- VM0031 --------------------------------------------------------------------------------

    /// <summary>
    /// Keyed on whether the target is sealed, never on which subtypes are visible - a diagnostic
    /// that came and went across an assembly boundary would reintroduce the layout-dependence the
    /// whole design exists to avoid.
    /// </summary>
    [Fact]
    public void UnsealedTargetWithNoMode_IsVM0031() {
        Assert.Contains(Run("[ValidateNested]").Result.Diagnostics, d => d.Id == "VM0031");
    }

    [Fact]
    public void SealedTarget_NeedsNoModeAndDoesNotWarn() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Address {
                [Required]
                public string? Street { get; init; }
            }

            public sealed record Person {
                [ValidateNested]
                public Address? Home { get; init; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM0031");
    }

    /// <summary>
    /// An author who wrote <c>DeclaredOnly</c> has made the decision and should not be asked again.
    /// </summary>
    [Theory]
    [InlineData("[ValidateNested(Polymorphism.DeclaredOnly)]")]
    [InlineData("[ValidateNested(Polymorphism.CompileTime)]")]
    public void AnExplicitMode_SilencesVM0031(string nested) {
        Assert.DoesNotContain(Run(nested).Result.Diagnostics, d => d.Id == "VM0031");
    }

    // -- the two features together --------------------------------------------------------------

    /// <summary>
    /// A guarded polymorphic descent: the condition decides whether to descend at all, the switch
    /// decides which validator runs. The two are emitted as separate concerns around one
    /// <c>ctx.Push</c>, which is what lets them compose without either knowing about the other.
    /// </summary>
    [Fact]
    public void ADescentCanBeBothConditionalAndPolymorphic() {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Sample;

            {{Hierarchy}}

            public sealed record Checkout {
                public bool IsPaid { get; init; }

                [ValidateNested(Polymorphism.CompileTime, When = nameof(IsPaid))]
                public Payment? Payment { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);

        var body = result.Sources.Single(s => s.Key.Contains("CheckoutValidator")).Value;

        Assert.Contains("var c0 = value.IsPaid;", body);
        Assert.Contains("if (c0 && (value.Payment is { } nestedPayment)) {", body);
        Assert.Contains("switch (nestedPayment) {", body);
    }

    /// <summary>
    /// Dispatch reaches collection elements too - a list of payments is as ordinary as one payment.
    /// </summary>
    [Fact]
    public void CollectionElements_DispatchThroughTheSameSwitch() {
        var result = GeneratorHarness.Run($$"""
            using System.Collections.Generic;
            using ValidationModules.Constraints;

            namespace Sample;

            {{Hierarchy}}

            public sealed record Checkout {
                [ValidateNested(Polymorphism.CompileTime)]
                public List<Payment> Payments { get; init; } = new();
            }
            """);

        Assert.Empty(result.CompilationErrors);

        var body = result.Sources.Single(s => s.Key.Contains("CheckoutValidator")).Value;

        Assert.Contains("switch (element) {", body);
        Assert.Contains("case global::Sample.Premium __typed:", body);
    }

    /// <summary>
    /// A subtype declared with no constraints of its own still has a validator, because it inherits
    /// its base's - so there is a class for the arm to name.
    /// </summary>
    [Fact]
    public void SubtypeAddingNothingOfItsOwn_StillGetsAnArm() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public abstract record Payment {
                [Required]
                public string? Currency { get; init; }
            }

            public sealed record Cash : Payment;

            public sealed record Checkout {
                [ValidateNested(Polymorphism.CompileTime)]
                public Payment? Payment { get; init; }
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains(
            "case global::Sample.Cash __typed:",
            result.Sources.Single(s => s.Key.Contains("CheckoutValidator")).Value);
    }

    // -- Polymorphism.Runtime --------------------------------------------------------------------

    [Fact]
    public void Runtime_ResolvesThroughTheContextRatherThanEmittingASwitch() {
        var body = Run("[ValidateNested(Polymorphism.Runtime)]").Checkout;

        Assert.Contains(
            "if (global::ValidationModules.DynamicValidation.Validate(ref ctxPayment, nestedPayment, " +
            "\"payment\", \"Checkout\").ShouldStop) return ValidationFlow.Stop;",
            body);

        Assert.DoesNotContain("switch (", body);
    }

    /// <summary>
    /// <c>IsValid</c> has no context, so it has no services either. A type that dispatches
    /// dynamically falls back to the interface default, which walks <c>Validate</c> - correct, just
    /// not free, and the same trade an applied rule already makes.
    /// </summary>
    [Fact]
    public void Runtime_SuppressesTheBooleanFastPath() {
        // The typed signature, not the bare name: the adapter beside the validator declares an
        // IsValid(object) of its own, and that one is meant to be there.
        Assert.DoesNotContain(
            "public bool IsValid(global::Sample.Checkout value)",
            Run("[ValidateNested(Polymorphism.Runtime)]").Checkout);
    }

    /// <summary>
    /// The adapters are what a runtime lookup finds. Emitted for every validated type in an assembly
    /// that dispatches dynamically, so a registry miss can only mean the assembly never registered.
    /// </summary>
    [Fact]
    public void Runtime_EmitsAnAdapterForEveryValidatedType() {
        var result = Run("[ValidateNested(Polymorphism.Runtime)]").Result;

        Assert.Empty(result.CompilationErrors);

        var emitted = string.Concat(result.Sources.Values);

        Assert.Contains("internal sealed class CardDynamicValidator : IDynamicValidator", emitted);
        Assert.Contains("internal sealed class PremiumDynamicValidator : IDynamicValidator", emitted);
        Assert.Contains("internal sealed class CheckoutDynamicValidator : IDynamicValidator", emitted);
        Assert.Contains("services.AddSingleton<IDynamicValidator, global::Sample.CardDynamicValidator>();", emitted);
        Assert.Contains("new DynamicValidatorRegistry(", emitted);
    }

    /// <summary>
    /// And none at all for an assembly that does not, because a registration roots its adapter and
    /// nobody should pay for a mode they never asked for.
    /// </summary>
    [Theory]
    [InlineData("[ValidateNested(Polymorphism.CompileTime)]")]
    [InlineData("[ValidateNested(Polymorphism.DeclaredOnly)]")]
    public void WithoutARuntimeDescent_NoAdaptersAreEmitted(string nested) {
        var emitted = string.Concat(Run(nested).Result.Sources.Values);

        Assert.DoesNotContain("IDynamicValidator", emitted);
        Assert.DoesNotContain("DynamicValidatorRegistry", emitted);
    }

    /// <summary>
    /// A sealed or value type's runtime type can never differ from its declared type, so dispatching
    /// on it buys a container lookup and nothing else.
    /// </summary>
    [Fact]
    public void RuntimeOnASealedTarget_IsVM0032() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Address {
                [Required]
                public string? Street { get; init; }
            }

            public sealed record Person {
                [ValidateNested(Polymorphism.Runtime)]
                public Address? Home { get; init; }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0032");
    }

    private static int Occurrences(string text, string value) {
        var count = 0;

        for (var i = text.IndexOf(value, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }
}
