namespace ValidationModules;

/// <summary>
/// Resolves a validator for a type and profile chosen at runtime - tenant-driven validation, or a
/// document whose version is only known once it has been read.
/// </summary>
/// <remarks>
/// The generated implementation is a <c>typeof(T) ==</c> ladder over the assembly's validated
/// types, ending in a cast over a closed generic type. That is the whole point of it: the
/// reflective spelling of this lookup is
/// <c>MakeGenericType(typeof(IValidatorFor&lt;,&gt;), petType, profileType)</c>, which is exactly
/// what this library exists to avoid.
/// </remarks>
public interface IValidatorProvider {

    /// <summary>
    /// The validator for the default profile, or <see langword="null"/> if the type has none.
    /// </summary>
    IValidatorFor<T>? GetValidator<T>();

    /// <summary>
    /// The validator for a specific profile, or <see langword="null"/> if the type has none for it.
    /// </summary>
    /// <param name="profile">A type implementing <see cref="IValidationProfile"/>.</param>
    IValidatorFor<T>? GetValidator<T>(Type profile);

    /// <summary>
    /// Which profiles have a validator for <typeparamref name="T"/>. Empty if the type is not
    /// validated, or has no profiled rules.
    /// </summary>
    IReadOnlyList<Type> GetProfiles<T>();
}
