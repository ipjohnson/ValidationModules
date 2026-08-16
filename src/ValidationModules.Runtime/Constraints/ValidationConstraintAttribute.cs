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
}
