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
    private readonly Func<T, bool>[] _conditions;
    private readonly int[][] _slots;

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
        _conditions = builder.Atoms;
        _slots = builder.Slots;
    }

    /// <inheritdoc/>
    public ValidationFlow Validate(ref ValidationContext context, T value) {
        // Every distinct condition, evaluated once, before any rule is tested. This is the runtime
        // engine's version of the locals the emitter hoists above a method body, and it exists for
        // the same reason: a condition may read live static state, so once per pass and once per
        // guarded rule are different answers. Evaluating per rule would make the two engines
        // disagree, which is exactly what API-SURFACE.md §19.9 promises cannot happen.
        //
        // A stackalloc rather than an array, so a guarded clean pass allocates what an unguarded one
        // does - which is nothing.
        Span<bool> held = stackalloc bool[_conditions.Length];

        for (var i = 0; i < _conditions.Length; i++) {
            held[i] = _conditions[i](value);
        }

        var conditions = new ConditionValues(held, _slots);

        // Indexed over an array, not foreach over a list: HANDOFF.md §2.3 measured exactly 32 bytes
        // for the enumerator ValidationRunner<T> was boxing on the same shape, and a clean pass here
        // has to allocate nothing.
        for (var i = 0; i < _rules.Length; i++) {
            var rule = _rules[i];

            if (conditions.Holds(rule.ConditionIndex) &&
                rule.Apply(ref context, value, conditions).ShouldStop) {
                return ValidationFlow.Stop;
            }
        }

        return ValidationFlow.Continue;
    }
}
