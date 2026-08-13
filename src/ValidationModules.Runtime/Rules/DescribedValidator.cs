using ValidationModules.Naming;
using ValidationModules.Rules;

namespace ValidationModules;

/// <summary>
/// Runs an <see cref="IValidationRulesFor{T}"/> without the source generator.
/// </summary>
/// <remarks>
/// <para>
/// The engine that makes a rules class portable. Another source generator - or a hand-written class -
/// can declare rules, register this, and validation works with none of this package's build-time
/// machinery present. That case is not exotic: plan §7.2 settled that cross-assembly convention
/// matching is unavailable and each assembly emits and registers its own validators, so a rules class
/// arriving from a referenced assembly is precisely what our generator cannot serve.
/// </para>
/// <para>
/// <b>Cost against the generated engine.</b> One interface dispatch and one delegate call per rule,
/// against a branch. It is the same difference the library exists to remove, so where the generator
/// does run it emits a validator and this type is not involved.
/// </para>
/// <para>
/// <b><see cref="IValidationRulesFor{T}.Describe"/> runs once, here, in the constructor.</b>
/// Registered as a singleton, that satisfies plan §2's "rule graphs are built once, never per
/// validation call" the same way a generated validator does - by there being nothing left to build
/// when the first request arrives.
/// </para>
/// <para>
/// <b>Do not register this for a rules class the generator already compiled.</b>
/// <see cref="ValidationRunner{T}"/> merges every registered <see cref="IValidatorFor{T}"/>, so both
/// would run and every error would appear twice with nothing to tell them apart. Within one
/// compilation the generator sees both and reports VM0074.
/// </para>
/// </remarks>
/// <typeparam name="T">The type being validated.</typeparam>
public sealed class DescribedValidator<T> : IValidatorFor<T> {
    private readonly ICompiledRule<T>[] _rules;

    /// <summary>
    /// Builds the rule set by running <paramref name="rules"/> once.
    /// </summary>
    /// <param name="rules">The declaration.</param>
    /// <param name="nested">
    /// Resolves validators for <c>Nested</c> and <c>Each</c> targets. Only needed when the
    /// declaration descends into something; a rules class of scalars and predicates needs none.
    /// </param>
    /// <param name="namer">
    /// Turns selector text into wire names. Defaults to camel case, matching the generator's own
    /// default. This is the one place the two engines can disagree: the generator also reads
    /// <c>[JsonPropertyName]</c> and <c>[DataMember]</c>, which cannot be seen from here without
    /// reflection, so a property carrying one wants an explicit <c>field:</c> on its rule.
    /// See API-SURFACE.md §19.9.
    /// </param>
    public DescribedValidator(
        IValidationRulesFor<T> rules,
        IValidatorProvider? nested = null,
        IValidationFieldNamer? namer = null) {

        ArgumentNullException.ThrowIfNull(rules);

        var builder = new ValidationRules<T>(namer ?? CamelCaseFieldNamer.Instance, nested);
        rules.Describe(builder);

        _rules = builder.Build();
    }

    /// <inheritdoc/>
    public void Validate(ref ValidationContext context, T value) {
        // Indexed over an array, not foreach over a list: HANDOFF.md §2.3 measured exactly 32 bytes
        // for the enumerator ValidationRunner<T> was boxing on the same shape, and a clean pass here
        // has to allocate nothing.
        for (var i = 0; i < _rules.Length; i++) {
            _rules[i].Apply(ref context, value);
        }
    }
}
