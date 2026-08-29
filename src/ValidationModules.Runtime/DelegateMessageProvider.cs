namespace ValidationModules;

/// <summary>
/// An <see cref="IValidationMessageProvider"/> over a template accessor delegate. The shape the
/// generator emits for a DataAnnotations attribute whose message lives in a resx:
/// <c>new DelegateMessageProvider(static () =&gt; Resources.NameRequired)</c> - a direct property
/// read, so the resource class is rooted for the trimmer, nothing resolves reflectively, and the
/// read happens per render, which is what lets <c>CurrentUICulture</c> and the satellite fallback
/// chain do their work.
/// </summary>
public sealed class DelegateMessageProvider : IValidationMessageProvider {

    private readonly Func<string> _template;

    /// <summary>Wraps the accessor whose value is the template.</summary>
    /// <param name="template">Read once per render. A <c>static</c> lambda over a resx property is the intended shape.</param>
    public DelegateMessageProvider(Func<string> template) {
        ArgumentNullException.ThrowIfNull(template);

        _template = template;
    }

    /// <inheritdoc />
    public string Template(in ValidationError error) => _template();
}
