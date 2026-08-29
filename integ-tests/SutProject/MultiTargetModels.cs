using ValidationModules;

namespace SutProject.Declared;

/// <summary>
/// One rules class, two targets: a class implements <c>IValidationRulesFor&lt;T&gt;</c> once per
/// type it describes, providing one <c>Describe</c> overload each. Each target still gets its own
/// validator; the shared private constant is the cohesion payoff.
/// </summary>
public sealed record Invoice {
    public string? Number { get; init; }
}

public sealed record CreditNote {
    public string? Number { get; init; }

    public decimal Amount { get; init; }
}

public sealed class LedgerRules :
    IValidationRulesFor<Invoice>,
    IValidationRulesFor<CreditNote> {

    private const int NumberLength = 10;

    public static void Describe(ValidationRules<Invoice> rules, Invoice x) {
        rules.Require(x.Number).Length(NumberLength, NumberLength);
    }

    public static void Describe(ValidationRules<CreditNote> rules, CreditNote x) {
        rules.Require(x.Number).Length(NumberLength, NumberLength);
        rules.Ensure(x.Amount > 0m, code: "positive");
    }
}
