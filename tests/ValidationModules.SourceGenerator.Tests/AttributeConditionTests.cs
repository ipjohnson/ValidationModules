using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>When</c> and <c>Unless</c> on a constraint: the attribute surface of conditional rules.
/// </summary>
/// <remarks>
/// A condition compiles to one conjunct on the test the constraint already emitted, and to one
/// local hoisted above the method body. Hoisting is not an optimization - a condition may read live
/// static state, so evaluating it once per pass and once per guarded constraint are different
/// answers, and once per pass is what the described engine also owes.
/// </remarks>
public class AttributeConditionTests {

    private static string Emit(string members, string extra = "") => Body(GeneratorHarness.Run($$"""
        using ValidationModules.Constraints;

        namespace Sample;

        public record Claim {
        {{members}}
        }
        {{extra}}
        """));

    private static string Body(GeneratorHarness.Result result) {
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        return result.Sources.Single(source => source.Key.Contains("ClaimValidator")).Value;
    }

    // -- the three accepted shapes -----------------------------------------------------------

    [Fact]
    public void BoolProperty_IsReadDirectly() {
        var body = Emit("""
                public bool IsAuto { get; init; }

                [Required(When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }
            """);

        Assert.Contains("var c0 = value.IsAuto;", body);
        Assert.Contains("if (c0 && (string.IsNullOrWhiteSpace(value.PolicyNumber)) && global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"policyNumber\", value: value.PolicyNumber).ShouldStop)", body);
    }

    [Fact]
    public void ParameterlessBoolMethod_IsCalledOnTheValue() {
        var body = Emit("""
                public bool IsAuto() => true;

                [Required(When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }
            """);

        Assert.Contains("var c0 = value.IsAuto();", body);
    }

    /// <summary>
    /// The static form is resolved on the validated type, because <c>When</c> carries a member name
    /// and nothing else - there is no <c>WhenType</c>, so shared logic is reached by a one-line
    /// forwarder on the model rather than by naming another class.
    /// </summary>
    [Fact]
    public void StaticBoolMethodTakingTheModel_IsCalledWithTheValue() {
        var body = Emit("""
                public static bool IsAuto(Claim value) => value.Kind == 1;

                public int Kind { get; init; }

                [Required(When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }
            """);

        Assert.Contains("var c0 = global::Sample.Claim.IsAuto(value);", body);
    }

    [Fact]
    public void Unless_BakesTheNegationIntoTheCondition() {
        var body = Emit("""
                public bool IsDraft { get; init; }

                [Required(Unless = nameof(IsDraft))]
                public string? PolicyNumber { get; init; }
            """);

        Assert.Contains("var c0 = !(value.IsDraft);", body);
    }

    // -- hoisting ----------------------------------------------------------------------------

    /// <summary>
    /// One local per distinct condition, however many constraints name it. This is what makes
    /// "evaluated once per pass" true rather than merely intended.
    /// </summary>
    [Fact]
    public void OneConditionUsedTwice_IsHoistedOnce() {
        var body = Emit("""
                public bool IsAuto { get; init; }

                [Required(When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }

                [Range(1, 5, When = nameof(IsAuto))]
                public int Priority { get; init; }
            """);

        var validate = Method(body, "public global::ValidationModules.ValidationFlow Validate");

        Assert.Equal(1, Occurrences(validate, "var c0 = value.IsAuto;"));
        Assert.DoesNotContain("var c1 =", validate);
    }

    [Fact]
    public void TwoDistinctConditions_EachGetTheirOwnLocal() {
        var body = Emit("""
                public bool IsAuto { get; init; }
                public bool IsExpedited { get; init; }

                [Required(When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }

                [Range(1, 5, When = nameof(IsExpedited))]
                public int Priority { get; init; }
            """);

        Assert.Contains("var c0 = value.IsAuto;", body);
        Assert.Contains("var c1 = value.IsExpedited;", body);
    }

    /// <summary>
    /// Conditions are collected per method body, not per type, so each declares its own locals.
    /// </summary>
    /// <remarks>
    /// This matters because <c>IsValid</c> skips Warning and Info constraints: a condition only
    /// those reference must not be declared in it, or the generated file carries an unused local
    /// and the solution stops being warning-free. Severity is declared through the rules DSL rather
    /// than through attributes, so the skip itself is exercised where that surface is tested; what
    /// is checkable from here is that the two scopes are genuinely separate.
    /// </remarks>
    [Fact]
    public void ConditionsAreScopedPerMethodBody() {
        var body = Emit("""
                public bool IsAuto { get; init; }

                [Required(When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }
            """);

        var validate = Method(body, "public global::ValidationModules.ValidationFlow Validate");
        var isValid = Method(body, "public bool IsValid");

        Assert.Equal(1, Occurrences(validate, "var c0 = value.IsAuto;"));
        Assert.Equal(1, Occurrences(isValid, "var c0 = value.IsAuto;"));
    }

    // -- interaction with Required ------------------------------------------------------------

    /// <summary>
    /// A guarded <c>Required</c> suppresses only when it runs. Condition false means the test is
    /// false, nothing is recorded, and nothing on the field is suppressed - which falls out of the
    /// condition being part of the test rather than needing a case of its own.
    /// </summary>
    [Fact]
    public void GuardedRequired_FoldsItsConditionIntoTheSuppressionLocal() {
        var body = Emit("""
                public bool IsAuto { get; init; }

                [Required(When = nameof(IsAuto))]
                [StringLength(2, 8)]
                public string? PolicyNumber { get; init; }
            """);

        Assert.Contains(
            "var missingPolicyNumber = c0 && (string.IsNullOrWhiteSpace(value.PolicyNumber));",
            body);
    }

    /// <summary>
    /// A condition on one constraint of a field does not leak onto the others.
    /// </summary>
    [Fact]
    public void UnconditionalConstraintsOnAGuardedField_StayUnconditional() {
        var body = Emit("""
                public bool IsAuto { get; init; }

                [Required]
                [StringLength(2, 8, When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }
            """);

        Assert.Contains("if (missingPolicyNumber && global::ValidationModules.ValidationContextExtensions.ReportRequired(ctx, \"policyNumber\", value: value.PolicyNumber).ShouldStop)", body);
        Assert.Contains("if (c0 && !missingPolicyNumber && (", body);
    }

    // -- guarded descent ----------------------------------------------------------------------

    /// <summary>
    /// <c>[ValidateNested]</c> inherits <c>When</c> from the constraint base, which is the
    /// discriminated-union case: the block a discriminator says to ignore reports nothing.
    /// </summary>
    [Fact]
    public void ValidateNested_CanBeGuarded() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Auto {
                [Required]
                public string? Plate { get; init; }
            }

            public record Claim {
                public bool IsAuto { get; init; }

                [ValidateNested(When = nameof(IsAuto))]
                public Auto? Auto { get; init; }
            }
            """);

        var body = Body(result);

        Assert.Contains("var c0 = value.IsAuto;", body);
        Assert.Contains("if (c0 && (value.Auto is { } nestedAuto))", body);
    }

    // -- diagnostics --------------------------------------------------------------------------

    [Fact]
    public void ConditionNamingAMemberThatDoesNotExist_IsVM0028() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Claim {
                [Required(When = "NoSuchMember")]
                public string? PolicyNumber { get; init; }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0028");
    }

    [Theory]
    [InlineData("public int IsAuto { get; init; }")]
    [InlineData("public string? IsAuto { get; init; }")]
    [InlineData("public bool IsAuto(int x) => true;")]
    [InlineData("public static bool IsAuto(int x) => true;")]
    public void ConditionNamingSomethingThatIsNotAPredicate_IsVM0029(string member) {
        var result = GeneratorHarness.Run($$"""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Claim {
                {{member}}

                [Required(When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0029");
    }

    [Fact]
    public void ConstraintSettingBothWhenAndUnless_IsVM0033() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Claim {
                public bool IsAuto { get; init; }
                public bool IsDraft { get; init; }

                [Required(When = nameof(IsAuto), Unless = nameof(IsDraft))]
                public string? PolicyNumber { get; init; }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "VM0033");
    }

    /// <summary>
    /// A condition declared on a shared base is usable from every type that inherits it - the same
    /// reach the constraints themselves have, and the reason resolution runs against the type being
    /// validated rather than the one that declared the constraint.
    /// </summary>
    [Fact]
    public void ConditionDeclaredOnABaseType_ResolvesFromTheDerivedType() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            public record Submission {
                public bool IsAuto { get; init; }

                [Required(When = nameof(IsAuto))]
                public string? PolicyNumber { get; init; }
            }

            public record Claim : Submission {
                [Required]
                public string? Reference { get; init; }
            }
            """);

        Assert.Contains("var c0 = value.IsAuto;", Body(result));
    }

    /// <summary>The text of one emitted method, so a per-method claim is checked per method.</summary>
    private static string Method(string body, string signature) {
        var start = body.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not emitted");

        var end = body.IndexOf("\n    }", start, StringComparison.Ordinal);
        return end < 0 ? body[start..] : body[start..end];
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
