namespace ValidationModules;

/// <summary>
/// One culture's message templates, keyed by the stable vocabulary: a wire code
/// (<c>required</c>, or a user code like <c>date_order</c>), or a shape key beneath the four codes
/// whose sentence varies with their arguments (<c>string_length.at_most</c>,
/// <c>range.greater_than</c>, <c>enum.denied</c> - see
/// <see cref="ValidationMessageTemplates.KeyOf"/>).
/// </summary>
/// <remarks>
/// <para>
/// Implementations are generated from <c>*.validation-messages.json</c> files - a switch over
/// string literals, validated at build time against the shape inventory
/// (docs/language-packs.md) - and registered as singletons by the same
/// <c>Add&lt;Assembly&gt;Validators()</c> that registers everything else. Hand-written
/// implementations are equally welcome; the contract is only "a template, or null".
/// </para>
/// <para>
/// Keys are contracts and templates are prose: a pack never keys on the English wording, so
/// rewording a default breaks no pack, and a shape a pack does not know simply renders the
/// default - additive in both directions.
/// </para>
/// </remarks>
public interface IValidationLanguagePack {

    /// <summary>
    /// The culture this pack translates - <c>"fr"</c>, <c>"zh"</c>, <c>"fr-CA"</c>. Matched
    /// case-insensitively against <c>CultureInfo.CurrentUICulture</c> and its parents by
    /// <see cref="LanguagePackFormatter"/>, so a neutral-culture pack serves every region beneath
    /// it.
    /// </summary>
    string Culture { get; }

    /// <summary>
    /// The template for one key, holes included - <c>{field}</c>, <c>{0}</c>… - or null when this
    /// pack has nothing to say about it and the next layer should answer.
    /// </summary>
    /// <param name="key">A wire code or shape key.</param>
    string? Template(string key);
}
