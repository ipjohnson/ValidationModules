namespace ValidationModules;

/// <summary>
/// One culture's message templates: pure data, keyed by the stable vocabulary - a wire code
/// (<c>required</c>, or a user code like <c>date_order</c>), or a shape key beneath the four codes
/// whose sentence varies with their arguments (<c>string_length.at_most</c>,
/// <c>range.greater_than</c>, <c>enum.denied</c> - see
/// <see cref="ValidationMessageTemplates.KeyOf"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A pack answers no lookups; it is enumerated once.</b>
/// <see cref="LanguagePackFormatter"/> folds every registered pack into one merged table per
/// requested culture on that culture's first render, so per-error reads are constant-time
/// whatever the pack count - the storage-strategy benchmarks in
/// <c>benchmarks/…/Design/LanguagePackStorageBenchmarks.cs</c> are the numbers behind the shape,
/// and docs/language-packs.md records the decision.
/// </para>
/// <para>
/// Implementations are generated from <c>*.validation-messages.json</c> files - a static entry
/// array, validated at build time against the shape inventory - and registered as singletons by
/// the same <c>Add&lt;Assembly&gt;Validators()</c> that registers everything else. Hand-written
/// implementations are equally welcome; the contract is only "a culture, and its entries".
/// </para>
/// <para>
/// Keys are contracts and templates are prose: a pack never keys on the English wording, so
/// rewording a default breaks no pack, and a key a pack does not carry simply renders the next
/// layer's answer - additive in both directions.
/// </para>
/// </remarks>
public interface IValidationLanguagePack {

    /// <summary>
    /// The culture this pack translates - <c>"fr"</c>, <c>"zh"</c>, <c>"fr-CA"</c>. Matched
    /// case-insensitively against the requested culture and its parents, so a neutral-culture
    /// pack serves every region beneath it.
    /// </summary>
    string Culture { get; }

    /// <summary>
    /// The pack's entries: key to template, holes included (<c>{field}</c>, <c>{0}</c>…). Read
    /// once per merged table; order within one pack does not matter, because layering is defined
    /// across packs by registration order, never within one.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, string>> Templates { get; }
}
