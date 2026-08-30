namespace ValidationModules.Constraints;

/// <summary>
/// The base every constraint derives from. Carries the per-rule overrides.
/// </summary>
/// <remarks>
/// <para>
/// <b>The names match DataAnnotations on purpose, and the vocabulary is a superset.</b> Every
/// <c>System.ComponentModel.DataAnnotations</c> validation attribute has an equivalent here, under
/// the same name where the concept is the same - so a model file needs exactly one using, and
/// migrating means swapping the directive rather than rewriting the model. The shared names only
/// hurt a file that imports both namespaces (CS0104 on the bare attribute), which complete
/// coverage makes unnecessary. Keeping constraints out of <c>ValidationModules</c> itself means
/// service code, which imports the contracts, never sees the collision even then.
/// </para>
/// <para>
/// <b>Profile attribution is not here, and its absence is deliberate.</b> <c>FromProfile</c>,
/// <c>UntilProfile</c> and <c>Profiles</c> shipped on this type before the feature behind them
/// existed, so setting one was a build error rather than a restriction. They were withdrawn for
/// 1.0.0 rather than pinned into the first stable surface. Adding init-only properties back is
/// additive in both source and binary, so this closes nothing.
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
