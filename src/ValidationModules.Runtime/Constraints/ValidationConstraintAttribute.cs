namespace ValidationModules.Constraints;

/// <summary>
/// The base every constraint derives from. Carries profile attribution and per-rule overrides.
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
/// <b>Profile attribution.</b> A constraint with none of <see cref="FromProfile"/>,
/// <see cref="UntilProfile"/> or <see cref="Profiles"/> applies in <i>every</i> profile including
/// the default. Setting any of them restricts it, and excludes it from the default profile.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = true,
    Inherited = true)]
public abstract class ValidationConstraintAttribute : Attribute {

    /// <summary>
    /// The first profile on the chain this rule applies to, inclusive. Walks the
    /// <see cref="IValidationProfile{TPredecessor}"/> ordering, so a rule introduced in V2 needs no
    /// edit when V4 lands.
    /// </summary>
    public Type? FromProfile { get; init; }

    /// <summary>
    /// The first profile on the chain this rule stops applying to, exclusive. This is how a
    /// relaxation is expressed - by the rule no longer admitting the later profile, rather than by
    /// a counter-rule that removes it.
    /// </summary>
    public Type? UntilProfile { get; init; }

    /// <summary>
    /// An explicit profile set, for profiles that are not on a chain - <c>Strict</c>,
    /// <c>TenantA</c>, <c>Draft</c>.
    /// </summary>
    /// <remarks>
    /// Collection expressions are legal here: <c>Profiles = [typeof(Strict)]</c>. The
    /// <c>new[] { ... }</c> form also works, for consumers pinned to an older language version.
    /// </remarks>
    public Type[]? Profiles { get; init; }

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
