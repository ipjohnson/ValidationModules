using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// The VM#### descriptors. Every one of these is a rule the emitter would otherwise have to guess
/// at, so they are roughly half the work of the generator rather than a finishing touch.
/// </summary>
public static class ValidationDiagnostics {
    private const string Usage = "ValidationModules.Usage";

    private static DiagnosticDescriptor Descriptor(string id, string title, string message, DiagnosticSeverity severity) =>
        new(id, title, message, Usage, severity, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor StringConstraintOnNonString = Descriptor(
        "VM0001", "Constraint requires a string",
        "'{0}' applies to strings; '{1}' is '{2}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ItemCountOnNonCollection = Descriptor(
        "VM0002", "[ItemCount] requires a collection",
        "[ItemCount] applies to collections; '{0}' is '{1}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RangeOnUnorderedType = Descriptor(
        "VM0003", "[Range] requires an ordered type",
        "[Range] applies to numeric and date types; '{0}' is '{1}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RequiredOnNonNullableValueType = Descriptor(
        "VM0004", "[Required] has no effect",
        "'{0}' is a non-nullable value type, so it is always present and [Required] can never fail",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor InvalidPattern = Descriptor(
        "VM0006", "Pattern is not a valid regular expression",
        "The pattern on '{0}' is not a valid regular expression: {1}", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor NestedTypeHasNoRules = Descriptor(
        "VM0007", "[ValidateNested] target has no rules",
        "'{0}' declares no constraints and no [GenerateValidator], so [ValidateNested] on '{1}' validates nothing",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor MinExceedsMax = Descriptor(
        "VM0008", "Lower bound exceeds upper bound",
        "The bounds on '{0}' are inverted, so the constraint can never be satisfied", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InaccessibleProperty = Descriptor(
        "VM0009", "Constrained property is not readable",
        "'{0}' has no accessible getter, so its constraints cannot be evaluated", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor DataAnnotationsSkipped = Descriptor(
        "VM0010", "DataAnnotations constraint is not being compiled",
        "'{0}' on '{1}' is a DataAnnotations constraint and ValidationModules_DataAnnotations is set to Ignore, so it is not enforced",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor CompiledRegexRequested = Descriptor(
        "VM0016", "RegexOptions.Compiled is not meaningful here",
        "Patterns compile through [GeneratedRegex]; RegexOptions.Compiled on '{0}' is ignored",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// Declared with a fixed default severity so release tracking can discover it; the effective
    /// severity is overridden per site from the resolved policy, because the same situation is a
    /// build error for an AOT-facing project and unremarkable for a JIT one.
    /// </summary>
    public static readonly DiagnosticDescriptor InlinePatternUnderAot = Descriptor(
        "VM0017", "Inline pattern roots the regex engine",
        "The pattern on '{0}' is built from a string at run time, which roots the regex parser and " +
        "interpreter - about 450 KB on an AOT-published binary, once, however many patterns follow. " +
        "Declare it as a " +
        "[GeneratedRegex] and point at it: [Pattern(typeof({1}Patterns), nameof({1}Patterns.{0}))]. " +
        "Set ValidationModules_PatternPolicy to Allow to keep the inline form",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// The guard on a declaration surface that shipped ahead of its implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ValidationConstraintAttribute</c> carries <c>FromProfile</c>, <c>UntilProfile</c> and
    /// <c>Profiles</c>, all three documented at length on the attribute itself, and
    /// <c>IValidationProfile</c> exists in the runtime. Profiles are plan Stage 3 and are not built,
    /// so the generator reads none of it: one validator is emitted rather than one per profile, and
    /// every profiled rule is enforced in every profile.
    /// </para>
    /// <para>
    /// <b>An error rather than a warning, because of which way it fails.</b> An unimplemented rule
    /// that never fires costs a caller nothing. This is the opposite - a rule written to apply only
    /// from V2 is enforced under V1 as well, rejecting data the caller was entitled to send - and a
    /// warning is a thing a build ships with.
    /// </para>
    /// <para>
    /// Deliberately outside VM0011-VM0015 and VM0020, which plan §11 reserves for profile
    /// <i>semantics</i>: a profile argument that is not a profile, a range that admits nothing, a
    /// cyclic chain. Those describe a feature that exists. This one says it does not.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ProfileAttributionNotImplemented = Descriptor(
        "VM0019", "Profile attribution is not implemented",
        "'{0}' declares a profile on '{1}', and profiles are not implemented - profile arguments " +
        "are ignored, so every rule is enforced in every profile including the ones it excludes. " +
        "Remove the profile argument until the feature ships",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RegexMemberUnusable = Descriptor(
        "VM0018", "Referenced regex member is unusable",
        "'{0}.{1}' {2}, so the pattern on '{3}' cannot be emitted", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RecordParameterMissingPropertyTarget = Descriptor(
        "VM0051", "Constraint on a record parameter has no effect",
        "'{0}' is on a record parameter without the property: target, so it lands on the parameter and is never evaluated. Write [property: {0}]",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor CustomValidationAttribute = Descriptor(
        "VM0060", "Custom ValidationAttribute is not compiled",
        "'{0}' on '{1}' derives from ValidationAttribute and carries arbitrary code, which cannot be compiled. It is not enforced; move the rule to a constraint or an IAsyncValidatorFor<T>",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor CrossFieldAttribute = Descriptor(
        "VM0061", "Cross-field DataAnnotations attribute is not compiled",
        "'{0}' on '{1}' compares against another member, which a per-property constraint cannot express. It is not enforced",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor FormatValidatorAttribute = Descriptor(
        "VM0063", "Format DataAnnotations attribute is not compiled",
        "'{0}' on '{1}' is not enforced. Its DataAnnotations implementation is more lenient than most callers expect; declare a [Pattern] whose behaviour is visible in your own source",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor LengthOnUnsupportedMember = Descriptor(
        "VM0064", "Length constraint requires a string or a collection",
        "'{0}' applies to strings and collections; '{1}' is '{2}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RangeBoundsNotParseable = Descriptor(
        "VM0065", "Range bounds do not match the member type",
        "The bounds on '{0}' do not parse as '{1}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ValidatableObjectNotCompiled = Descriptor(
        "VM0067", "IValidatableObject is not compiled",
        "'{0}' implements IValidatableObject; its Validate method is not called by the generated validator",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// Reported before any source is added, so the build fails here rather than on generated code
    /// calling a runtime member that does not exist. Plan §7.5.
    /// </summary>
    public static readonly DiagnosticDescriptor RuntimeContractTooOld = Descriptor(
        "VM0040", "ValidationModules.Runtime is too old",
        "The generated validators require ValidationModules.Runtime contract {0} or later; the referenced runtime is contract {1}. Update the ValidationModules.Runtime package reference.",
        DiagnosticSeverity.Error);

    /// <summary>
    /// A Describe body is a whitelisted DSL, not general C#. The body being runnable makes it look
    /// like ordinary code, which is exactly why the half that cannot be compiled has to break the
    /// build rather than behave differently on the two engines.
    /// </summary>
    public static readonly DiagnosticDescriptor NotARuleDeclaration = Descriptor(
        "VM0070", "Not a rule declaration",
        "Only rule declarations on the builder are allowed in '{0}.Describe'; this statement is not one and is not compiled",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor SelectorNotAPath = Descriptor(
        "VM0071", "Selector is not a property path",
        "A rule selector in '{0}.Describe' must read a property of its parameter, so the error has a field to be pathed against",
        DiagnosticSeverity.Error);

    /// <summary>
    /// A predicate is lifted into a static method by the generator and held as a delegate by the
    /// runtime. A delegate can close over the rules class instance and a static method cannot, so
    /// anything captured would compile on one path and not the other.
    /// </summary>
    public static readonly DiagnosticDescriptor PredicateCapturesState = Descriptor(
        "VM0072", "Predicate captures state",
        "A predicate in '{0}.Describe' may read only its own parameter and static or constant state; this one captures something else and cannot be compiled",
        DiagnosticSeverity.Error);

    /// <summary>
    /// The message used to end "pass field: explicitly", which is the one fix that does not work.
    /// A rule is emitted inside its anchored property's chain so both engines agree on ordering
    /// (§4.2), so <c>field:</c> renames the error and does not detach the rule - passing it leaves
    /// this firing, and the old wording sent the reader round the loop a second time.
    /// </summary>
    public static readonly DiagnosticDescriptor EnsureHasNoField = Descriptor(
        "VM0075", "Ensure has no field",
        "The predicate in '{0}.Describe' reads no property of its parameter, so the rule has no " +
        "property to be anchored to. Rewrite it to read the property it is about; field: renames " +
        "the error but does not anchor the rule, so passing it does not resolve this",
        DiagnosticSeverity.Error);

}
