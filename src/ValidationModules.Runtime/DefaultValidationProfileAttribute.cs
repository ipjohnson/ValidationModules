namespace ValidationModules;

/// <summary>
/// Redirects the unprofiled <see cref="IValidatorFor{T}"/> registration for this assembly to a
/// named profile.
/// </summary>
/// <remarks>
/// Without this, the default validator carries only the rules that apply in every profile - the
/// unattributed ones. That is the coherent meaning of "no profile", but it means adding
/// <c>[Required(FromProfile = typeof(V2))]</c> to a type silently weakens what an injected
/// <see cref="IValidatorFor{T}"/> checks. The generator warns when an assembly has profiled rules
/// and no default declared; this attribute is the answer to that warning.
///
/// The common-core validator is still emitted, and still reachable by name.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DefaultValidationProfileAttribute : Attribute {

    /// <summary>
    /// Declares which profile the bare <see cref="IValidatorFor{T}"/> resolves to.
    /// </summary>
    /// <param name="profile">A type implementing <see cref="IValidationProfile"/>.</param>
    public DefaultValidationProfileAttribute(Type profile) {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
    }

    /// <summary>The profile the bare registration resolves to.</summary>
    public Type Profile { get; }
}
