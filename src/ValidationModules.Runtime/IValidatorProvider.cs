namespace ValidationModules;

/// <summary>
/// Resolves the validator for a type chosen at runtime, without reflecting over a generic
/// definition to do it.
/// </summary>
/// <remarks>
/// <para>
/// The live consumer is <see cref="DescribedValidator{T}"/>: a rule class that declares
/// <c>Nested</c> or <c>Each</c> needs the nested type's validator, and on the generator-less path
/// there is nothing to bake that reference into. An implementation is a <c>typeof(T) ==</c> ladder
/// over the assembly's validated types, ending in a cast over a closed generic type. That is the
/// whole point of it: the reflective spelling is
/// <c>MakeGenericType(typeof(IValidatorFor&lt;&gt;), petType)</c>, which is exactly what this
/// library exists to avoid.
/// </para>
/// <para>
/// <b>The profile members were withdrawn for 1.0.0, and adding them back is additive.</b> This
/// interface carried <c>GetValidator&lt;T&gt;(Type profile)</c> and <c>GetProfiles&lt;T&gt;()</c>
/// before profiles existed. Because a consumer <i>implements</i> this interface, restoring them
/// naively in 1.1 would break every implementer - so restore them as default interface members
/// (returning <see langword="null"/> and an empty list), which net8.0 supports and which breaks
/// nobody. <c>docs/deferred-features.md</c> records why that is the one member set here needing a
/// technique rather than a plain re-add.
/// </para>
/// </remarks>
public interface IValidatorProvider {

    /// <summary>
    /// The validator for <typeparamref name="T"/>, or <see langword="null"/> if it has none.
    /// </summary>
    IValidatorFor<T>? GetValidator<T>();
}
