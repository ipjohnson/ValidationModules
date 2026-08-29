namespace ValidationModules;

/// <summary>
/// Supplies a <see cref="ValidationMessageInfo"/>'s template at render time instead of the baked
/// <see cref="ValidationMessageInfo.Template"/> string.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read per render, and that is the point.</b> A resx accessor property consults
/// <c>CultureInfo.CurrentUICulture</c> and the satellite fallback chain on every read; capturing
/// its value once into a static field would freeze whichever culture happened to be current at
/// first use. The generator emits an implementation of this interface for a mapped DataAnnotations
/// attribute that sets <c>ErrorMessageResourceType</c> - a direct property read, so the resource
/// class is rooted for the trimmer and nothing resolves reflectively.
/// </para>
/// <para>
/// The template returned still carries holes, filled by the same renderer as everything else. The
/// error is passed in rather than just the field so an implementation can vary its template by
/// code or severity if it has reason to; most read nothing.
/// </para>
/// </remarks>
public interface IValidationMessageProvider {

    /// <summary>The template to render for this error, holes included.</summary>
    /// <param name="error">The error being rendered.</param>
    string Template(in ValidationError error);
}
