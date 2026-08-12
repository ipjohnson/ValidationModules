namespace ValidationModules;

/// <summary>
/// Marks a type as a validation profile - a named variant of a type's rules. Document standard
/// versions, tenant overlays, draft-versus-published state.
/// </summary>
/// <remarks>
/// <para>
/// Profiles are types, never instantiated, and every rule declares which profiles it belongs to.
/// A rule with no profile arguments applies in all of them, which is what makes the feature opt-in
/// and free: a codebase declaring no profiles generates exactly one validator per type and never
/// encounters the concept.
/// </para>
/// <para>
/// Profiles flatten at build time. Per (type, profile) the generator collects every rule whose
/// attribution admits that profile and emits one straight-line validator, so having five profiles
/// costs exactly what having one costs at runtime.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class V1 : IValidationProfile;
/// public sealed class V2 : IValidationProfile&lt;V1&gt;;
/// </code>
/// </example>
public interface IValidationProfile;

/// <summary>
/// Marks a profile as the successor of <typeparamref name="TPredecessor"/>, placing it on an
/// ordered chain.
/// </summary>
/// <remarks>
/// The relationship supplies <b>ordering only</b>. It does not inherit rules - a successor does not
/// start from its predecessor's rule set, which is what removes the need to subtract anything. That
/// a rule stopped applying in V2 is expressed by the rule not admitting V2, not by a counter-rule.
///
/// The ordering is what <c>FromProfile</c> and <c>UntilProfile</c> walk, so a linearly versioned
/// standard needs no attribute edits when V4 lands.
/// </remarks>
/// <typeparam name="TPredecessor">The profile immediately before this one.</typeparam>
public interface IValidationProfile<TPredecessor> : IValidationProfile
    where TPredecessor : IValidationProfile;
