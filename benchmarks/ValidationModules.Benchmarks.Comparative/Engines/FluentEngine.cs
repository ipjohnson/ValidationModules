using FluentValidation;
using ValidationModules.Benchmarks.Comparative.Models;

namespace ValidationModules.Benchmarks.Comparative.Engines;

/// <summary>
/// The FluentValidation side of the comparison: the same rules as the constraint attributes on
/// <see cref="Models.Customer"/> and friends, spelled the FluentValidation way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cascade mode is set to Stop deliberately.</b> A generated validator emits suppression as an
/// <c>else if</c> - a field that failed Required is not checked for length - and FluentValidation's
/// default is to run every rule in the chain regardless. Left at the default, FluentValidation would
/// produce a different error set on a failing payload and do more work to produce it, so the
/// comparison would be measuring two different specifications. <c>Stop</c> makes the two agree.
/// </para>
/// <para>
/// Everything else is idiomatic FluentValidation. No attempt is made to write it unusually fast or
/// unusually slow; a rule that has a direct spelling uses it, and only <c>[AllowedValues]</c> and
/// <c>[ItemCount]</c> fall through to <c>Must</c>, because FluentValidation has no closer
/// equivalent.
/// </para>
/// </remarks>
public sealed class CustomerFluentValidator : AbstractValidator<Customer> {

    /// <summary>
    /// Built once and shared. Constructing one runs every <c>RuleFor</c> and compiles a property
    /// accessor per rule, which <c>ValidatorConstructionComparison</c> prices separately.
    /// </summary>
    public static readonly CustomerFluentValidator Instance = new();

    public CustomerFluentValidator() {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Name).NotEmpty().Length(1, 100);
        RuleFor(x => x.Email).NotEmpty().Matches(Patterns.Email());
        RuleFor(x => x.Age).InclusiveBetween(0, 120);
        RuleFor(x => x.Tier).Must(IsKnownTier);
        RuleFor(x => x.Notes).MaximumLength(500);
    }

    /// <summary>
    /// Null passes, matching <c>[AllowedValues]</c>, which constrains the value only when there is
    /// one. Requiring it is <c>[Required]</c>'s job.
    /// </summary>
    private static bool IsKnownTier(string? tier) => tier is null or "gold" or "silver" or "bronze";
}

public sealed class AddressFluentValidator : AbstractValidator<Address> {
    public static readonly AddressFluentValidator Instance = new();

    public AddressFluentValidator() {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Line1).NotEmpty().Length(1, 120);
        RuleFor(x => x.City).NotEmpty().Length(1, 60);
        RuleFor(x => x.PostalCode).NotEmpty().Matches(Patterns.PostalCode());
    }
}

public sealed class OrderLineFluentValidator : AbstractValidator<OrderLine> {
    public static readonly OrderLineFluentValidator Instance = new();

    public OrderLineFluentValidator() {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Sku).NotEmpty().Matches(Patterns.Sku());
        RuleFor(x => x.Quantity).InclusiveBetween(1, 999);
    }
}

public sealed class OrderFluentValidator : AbstractValidator<Order> {
    public static readonly OrderFluentValidator Instance = new();

    public OrderFluentValidator() {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Reference).NotEmpty().Matches(Patterns.Reference());

        // SetValidator skips a null child, which is what the generated code's `is { }` check does.
        RuleFor(x => x.Buyer).SetValidator(CustomerFluentValidator.Instance!);
        RuleFor(x => x.ShipTo).SetValidator(AddressFluentValidator.Instance!);

        RuleFor(x => x.Lines).Must(lines => lines.Count is >= 1 and <= 100);
        RuleForEach(x => x.Lines).SetValidator(OrderLineFluentValidator.Instance);
    }
}

public sealed class BasketFluentValidator : AbstractValidator<Basket> {
    public static readonly BasketFluentValidator Instance = new();

    public BasketFluentValidator() {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Id).NotEmpty();
        RuleForEach(x => x.Lines).SetValidator(OrderLineFluentValidator.Instance);
    }
}
