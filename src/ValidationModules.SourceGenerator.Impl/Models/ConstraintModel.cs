namespace ValidationModules.SourceGenerator.Impl.Models;

/// <summary>What a constraint checks. One kind per code in the runtime's vocabulary.</summary>
public enum ConstraintKind {
    Required,
    StringLength,
    Range,
    Pattern,
    AllowedValues,

    /// <summary>
    /// An enum member check. Resolved against the member's type in the front end, which turns it
    /// into a comparison against the declared members, or a mask test on a [Flags] enum.
    /// </summary>
    EnumDefined,
    ItemCount,
    MultipleOf,

    /// <summary>
    /// Does not compile to a comparison - it calls <c>ConstraintChecks.AllUnique</c>. See
    /// <c>UniqueItemsAttribute</c>.
    /// </summary>
    UniqueItems,

    /// <summary>
    /// The format validators, each compiling to its <c>ConstraintChecks</c> check with the BCL's
    /// own semantics. Produced by both front ends - the native vocabulary carries them under the
    /// DataAnnotations names, so either attribute reaches the identical emitted check.
    /// </summary>
    Email,
    Phone,

    /// <summary>
    /// The one format kind whose member may be <c>System.Uri</c> as well as <c>string</c>; the
    /// emitted call resolves to the matching <c>ConstraintChecks.IsUrl</c> overload.
    /// </summary>
    Url,
    CreditCard,
    Base64,

    /// <summary>
    /// Carries its permitted set in <c>Values</c>/<c>ValueDisplays</c>, normalized at build time
    /// exactly as <c>FileExtensionsAttribute</c> normalizes its <c>Extensions</c> property, and
    /// hoisted into a static field by the emitter the way patterns are.
    /// </summary>
    FileExtension,

    /// <summary>
    /// A custom <c>ValidationAttribute</c> subclass, constructed once from
    /// <c>CustomConstruction</c> into a static field and invoked through
    /// <c>DataAnnotationsSupport</c>. Unlike every kind above, its check is user code run at
    /// validation time rather than a test this emitter writes - the faithful reading of an
    /// attribute whose semantics only its author knows.
    /// </summary>
    CustomAttribute,

    /// <summary>
    /// A <c>[CustomValidation]</c> target, resolved at build time to the static method in
    /// <c>CustomAccessor</c> and called directly - no attribute instance, no reflective dispatch.
    /// </summary>
    CustomValidationMethod,

    /// <summary>
    /// A native <c>CustomConstraintAttribute</c> subclass: the author's own check, compiled like a
    /// built-in. <c>CustomAccessor</c> holds the attribute class's static <c>IsValid</c>;
    /// <c>Values</c> carries the constructor arguments, already rendered, that follow the member's
    /// value in the call. The high-performance counterpart of <see cref="CustomAttribute"/>: no
    /// instance, no context, nothing allocated on a passing value.
    /// </summary>
    CustomCheck,

    /// <summary>
    /// An attribute implementing <c>IConstraintFor&lt;T&gt;</c>: the author's own instance,
    /// constructed by generated code from <c>CustomConstruction</c> and invoked through the two
    /// members the interface pins - <c>IsValid</c> on the boolean path, <c>Validate</c> on the
    /// reporting one. The stateful middle of the custom family: cheaper than
    /// <see cref="CustomAttribute"/> because nothing boxes and no context is built, richer than
    /// <see cref="CustomCheck"/> because the constructor may precompute and the reporting side
    /// sees the pass's context.
    /// </summary>
    CustomInstance,

    /// <summary>
    /// A predicate declared with <c>rules.Ensure(…)</c>. Carries no bounds and composes no message -
    /// its message was rendered from its own source text when it was read.
    /// </summary>
    Predicate,
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
/// than two kinds.
/// </param>
/// <param name="RegexOptions">Pattern only: flows to the emitted Regex.</param>
/// <param name="MatchTimeoutMilliseconds">
/// Pattern only, inline form only: the per-match timeout, flowed to the emitted Regex as its third
/// constructor argument. Zero means none, and emits the single-argument constructor - which is
/// load-bearing, because it is what lets ILC prove RegexOptions.Compiled is never set and trim the
/// RegexCompiler path. The reference form has nowhere to put this: the consumer owns the
/// [GeneratedRegex] and sets MatchTimeoutMilliseconds on it directly.
/// </param>
/// <param name="RegexAccessor">
/// Pattern only. The already-resolved expression that yields the Regex in the reference form -
/// "global::My.Patterns.Sku()" for a method, without parentheses for a property or field. Null
/// means the inline form, where the emitter declares the Regex itself.
/// </param>
/// <param name="Divisor">
/// MultipleOf only: the divisor, already rendered in the member's own denomination - "5" for an
/// integral member, "0.05m" for a decimal one, and also "0.05m" for a double or float, because the
/// check for those is decided in the decimal domain.
/// </param>
/// <param name="DecimalDomain">
/// MultipleOf only. True when the member is double or float, so the emitted test calls
/// <c>ConstraintChecks.IsMultipleOf</c> rather than writing <c>%</c> - which in binary floating
/// point rejects 0.3, 1.05 and 99.99 against a divisor of 0.01.
/// </param>
/// <param name="Values">AllowedValues only: the permitted set, already rendered as C# literals.</param>
/// <param name="ValueDisplays">
/// AllowedValues only: the same set as a reader should see it, positionally matching
/// <paramref name="Values"/>. Carried rather than derived, because the two differ for exactly one
/// kind and cannot be told apart after the fact - an enum member compares as
/// <c>global::My.Tier.Pro</c> and reads as <c>Pro</c>, while a string containing a dot would be
/// mangled by any last-segment heuristic applied to it. Empty means "same as Values", which is
/// what every front end but the native one produces.
/// </param>
/// <param name="Negated">AllowedValues only: set by DataAnnotations' [DeniedValues].</param>
/// <param name="PredicateAccessor">
/// Predicate only. The fully qualified name of the static method the predicate was lifted into -
/// "global::My.PetRules_Rules.Rule0". The predicate is not inlined at the constraint site because
/// the lambda's source resolves against its own file's using directives, which the validator file
/// does not have; the lifted method lives in a file that carries them.
/// </param>
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
    int MatchTimeoutMilliseconds = 0,
    string? RegexAccessor = null,
    string? Divisor = null,
    bool DecimalDomain = false,
    EquatableArray<string> Values = default,
    EquatableArray<string> ValueDisplays = default,
    bool Negated = false,
    string? PredicateAccessor = null,

    /// <summary>
    /// The field this one constraint reports under, when it differs from its property's. A rule is
    /// anchored to a property so both engines agree on ordering, but <c>field:</c> renames the
    /// error rather than moving the rule - and a property can carry several rules each naming a
    /// different field, so the name cannot live on the property.
    /// </summary>
    string? Field = null,

    /// <summary>
    /// The severity member to report with - <c>Warning</c> or <c>Info</c>. Null is
    /// <c>Error</c>, which is both the default and what an omitted argument means.
    /// </summary>
    string? Severity = null,

    /// <summary>
    /// For <see cref="ConstraintKind.EnumDefined"/> on a <c>[Flags]</c> enum: the OR of every
    /// declared member, tested as a mask rather than as membership because a combination is a
    /// legitimate value that no single member equals.
    /// </summary>
    string? FlagsMask = null,

    /// <summary>
    /// The member named by <c>When</c>, exactly as written. Resolved by the front end into
    /// <see cref="Condition"/>; carried separately so that "both were set" stays answerable, which
    /// is VM1403.
    /// </summary>
    string? WhenMember = null,

    /// <summary>The member named by <c>Unless</c>, exactly as written.</summary>
    string? UnlessMember = null,

    /// <summary>
    /// The resolved condition: a complete boolean expression in terms of <c>value</c>, with any
    /// negation already baked in, or null when the constraint is unconditional.
    /// </summary>
    /// <remarks>
    /// One field rather than an expression plus a "negated" flag, because the emitter must not be
    /// able to tell the surfaces apart. An attribute condition arrives as <c>value.IsAuto</c>, a
    /// DSL one as <c>global::My.ClaimRules_Rules.Cond0(value)</c>, and both are hoisted and tested
    /// identically.
    /// </remarks>
    string? Condition = null,

    /// <summary>
    /// <see cref="ConstraintKind.CustomAttribute"/> only: the complete construction expression -
    /// <c>new global::My.EvenNumberAttribute(2) { ErrorMessage = "…" }</c> - every argument
    /// rendered fully qualified from the attribute's compile-time constants. The emitter hoists it
    /// into a static field, so the instance is built once per validator rather than per pass.
    /// </summary>
    string? CustomConstruction = null,

    /// <summary>
    /// <see cref="ConstraintKind.CustomValidationMethod"/> only: the fully qualified static method
    /// - <c>global::My.Checks.EvenNumber</c> - already resolved and signature-checked, so the
    /// emitter writes a direct call.
    /// </summary>
    string? CustomAccessor = null,

    /// <summary>
    /// <see cref="ConstraintKind.CustomValidationMethod"/> only: whether the method's second
    /// parameter takes the DataAnnotations <c>ValidationContext</c>, which the emitted call then
    /// builds through <c>DataAnnotationsSupport.CreateContext</c>.
    /// </summary>
    bool CustomTakesContext = false,

    /// <summary>
    /// <see cref="ConstraintKind.CustomInstance"/> only: the attribute class, fully qualified. It
    /// types the hoisted field, so the woven calls stay direct - and inlineable - whenever the
    /// class implements the interface implicitly.
    /// </summary>
    string? InstanceType = null,

    /// <summary>
    /// <see cref="ConstraintKind.CustomInstance"/> only: the <c>IConstraintFor&lt;T&gt;</c>
    /// instantiation the member matched, fully qualified. A call the class cannot bind - a member
    /// left to the interface's default implementation, or implemented explicitly - goes through a
    /// cast to this.
    /// </summary>
    string? InstanceInterface = null,

    /// <summary>
    /// <see cref="ConstraintKind.CustomInstance"/> only: <c>Validate</c> is not a public method of
    /// the class, so the woven call must go through <see cref="InstanceInterface"/>.
    /// </summary>
    bool ValidateThroughInterface = false,

    /// <summary>
    /// The same question for <c>IsValid</c>, answered separately: an author who overrides
    /// <c>Validate</c> but implements <c>IsValid</c> explicitly - or the reverse - gets each call
    /// bound the cheapest way it can be.
    /// </summary>
    bool IsValidThroughInterface = false,

    /// <summary>
    /// <see cref="ConstraintKind.CustomInstance"/> only: <c>[PerValidationInstance]</c> is on the
    /// attribute class, so the emitter constructs the attribute at every check instead of hoisting
    /// one instance into a static field.
    /// </summary>
    bool PerPassInstance = false,

    /// <summary>
    /// The <see cref="Message"/> came from a DataAnnotations attribute and follows that dialect:
    /// <c>{0}</c> is the member's display name. The reader bakes every other placeholder in - the
    /// remaining arguments are compile-time constants - and the emitter substitutes <c>{0}</c>
    /// with the property's resolved display name, so the wire carries finished text where
    /// DataAnnotations would have called <c>string.Format</c> per failure.
    /// </summary>
    bool DataAnnotationsMessage = false,

    /// <summary>
    /// A DataAnnotations attribute whose message lives in a resx:
    /// <c>ErrorMessageResourceType</c>/<c>ErrorMessageResourceName</c> resolved to the accessor
    /// property, fully qualified - <c>global::My.Resources.NameRequired</c>. The emitter wraps it
    /// in a <c>DelegateMessageProvider</c> read per render, so culture fallback works and nothing
    /// resolves reflectively. Null everywhere else, including when <see cref="Message"/> is set -
    /// DataAnnotations itself treats an explicit <c>ErrorMessage</c> as winning.
    /// </summary>
    string? MessageResourceAccessor = null,

    /// <summary>
    /// The template arguments a resx message's <c>{1}</c>… placeholders refer to, as C# constant
    /// expressions, in the declaring attribute's own <c>FormatErrorMessage</c> order - which is not
    /// this model's Min/Max order for every attribute ([StringLength] formats max before min).
    /// Meaningful only beside <see cref="MessageResourceAccessor"/>.
    /// </summary>
    EquatableArray<string> MessageResourceArgs = default) : IEquatable<ConstraintModel>;
