namespace ValidationModules.Constraints;

/// <summary>
/// The base every constraint derives from. Carries the per-rule overrides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why constraints live in their own namespace.</b> Five of the names here collide with
/// <c>System.ComponentModel.DataAnnotations</c> - <c>Required</c>, <c>StringLength</c>,
/// <c>Range</c>, <c>AllowedValues</c>, <c>Length</c> - and a model file importing both namespaces
/// gets CS0104 on the bare attribute. Keeping them out of <c>ValidationModules</c> means service
/// code, which imports the contracts, never trips it; only a model file that explicitly asks for
/// both can.
/// </para>
/// <para>
/// <b>Profile attribution is not here, and its absence is deliberate.</b> <c>FromProfile</c>,
/// <c>UntilProfile</c> and <c>Profiles</c> shipped on this type before the feature behind them
/// existed, so setting one was a build error rather than a restriction. They were withdrawn for
/// 1.0.0 rather than pinned into the first stable surface. Adding init-only properties back is
/// additive in both source and binary, so this closes nothing - see <c>docs/deferred-features.md</c>
/// for the full reversibility analysis.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = true,
    Inherited = true)]
public abstract class ValidationConstraintAttribute : Attribute {

    /// <summary>
    /// Overrides the machine-readable code this constraint emits. Defaults to the constraint's own
    /// code - <c>required</c>, <c>string_length</c>, and so on.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Overrides the generated message. <c>{field}</c> is substituted at generation time.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Names a predicate on the validated type; this constraint is checked only when it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is resolved at build time against the type being validated, and must be one of
    /// three shapes: a <c>bool</c> property, a parameterless <c>bool</c> method, or a
    /// <c>static bool</c> method taking the model. All three keep <c>nameof</c> working, and none
    /// can close over anything - so the self-containment rule VM0072 enforces for
    /// <c>rules.Ensure</c> predicates is satisfied here by construction rather than by analysis.
    /// </para>
    /// <para>
    /// A condition is evaluated once per validation pass, not once per constraint that names it.
    /// Conditions are permitted to read live static state, so the two are observably different
    /// answers rather than two spellings of one.
    /// </para>
    /// <para>
    /// This lives on the base rather than on individual constraints so that every constraint has
    /// it, <see cref="ValidateNestedAttribute"/> included - a guarded descent is the discriminated
    /// union case, where the block a discriminator says to ignore should report nothing.
    /// </para>
    /// </remarks>
    public string? When { get; init; }

    /// <summary>
    /// The negation of <see cref="When"/>: this constraint is checked only when the named predicate
    /// does <i>not</i> hold.
    /// </summary>
    /// <remarks>
    /// Setting both <see cref="When"/> and <see cref="Unless"/> on one constraint is ambiguous by
    /// construction and is a build error (VM0033). Write two constraints, or one negated condition.
    /// </remarks>
    public string? Unless { get; init; }
}
