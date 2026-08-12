namespace ValidationModules.SourceGenerator.Impl.Models;

/// <summary>What a constraint checks. One kind per code in the runtime's vocabulary.</summary>
public enum ConstraintKind {
    Required,
    StringLength,
    Range,
    Pattern,
    AllowedValues,
    ItemCount,
}

/// <summary>
/// One constraint, with its arguments already read off the attribute and normalized.
/// </summary>
/// <remarks>
/// Both front-ends produce this, which is the point of it: a rule declared with
/// <c>[StringLength(1, 100)]</c> and one declared with DataAnnotations' equivalent are the same
/// model by the time the emitter sees them, so they cannot produce different code, different field
/// paths or different messages.
/// </remarks>
/// <param name="Kind">Which check this is.</param>
/// <param name="Code">Overrides the default code for the kind.</param>
/// <param name="Message">Overrides the composed message; when set the emitter writes a literal.</param>
/// <param name="AllowEmptyStrings">Required only: whether whitespace counts as present.</param>
/// <param name="Min">Lower bound, as written. Length and count use it as an int.</param>
/// <param name="Max">Upper bound, as written.</param>
/// <param name="ExclusiveMin">Range only.</param>
/// <param name="ExclusiveMax">Range only.</param>
/// <param name="Pattern">Pattern only: the regular expression.</param>
/// <param name="Anchored">
/// Pattern only. DataAnnotations' [RegularExpression] requires a whole-value match; the native
/// [Pattern] does not, because JSON Schema and OpenAPI patterns are unanchored. Two states rather
/// than two kinds - see API-SURFACE.md §18.3.
/// </param>
/// <param name="RegexOptions">Pattern only: flows to the emitted Regex.</param>
/// <param name="RegexAccessor">
/// Pattern only. The already-resolved expression that yields the Regex in the reference form -
/// "global::My.Patterns.Sku()" for a method, without parentheses for a property or field. Null
/// means the inline form, where the emitter declares the Regex itself.
/// </param>
/// <param name="Values">AllowedValues only: the permitted set, already rendered as C# literals.</param>
/// <param name="Negated">AllowedValues only: set by DataAnnotations' [DeniedValues].</param>
public sealed record ConstraintModel(
    ConstraintKind Kind,
    string? Code = null,
    string? Message = null,
    bool AllowEmptyStrings = false,
    string? Min = null,
    string? Max = null,
    bool ExclusiveMin = false,
    bool ExclusiveMax = false,
    string? Pattern = null,
    bool Anchored = false,
    int RegexOptions = 0,
    string? RegexAccessor = null,
    EquatableArray<string> Values = default,
    bool Negated = false) : IEquatable<ConstraintModel>;
