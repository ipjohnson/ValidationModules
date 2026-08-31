using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// The VM#### descriptors. Every one of these is a rule the emitter would otherwise have to guess
/// at, so they are roughly half the work of the generator rather than a finishing touch.
/// </summary>
/// <remarks>
/// <para>
/// <b>The thousand digit is the front end that raises the diagnostic; the hundred digit is the
/// category within it.</b>
/// </para>
/// <list type="bullet">
/// <item><description>VM1xxx - constraint declarations, from <c>AttributeFrontEnd</c>.
/// VM10xx a constraint on a member that cannot carry it, VM11xx arguments that do not resolve,
/// VM12xx checks that cannot fail or would mislead, VM13xx patterns under the AOT policy,
/// VM14xx When/Unless conditions, VM15xx nesting and descent, VM16xx custom constraint
/// shapes.</description></item>
/// <item><description>VM2xxx - the DataAnnotations bridge, keyed on the vocabulary rather than
/// the file: four of these report from <c>AttributeFrontEnd</c>, which hosts the attribute loop,
/// but each can only fire when a DataAnnotations attribute is present.</description></item>
/// <item><description>VM3xxx - rules classes, from <c>RulesFrontEnd</c>. VM30xx what the reader
/// cannot follow, VM31xx rule semantics.</description></item>
/// <item><description>VM4xxx - language packs, from <c>LanguagePackReader</c>.</description></item>
/// <item><description>VM5xxx - the toolchain: the runtime contract check, the emit backstop, and
/// the <c>.Validate&lt;T&gt;()</c> analyzer.</description></item>
/// </list>
/// <para>
/// Banding follows the raiser rather than the theme because a diagnostic rarely changes which front
/// end raises it. <c>RangeBoundsNotParseable</c> is the one deliberate exception in the other
/// direction: it stays VM1xxx because <c>ResolveRangeBounds</c> runs for both vocabularies.
/// </para>
/// <para>
/// <b>The rule for the next id.</b> Append within the category. A new category takes the next free
/// hundred, a new raiser the next free thousand. Never reclaim a retired id: an <c>.editorconfig</c>
/// line written against a withdrawn meaning would go on suppressing something else entirely.
/// </para>
/// <para>
/// These ids replaced the flat VM0### range before 1.0.0, while nothing had shipped stable. The two
/// ranges do not overlap, so a stale <c>dotnet_diagnostic</c> line in a consumer's
/// <c>.editorconfig</c> is inert rather than misdirected. <c>reference/diagnostics.md</c> carries
/// the old-to-new table.
/// </para>
/// </remarks>
public static class ValidationDiagnostics {
    private const string Usage = "ValidationModules.Usage";

    private static DiagnosticDescriptor Descriptor(string id, string title, string message, DiagnosticSeverity severity) =>
        new(id, title, message, Usage, severity, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor StringConstraintOnNonString = Descriptor(
        "VM1001", "Constraint requires a string",
        "'{0}' applies to strings; '{1}' is '{2}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ItemCountOnNonCollection = Descriptor(
        "VM1002", "[ItemCount] requires a collection",
        "[ItemCount] applies to collections; '{0}' is '{1}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RangeOnUnorderedType = Descriptor(
        "VM1003", "[Range] requires an ordered type",
        "[Range] applies to numeric and date types; '{0}' is '{1}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor MultipleOfOnUnsupportedType = Descriptor(
        "VM1004", "[MultipleOf] requires a numeric type",
        "[MultipleOf] applies to integral, decimal and floating-point types; '{0}' is '{1}'",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UniqueItemsOnNonCollection = Descriptor(
        "VM1005", "[UniqueItems] requires a collection",
        "[UniqueItems] applies to collections; '{0}' is '{1}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor EnumDefinedOnNonEnum = Descriptor(
        "VM1006", "[EnumDefined] requires an enum type",
        "[EnumDefined] applies to enum types; '{0}' is '{1}'",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InaccessibleProperty = Descriptor(
        "VM1007", "Constrained property is not readable",
        "'{0}' has no accessible getter, so its constraints cannot be evaluated", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RecordParameterMissingPropertyTarget = Descriptor(
        "VM1008", "Constraint on a record parameter has no effect",
        "'{0}' is on a record parameter without the property: target, so it lands on the parameter and is never evaluated. Write [property: {0}]",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// Constraints on a base type's properties are collected into the derived type's validator, so
    /// a derived declaration of the same property name silently takes over every constraint on that
    /// field.
    /// </summary>
    public static readonly DiagnosticDescriptor HiddenBaseConstraints = Descriptor(
        "VM1009", "Hidden property drops the base declaration's constraints",
        "'{0}' hides '{1}.{0}', so the {2} constraint(s) declared there no longer apply. The " +
        "most-derived declaration of a property supplies all of its constraints, never some of " +
        "them - restate what is still wanted, or rename one of the two",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// A generic type cannot have a generated validator.
    /// </summary>
    /// <remarks>
    /// The class itself is emittable - <c>EnvelopeValidator&lt;T&gt; :
    /// IValidatorFor&lt;Envelope&lt;T&gt;&gt;</c> is ordinary C#. Registering it is not: MS.DI's
    /// open-generic support matches <c>Foo&lt;&gt;</c> to <c>Bar&lt;&gt;</c>, and here the type
    /// parameter is nested inside another construction, so there is no open form to register.
    /// Closing it per construction needs <c>MakeGenericType</c>, which plan §2 rules out.
    /// <para>
    /// Emitting it anyway and leaving it out of <c>AddXValidators()</c> was the other option, and
    /// it is worse: resolving <c>IValidatorFor&lt;Envelope&lt;Order&gt;&gt;</c> would find nothing
    /// and the value would go unvalidated in silence, which is the failure this library refuses
    /// everywhere else. So this is an error, and it names the way out.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor GenericTypeCannotBeValidated = Descriptor(
        "VM1010", "A generic type cannot have a generated validator",
        "'{0}' is generic, and a validator for it could not be registered - the service type has its parameter nested inside a construction, which no container can resolve without MakeGenericType. Declare the constraints on a closed type instead ('{0}<Order>' written out as its own type), or validate the payload's own type and leave the envelope unconstrained",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor MinExceedsMax = Descriptor(
        "VM1101", "Lower bound exceeds upper bound",
        "The bounds on '{0}' are inverted, so the constraint can never be satisfied", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RangeHasNoBounds = Descriptor(
        "VM1102", "[Range] declares no bounds",
        "[Range] on '{0}' sets neither Min nor Max, so it can never fail", DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor RangeBoundsNotParseable = Descriptor(
        "VM1103", "Range bounds do not match the member type",
        "The bounds on '{0}' do not parse as '{1}'", DiagnosticSeverity.Error);

    /// <summary>
    /// A zero divisor is the reason this is an error rather than a warning that drops the rule.
    /// <c>value % 0</c> is CS0020 for an integral member and a DivideByZeroException for a decimal
    /// one, so leaving it to the emitter puts the failure inside generated code - plan §7.5.
    /// </summary>
    public static readonly DiagnosticDescriptor MultipleOfDivisorNotPositive = Descriptor(
        "VM1104", "[MultipleOf] divisor must be positive",
        "The divisor on '{0}' is '{1}'; it must be greater than zero", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor MultipleOfDivisorNotParseable = Descriptor(
        "VM1105", "[MultipleOf] divisor does not match the member type",
        "The divisor on '{0}' does not parse as '{1}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InvalidPattern = Descriptor(
        "VM1106", "Pattern is not a valid regular expression",
        "The pattern on '{0}' is not a valid regular expression: {1}", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RegexMemberUnusable = Descriptor(
        "VM1107", "Referenced regex member is unusable",
        "'{0}.{1}' {2}, so the pattern on '{3}' cannot be emitted", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RequiredOnNonNullableValueType = Descriptor(
        "VM1201", "[Required] has no effect",
        "'{0}' is a non-nullable value type, so it is always present and [Required] can never fail",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// The quiet half of <c>[UniqueItems]</c>. The check runs through
    /// <c>EqualityComparer&lt;T&gt;.Default</c>, so a class that does not override
    /// <c>Equals</c> is compared by reference and two elements with identical contents are
    /// "unique" - a rule that passes for the wrong reason rather than one that fails.
    /// </summary>
    public static readonly DiagnosticDescriptor UniqueItemsComparesByReference = Descriptor(
        "VM1202", "[UniqueItems] will compare by reference",
        "'{1}' does not override Equals, so [UniqueItems] on '{0}' compares elements by reference " +
        "and two elements with equal contents both pass. Make it a record, override Equals, or " +
        "implement IEquatable<{1}>",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// Declared with a fixed default severity so release tracking can discover it; the effective
    /// severity is overridden per site from the resolved policy, because the same situation is a
    /// build error for an AOT-facing project and unremarkable for a JIT one.
    /// </summary>
    public static readonly DiagnosticDescriptor InlinePatternUnderAot = Descriptor(
        "VM1301", "Inline pattern roots the regex engine",
        "The pattern on '{0}' is built from a string at run time, which roots the regex parser and " +
        "interpreter - about 450 KB on an AOT-published binary, once, however many patterns follow. " +
        "Declare it as a " +
        "[GeneratedRegex] and point at it: [Pattern(typeof({1}Patterns), nameof({1}Patterns.{0}))]. " +
        "Set ValidationModules_PatternPolicy to Allow to keep the inline form",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor CompiledRegexRequested = Descriptor(
        "VM1302", "RegexOptions.Compiled is not meaningful here",
        "Patterns compile through [GeneratedRegex]; RegexOptions.Compiled on '{0}' is ignored",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor ConditionMemberNotFound = Descriptor(
        "VM1401", "Condition member not found",
        "'{0}' names '{1}', which '{2}' does not declare. A condition names a member of the type " +
        "being validated",
        DiagnosticSeverity.Error);

    /// <summary>
    /// The three accepted shapes are the three that cannot capture anything, which is what makes
    /// the self-containment a static abstract <c>Describe</c> gives <c>Ensure</c> predicates hold
    /// here for free.
    /// </summary>
    public static readonly DiagnosticDescriptor ConditionMemberNotAPredicate = Descriptor(
        "VM1402", "Condition member is not a predicate",
        "'{0}.{1}' cannot be used as a condition. A condition is a bool property, a parameterless " +
        "bool method, or a static bool method taking a single '{0}' parameter",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ConditionSetBothWays = Descriptor(
        "VM1403", "Constraint sets both When and Unless",
        "'{0}' on '{1}' sets both When and Unless, which is ambiguous. Write two constraints, or " +
        "one negated condition",
        DiagnosticSeverity.Error);

    /// <summary>
    /// <c>[ValidateNested]</c> pointing at a type that has no rules, so the descent finds nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure is silent, which is the whole reason to report it: the attribute compiles, the
    /// property is walked, and nothing is ever checked. A model that reads as validated and
    /// validates nothing is the exact shape this library exists to make impossible.
    /// </para>
    /// <para>
    /// <b>Warning rather than error, unlike the neighbouring rules.</b> The result is a rule that
    /// does not run, not one that runs where it should not - nothing is rejected that should have
    /// been accepted. It is also legitimately transitional: adding <c>[ValidateNested]</c> before
    /// the nested type's own constraints is an ordinary order to work in.
    /// </para>
    /// <para>
    /// Only reported for types this compilation declares. A nested type from a referenced assembly
    /// may carry a validator generated over there, which we cannot see and must not second-guess -
    /// a false negative, which is the safe direction.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor NestedTypeHasNoRules = Descriptor(
        "VM1501", "[ValidateNested] target has no rules",
        "'{0}' declares no constraints and no [GenerateValidator], so [ValidateNested] on '{1}' " +
        "validates nothing and the descent is dropped",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// A nested descent whose target could never carry a generated validator: a constructed
    /// generic like <c>List&lt;Section&gt;</c>, an array, a nullable element - anything that is not
    /// a plain declared named type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from VM1501, which is about a plain type that merely has no rules yet. This target
    /// can never have rules: validators are named <c>&lt;Type&gt;Validator</c> over non-generic
    /// declared types, so the descent would call a validator that cannot exist. Before this
    /// diagnostic, the front end passed the constructed generic name through to the emitter, which
    /// threw - and an unhandled generator exception surfaces as a CS8785 warning, so a model-only
    /// class library said "Build succeeded" having generated nothing at all.
    /// </para>
    /// <para>
    /// Warning rather than error for VM1501's reason: the descent is dropped, so nothing runs that
    /// should not, and the message names the remodelling the documentation already recommends.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor NestedTargetCannotHaveValidator = Descriptor(
        "VM1502", "[ValidateNested] target can never have a validator",
        "'{0}' is not a type a validator can be generated for, so [ValidateNested] on '{1}' is " +
        "dropped; model the inner collection as a property of a type that declares its own rules",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// Keyed on whether the target is sealed - a local fact about the type - and never on which
    /// subtypes happen to be visible from this compilation.
    /// </summary>
    /// <remarks>
    /// That distinction is the whole design. A diagnostic keyed on subtype visibility would appear
    /// when the hierarchy sat in one assembly and vanish when a type moved to a package, which is
    /// precisely the layout-dependence polymorphic dispatch is written to avoid.
    /// </remarks>
    public static readonly DiagnosticDescriptor UnsealedNestedTargetHasNoMode = Descriptor(
        "VM1503", "[ValidateNested] target is not sealed and declares no polymorphism mode",
        "'{0}' is not sealed, so a value of a more derived type may reach '{1}'. Say what should " +
        "happen: seal it, or pass Polymorphism.DeclaredOnly to check only the declared type, or " +
        "Polymorphism.CompileTime to dispatch over its subtypes",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// Runtime dispatch on a type that can have no subtypes resolves, at cost, to the validator the
    /// declared type would have used anyway.
    /// </summary>
    /// <remarks>
    /// An error rather than a warning because it also fails at run time without a container, and a
    /// mode that can never differ from DeclaredOnly is never what was meant.
    /// </remarks>
    public static readonly DiagnosticDescriptor RuntimePolymorphismOnClosedType = Descriptor(
        "VM1504", "Polymorphism.Runtime on a type that can have no subtypes",
        "'{0}' is {1}, so its runtime type can never differ from its declared type and dispatching " +
        "on it costs a container lookup for the same answer. Use Polymorphism.DeclaredOnly",
        DiagnosticSeverity.Error);

    /// <summary>
    /// A <c>CustomConstraintAttribute</c> subclass whose <c>IsValid</c> cannot be compiled: the
    /// method is missing or the wrong shape, its parameters do not line up with the constructor,
    /// or the declaration sets a property the static check has no way to receive.
    /// </summary>
    /// <remarks>
    /// An error with the reason in the tail, the VM2008 arrangement: the whole point of the native
    /// custom shape is that a mistake in it is a build error naming the fix, where the invoked
    /// DataAnnotations shape can only discover one at run time.
    /// </remarks>
    public static readonly DiagnosticDescriptor CustomConstraintUnusable = Descriptor(
        "VM1601", "Custom constraint attribute is unusable",
        "'{0}' on '{1}' cannot be compiled: {2}",
        DiagnosticSeverity.Error);

    /// <summary>
    /// An attribute implementing <c>IConstraintFor&lt;T&gt;</c> that cannot be compiled: no
    /// implemented instantiation accepts the member, more than one does, an argument in the
    /// declaration is not a renderable constant, or the class mixes the instance contract with
    /// another custom shape.
    /// </summary>
    /// <remarks>
    /// The VM2008/VM1601 arrangement - an error with the reason in the tail - for the same reason:
    /// a mistake in a native shape is a build error naming the fix, never a rule that silently
    /// stops running.
    /// </remarks>
    public static readonly DiagnosticDescriptor ConstraintInterfaceUnusable = Descriptor(
        "VM1602", "IConstraintFor<T> attribute is unusable",
        "'{0}' on '{1}' cannot be compiled: {2}",
        DiagnosticSeverity.Error);

    /// <summary>
    /// A constraint attribute opted out of the shared instance, so every check constructs one.
    /// </summary>
    /// <remarks>
    /// Info at the site that pays, the VM2002 reasoning: nothing is wrong - the class asked for
    /// isolation and gets it - but this is the one constraint cost a clean pass pays, on a path
    /// that otherwise allocates nothing, so it is stated where it is incurred rather than only on
    /// the class that caused it.
    /// </remarks>
    public static readonly DiagnosticDescriptor PerValidationInstanceCost = Descriptor(
        "VM1603", "Constraint instance is constructed per check",
        "'{0}' is marked [PerValidationInstance], so checking '{1}' constructs a new instance on " +
        "every validation pass, passing values included - the allocation a shared instance would " +
        "not cost",
        DiagnosticSeverity.Info);

    /// <summary>
    /// Info rather than Warning, because it only fires when the project explicitly set
    /// <c>ValidationModules_DataAnnotations=Ignore</c> - the attribute being skipped is the
    /// configuration working, not a problem. The message says <i>who</i> is ignoring it, because
    /// another validation system reading the same attributes may still enforce them.
    /// </summary>
    public static readonly DiagnosticDescriptor DataAnnotationsSkipped = Descriptor(
        "VM2001", "DataAnnotations constraint is ignored by ValidationModules",
        "'{0}' on '{1}' is a DataAnnotations constraint, which ValidationModules is ignoring because ValidationModules_DataAnnotations is set to Ignore; another validation system may still enforce it",
        DiagnosticSeverity.Info);

    /// <summary>
    /// One descriptor with distinct closing sentences rather than several descriptors: the
    /// catalogue keys an id to one declaration, and an .editorconfig override addresses the id.
    /// The tail carries what actually happened - invoked, or ignored under
    /// <c>ValidationModules_DataAnnotations=Ignore</c> - and the rare attribute whose arguments
    /// cannot be rendered reports the same id back at Warning with the not-enforced tail.
    /// </summary>
    /// <remarks>
    /// Info at the default, matching VM2004's reasoning inverted: the attribute <i>is</i> enforced,
    /// by constructing it once and invoking it - the only faithful reading of user code - so there
    /// is nothing to fix, only the cost model worth knowing.
    /// </remarks>
    public static readonly DiagnosticDescriptor CustomValidationAttribute = Descriptor(
        "VM2002", "Custom ValidationAttribute is invoked, not compiled",
        "'{0}' on '{1}' derives from ValidationAttribute, so its check is user code. {2}",
        DiagnosticSeverity.Info);

    /// <summary>VM2002's tail when the attribute compiles to an invocation.</summary>
    public const string CustomValidationInvokeTail =
        "It is constructed once and invoked with DataAnnotations semantics, so this property pays " +
        "DataAnnotations' costs: a ValidationContext per check, and a box if the value is a value " +
        "type";

    /// <summary>VM2002's tail when the attribute's arguments cannot be rendered.</summary>
    public const string CustomValidationEnforceTail =
        "It is not enforced; move the rule to a constraint or an IAsyncValidatorFor<T>";

    /// <summary>VM2002's tail under <c>ValidationModules_DataAnnotations=Ignore</c>.</summary>
    public const string CustomValidationIgnoreTail =
        "ValidationModules is ignoring it because ValidationModules_DataAnnotations is set to Ignore; another validation system may still enforce it";

    public static readonly DiagnosticDescriptor CrossFieldAttribute = Descriptor(
        "VM2003", "Cross-field DataAnnotations attribute is not compiled",
        "'{0}' on '{1}' compares against another member, which a per-property constraint cannot express. It is not enforced",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// The format validators compile with the BCL's exact semantics, and those semantics are
    /// looser than the attribute names suggest - <c>[EmailAddress]</c> accepts <c>a@b</c>, by
    /// design (RFC 5322 permits a dotless domain, and dotnet/runtime#45670 closed every request
    /// to tighten it). This says precisely what check was emitted, at the site that asked for it.
    /// </summary>
    /// <remarks>
    /// Info, not Warning: the attribute is enforced, identically to every other DataAnnotations
    /// consumer, so there is nothing to fix - only something worth knowing. The id previously
    /// carried the Warning that these attributes were <i>not</i> compiled; the subject is the
    /// same six attributes and no stable release shipped the old meaning, so the id stays rather
    /// than joining the retired list.
    /// </remarks>
    public static readonly DiagnosticDescriptor FormatValidatorCompiled = Descriptor(
        "VM2004", "Format DataAnnotations attribute is compiled with its BCL semantics",
        "'{0}' on '{1}' compiles to the DataAnnotations check: {2}. Declare a [Pattern] instead if you want a stricter rule",
        DiagnosticSeverity.Info);

    public static readonly DiagnosticDescriptor LengthOnUnsupportedMember = Descriptor(
        "VM2005", "Length constraint requires a string or a collection",
        "'{0}' applies to strings and collections; '{1}' is '{2}'", DiagnosticSeverity.Error);

    /// <summary>
    /// The same multi-tail arrangement as VM2002, for the same reason.
    /// </summary>
    public static readonly DiagnosticDescriptor ValidatableObjectCompiled = Descriptor(
        "VM2006", "IValidatableObject is invoked after every other rule passes",
        "'{0}' implements IValidatableObject; {1}",
        DiagnosticSeverity.Info);

    /// <summary>VM2006's tail when the DataAnnotations front end is on.</summary>
    public const string ValidatableObjectEnforceTail =
        "the generated validator calls its Validate method after every other rule on the type has " +
        "passed, exactly as Validator.TryValidateObject sequences it, and the type keeps no " +
        "boolean fast path";

    /// <summary>VM2006's tail under <c>ValidationModules_DataAnnotations=Ignore</c>.</summary>
    public const string ValidatableObjectIgnoreTail =
        "ValidationModules is ignoring its Validate method because ValidationModules_DataAnnotations is set to Ignore; another validation system may still call it";

    /// <summary>
    /// <c>[EnumDataType]</c> reached the bridge and was dropped without a word, which is the
    /// anti-silent-drop rule broken in the one place it still could be: every other validating
    /// DataAnnotations attribute either compiles or reports why it cannot. The check itself -
    /// a loosely-typed value parses as an enum member - is a runtime string conversion, the same
    /// family <c>[CustomValidation]</c>'s narrowing refuses.
    /// </summary>
    public static readonly DiagnosticDescriptor EnumDataTypeNotCompiled = Descriptor(
        "VM2007", "[EnumDataType] is not compiled",
        "'{0}' on '{1}' checks that a loosely-typed value parses as an enum, a runtime conversion " +
        "this library does not compile. It is not enforced; type the member as the enum and use " +
        "[EnumDefined]",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// <c>[CustomValidation]</c> whose target cannot be called: the type or method does not
    /// resolve, or the signature is not one DataAnnotations would accept from here.
    /// </summary>
    /// <remarks>
    /// An error rather than a silently dropped rule, and reported with the reason in the tail -
    /// the same arrangement VM1107 gives an unusable regex member. One deliberate narrowing from
    /// DataAnnotations is caught here at build time instead of at run time: a value parameter
    /// that matches neither the member's type nor <c>object</c> relies on
    /// <c>[CustomValidation]</c>'s runtime string conversion, which this library does not do.
    /// </remarks>
    public static readonly DiagnosticDescriptor CustomValidationTargetUnusable = Descriptor(
        "VM2008", "[CustomValidation] target is unusable",
        "'{0}' on '{1}' cannot be compiled: {2}",
        DiagnosticSeverity.Error);

    /// <summary>
    /// A custom attribute configures resource-based error messages, whose lookup reflects at run
    /// time - the one part of an invoked attribute the trimmer can break.
    /// </summary>
    public static readonly DiagnosticDescriptor ResourceErrorMessageUnderTrimming = Descriptor(
        "VM2009", "Resource-based ErrorMessage resolves reflectively",
        "'{0}' on '{1}' sets ErrorMessageResourceType, which DataAnnotations resolves with " +
        "reflection when the message is formatted. Under trimming or Native AOT the resource " +
        "property may be removed; set ErrorMessage, or keep the resource type rooted",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// A Describe body is transcribed, and almost everything transcribes; what remains rejected is
    /// the short blacklist - exotica, mutation of the subject, misplaced islands. Never silently
    /// dropped: a statement the reader cannot carry has to break the build, because the generated
    /// validator otherwise checks less than the body says.
    /// </summary>
    public static readonly DiagnosticDescriptor NotTranscribable = Descriptor(
        "VM3001", "Statement is not transcribable",
        "'{0}.Describe' contains {1}, which the generator does not transcribe",
        DiagnosticSeverity.Error);

    /// <summary>
    /// The anti-silent-drop rule. A rule call the generator cannot see would transcribe into a
    /// call on the inert builder and validate nothing - EF Core learned this lesson with implicit
    /// client-eval and made it an error; so does this.
    /// </summary>
    public static readonly DiagnosticDescriptor RulesFlowNotFollowable = Descriptor(
        "VM3002", "The rules builder flows where the generator cannot follow",
        "The builder declares rules only where the generator can read them; here it would {0}, " +
        "which would validate nothing at runtime",
        DiagnosticSeverity.Error);

    /// <summary>
    /// Islands need generator-computed identity - a field, a rendered message - and a loop or
    /// lambda gives them none. Collections are Each's job; the reporter tier covers the exotic
    /// per-element case with a computed field string.
    /// </summary>
    public static readonly DiagnosticDescriptor IslandInUnreadableScope = Descriptor(
        "VM3003", "Rule declaration inside a loop, lambda, or local function",
        "'{0}.Describe' declares a rule inside a scope the generator cannot expand it in. Use Each " +
        "for collections - a collection of strings chains element rules, " +
        "Each(x.Steps).Length(5, 500) - or report per element through rules.Context",
        DiagnosticSeverity.Error);

    /// <summary>
    /// Transcribed code must compile at the emission site: the companion file is internal to the
    /// same assembly, so what breaks is private and protected members of the rules class. Caught
    /// here rather than surfacing as CS0122 inside generated code, which is the worst place for it.
    /// </summary>
    public static readonly DiagnosticDescriptor MemberNotReachableFromRegion = Descriptor(
        "VM3004", "Member is not reachable from the generated region",
        "'{0}' is not accessible from the companion file '{1}.Describe' is transcribed into. " +
        "Make it internal",
        DiagnosticSeverity.Error);

    /// <summary>
    /// A fragment is expanded from syntax, and a referenced assembly ships IL - the symbol has no
    /// body to read. The same-compilation rule is physics, not policy, and a plain
    /// ProjectReference is on the wrong side of it.
    /// </summary>
    public static readonly DiagnosticDescriptor FragmentIsCompiledIl = Descriptor(
        "VM3005", "Fragment is compiled IL from a referenced assembly",
        "Fragment '{0}' is compiled IL from a referenced assembly; fragments must be part of this " +
        "compilation - use a shared project or a source-only package",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor FragmentCallCycle = Descriptor(
        "VM3006", "Fragment call cycle",
        "Fragments may call fragments, but this chain returns to where it started: {0}",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor SelectorNotAPath = Descriptor(
        "VM3007", "Value argument is not a member path",
        "A rule's value argument in '{0}' must be a member path on the subject parameter, so the error has a field to be pathed against; anything else needs field:",
        DiagnosticSeverity.Error);

    /// <summary>
    /// The selector overload matrix used to make this unwritable; values cannot, because a
    /// non-nullable value type converts to its nullable form implicitly.
    /// </summary>
    public static readonly DiagnosticDescriptor RequireCannotFail = Descriptor(
        "VM3101", "Require on a non-nullable value type has no effect",
        "'{0}' is a non-nullable value type and can never be missing, so this rule can never " +
        "fail. Constrain the value instead, or make the property nullable",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor EnsureHasNoField = Descriptor(
        "VM3102", "Ensure has no field",
        "The condition in '{0}.Describe' reads no property of the subject, so the rule has no " +
        "field to report against. Anchor it by reading the property it is about, or pass field:",
        DiagnosticSeverity.Error);

    /// <summary>
    /// The code an <c>Ensure</c> derived, stated where the rule was written.
    /// </summary>
    /// <remarks>
    /// Info because there is nothing to fix. The diagnostic exists so the key is visible at the
    /// site that owns it - the same reason VM2004 states the compiled DataAnnotations check at its
    /// declaration - and because a derived code is the one part of a rules class you cannot read
    /// off the source. A warning would imply the author erred by not passing <c>code:</c>, and
    /// deriving one is the designed behaviour rather than a fallback. Not reported when the author
    /// passed <c>code:</c>, since then the code is already in the source.
    /// </remarks>
    public static readonly DiagnosticDescriptor EnsureCodeDerived = Descriptor(
        "VM3103", "Ensure derives its code from its condition",
        "This rule reports code '{0}', derived from '{1}'. Pass code: to pin it against a change " +
        "to the condition", DiagnosticSeverity.Info);

    /// <summary>
    /// A rule value written as <c>x.Member.Value</c>. The rule methods take the nullable
    /// directly, so the unwrap is never needed - and before the range methods grew their
    /// non-nullable overloads it also skewed inference into an opaque CS1503. What remains
    /// wrong with it: the derived field path keeps the <c>.Value</c> hop, so the wire path and
    /// the composed message name <c>value</c> rather than the member.
    /// </summary>
    /// <remarks>
    /// Warning rather than error, because the reader also corrects it - the rule compiles against
    /// the member itself, path and all - and failing a build the correction just fixed would be
    /// spite. The diagnostic still fires so the source stops disagreeing with what is generated.
    /// </remarks>
    public static readonly DiagnosticDescriptor NullableValueUnwrapped = Descriptor(
        "VM3104", "Drop .Value; the rule takes the nullable directly",
        "'{0}.Value' unwraps a nullable member. The rule takes the nullable directly, and the " +
        "field path is derived from the member - write '{0}'", DiagnosticSeverity.Warning);

    /// <summary>
    /// A facet declared in this compilation with no rules at all would make <c>As</c> a silent
    /// no-op - the failure this library refuses everywhere else.
    /// </summary>
    public static readonly DiagnosticDescriptor FacetDeclaresNoRules = Descriptor(
        "VM3105", "Facet declares no rules",
        "'{0}' is validated as a facet here, but nothing in this compilation declares rules for " +
        "it, so this would check nothing. Give the facet constraint attributes or a rules class",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor LanguagePackUnreadable = Descriptor(
        "VM4001", "Language pack cannot be read",
        "'{0}' was skipped: {1}", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor LanguagePackUnknownShape = Descriptor(
        "VM4002", "Language pack names an unknown shape key",
        "'{0}' in '{1}' names no known shape; the nearest is '{2}'. The entry was skipped",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor LanguagePackHoleOutOfRange = Descriptor(
        "VM4003", "Template hole exceeds the shape's arguments",
        "'{0}' uses {{{1}}}, but the shape carries {2} argument(s); the entry in '{3}' was skipped",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor LanguagePackDuplicateKey = Descriptor(
        "VM4004", "Language pack repeats a key",
        "'{0}' appears more than once in '{1}'; entries after the first were skipped",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor LanguagePackNameMismatch = Descriptor(
        "VM4005", "Language pack file name and culture disagree",
        "'{0}' is named for '{1}' but declares culture '{2}'; the body wins",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor LanguagePackCoverage = Descriptor(
        "VM4006", "Language pack coverage",
        "'{0}' covers {1} of {2} shapes; missing: {3}", DiagnosticSeverity.Info);

    /// <summary>
    /// Reported before any source is added, so the build fails here rather than on generated code
    /// calling a runtime member that does not exist. Plan §7.5.
    /// </summary>
    public static readonly DiagnosticDescriptor RuntimeContractTooOld = Descriptor(
        "VM5001", "ValidationModules.Runtime is too old",
        "The generated validators require ValidationModules.Runtime contract {0} or later; the referenced runtime is contract {1}. Update the ValidationModules.Runtime package reference.",
        DiagnosticSeverity.Error);

    /// <summary>
    /// The backstop for the class of failure VM1502 fixes one instance of: any unhandled exception
    /// in an emit stage.
    /// </summary>
    /// <remarks>
    /// Roslyn converts a generator exception into a CS8785 <b>warning</b> and drops every source
    /// the stage would have added. In an executable that is a loud CS0246 on the missing
    /// <c>Add…Validators()</c>; in a class library holding only models, nothing references a
    /// generated symbol, so the build succeeds with zero validators and every model silently
    /// validates nothing. Reporting the exception as an error is what makes a generator defect
    /// fail the build instead of shipping as an assembly that validates nothing.
    /// </remarks>
    public static readonly DiagnosticDescriptor GeneratorFailed = Descriptor(
        "VM5002", "The validator generator failed",
        "Emitting {0} threw {1}: {2}. The build is failed so the missing generated source cannot " +
        "ship silently; please report this",
        DiagnosticSeverity.Error);

    /// <summary>
    /// <c>.Validate&lt;T&gt;()</c> naming a type this compilation declares and generates no
    /// validator for.
    /// </summary>
    /// <remarks>
    /// The build-time version of the endpoint filter factory's startup check, reported where the
    /// call was written. Warning rather than error for VM1501's cross-assembly reason: a rules
    /// class in another assembly may target even a local type, so the startup check stays the
    /// authority and this is the earlier, cheaper signal.
    /// </remarks>
    public static readonly DiagnosticDescriptor ValidateTargetHasNoValidator = Descriptor(
        "VM5003", "Validate<T>() names a type with no validator",
        "'{0}' has no constraints, no [GenerateValidator], and no rules class or hand-written " +
        "validator in this compilation, so .Validate<{1}>() will fail when the endpoint is " +
        "built. Add constraints or [GenerateValidator] - or, if its rules arrive from another " +
        "assembly, ignore this and the startup check will agree",
        DiagnosticSeverity.Warning);

}
