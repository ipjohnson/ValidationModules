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
        "VM0007", "[ValidateNested] target has no rules",
        "'{0}' declares no constraints and no [GenerateValidator], so [ValidateNested] on '{1}' " +
        "descends into it and validates nothing",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor MinExceedsMax = Descriptor(
        "VM0008", "Lower bound exceeds upper bound",
        "The bounds on '{0}' are inverted, so the constraint can never be satisfied", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor InaccessibleProperty = Descriptor(
        "VM0009", "Constrained property is not readable",
        "'{0}' has no accessible getter, so its constraints cannot be evaluated", DiagnosticSeverity.Error);

    /// <summary>
    /// Info rather than Warning, because it only fires when the project explicitly set
    /// <c>ValidationModules_DataAnnotations=Ignore</c> - the attribute being skipped is the
    /// configuration working, not a problem. The message says <i>who</i> is ignoring it, because
    /// another validation system reading the same attributes may still enforce them.
    /// </summary>
    public static readonly DiagnosticDescriptor DataAnnotationsSkipped = Descriptor(
        "VM0010", "DataAnnotations constraint is ignored by ValidationModules",
        "'{0}' on '{1}' is a DataAnnotations constraint, which ValidationModules is ignoring because ValidationModules_DataAnnotations is set to Ignore; another validation system may still enforce it",
        DiagnosticSeverity.Info);

    public static readonly DiagnosticDescriptor CompiledRegexRequested = Descriptor(
        "VM0016", "RegexOptions.Compiled is not meaningful here",
        "Patterns compile through [GeneratedRegex]; RegexOptions.Compiled on '{0}' is ignored",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// VM0021-VM0026 are a fresh block rather than the gaps at VM0005 and VM0011.
    /// </summary>
    /// <remarks>
    /// Both gaps are spoken for. API-SURFACE §11 assigns VM0005 to "[Pattern] on a non-string",
    /// whose meaning was folded into VM0001 at implementation time, and VM0011-VM0015 to profile
    /// semantics. Reclaiming a retired id is worse than leaving it retired: an .editorconfig line
    /// written against the old meaning would go on suppressing, silently, something else entirely.
    /// </remarks>
    public static readonly DiagnosticDescriptor MultipleOfOnUnsupportedType = Descriptor(
        "VM0021", "[MultipleOf] requires a numeric type",
        "[MultipleOf] applies to integral, decimal and floating-point types; '{0}' is '{1}'",
        DiagnosticSeverity.Error);

    /// <summary>
    /// A zero divisor is the reason this is an error rather than a warning that drops the rule.
    /// <c>value % 0</c> is CS0020 for an integral member and a DivideByZeroException for a decimal
    /// one, so leaving it to the emitter puts the failure inside generated code - plan §7.5.
    /// </summary>
    public static readonly DiagnosticDescriptor MultipleOfDivisorNotPositive = Descriptor(
        "VM0022", "[MultipleOf] divisor must be positive",
        "The divisor on '{0}' is '{1}'; it must be greater than zero", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor MultipleOfDivisorNotParseable = Descriptor(
        "VM0023", "[MultipleOf] divisor does not match the member type",
        "The divisor on '{0}' does not parse as '{1}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor UniqueItemsOnNonCollection = Descriptor(
        "VM0024", "[UniqueItems] requires a collection",
        "[UniqueItems] applies to collections; '{0}' is '{1}'", DiagnosticSeverity.Error);

    /// <summary>
    /// The quiet half of <c>[UniqueItems]</c>. The check runs through
    /// <c>EqualityComparer&lt;T&gt;.Default</c>, so a class that does not override
    /// <c>Equals</c> is compared by reference and two elements with identical contents are
    /// "unique" - a rule that passes for the wrong reason rather than one that fails.
    /// </summary>
    public static readonly DiagnosticDescriptor UniqueItemsComparesByReference = Descriptor(
        "VM0025", "[UniqueItems] will compare by reference",
        "'{1}' does not override Equals, so [UniqueItems] on '{0}' compares elements by reference " +
        "and two elements with equal contents both pass. Make it a record, override Equals, or " +
        "implement IEquatable<{1}>",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor RangeHasNoBounds = Descriptor(
        "VM0026", "[Range] declares no bounds",
        "[Range] on '{0}' sets neither Min nor Max, so it can never fail", DiagnosticSeverity.Warning);

    /// <summary>
    /// Continues the VM0021 block. VM0005 and VM0011-VM0015 stay retired for the reason given
    /// above: an .editorconfig line written against a withdrawn meaning would go on suppressing
    /// something else entirely.
    /// </summary>
    public static readonly DiagnosticDescriptor EnumDefinedOnNonEnum = Descriptor(
        "VM0027", "[EnumDefined] requires an enum type",
        "[EnumDefined] applies to enum types; '{0}' is '{1}'",
        DiagnosticSeverity.Error);

    /// <summary>
    /// Conditional rules. VM0028 and VM0029 continue the constraint-declaration block from VM0027;
    /// VM0033 and VM0034 sit just past the polymorphism ids that follow. See
    /// docs/design/CONDITIONS-AND-POLYMORPHISM.md for why the two features interleave here.
    /// </summary>
    public static readonly DiagnosticDescriptor ConditionMemberNotFound = Descriptor(
        "VM0028", "Condition member not found",
        "'{0}' names '{1}', which '{2}' does not declare. A condition names a member of the type " +
        "being validated",
        DiagnosticSeverity.Error);

    /// <summary>
    /// The three accepted shapes are the three that cannot capture anything, which is what makes
    /// the self-containment rule VM0072 enforces for <c>Ensure</c> predicates hold here for free.
    /// </summary>
    public static readonly DiagnosticDescriptor ConditionMemberNotAPredicate = Descriptor(
        "VM0029", "Condition member is not a predicate",
        "'{0}.{1}' cannot be used as a condition. A condition is a bool property, a parameterless " +
        "bool method, or a static bool method taking a single '{0}' parameter",
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor ConditionSetBothWays = Descriptor(
        "VM0033", "Constraint sets both When and Unless",
        "'{0}' on '{1}' sets both When and Unless, which is ambiguous. Write two constraints, or " +
        "one negated condition",
        DiagnosticSeverity.Error);

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
        "VM0031", "[ValidateNested] target is not sealed and declares no polymorphism mode",
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
        "VM0032", "Polymorphism.Runtime on a type that can have no subtypes",
        "'{0}' is {1}, so its runtime type can never differ from its declared type and dispatching " +
        "on it costs a container lookup for the same answer. Use Polymorphism.DeclaredOnly",
        DiagnosticSeverity.Error);

    /// <summary>
    /// Constraints on a base type's properties are collected into the derived type's validator, so
    /// a derived declaration of the same property name silently takes over every constraint on that
    /// field.
    /// </summary>
    /// <remarks>
    /// VM0028 and VM0029 belong to conditional rules, which took the two ids adjacent to VM0027 for
    /// the constraint-declaration block. This continues that block from VM0030 rather than
    /// interleaving; see docs/design/CONDITIONS-AND-POLYMORPHISM.md.
    /// </remarks>
    public static readonly DiagnosticDescriptor HiddenBaseConstraints = Descriptor(
        "VM0030", "Hidden property drops the base declaration's constraints",
        "'{0}' hides '{1}.{0}', so the {2} constraint(s) declared there no longer apply. The " +
        "most-derived declaration of a property supplies all of its constraints, never some of " +
        "them - restate what is still wanted, or rename one of the two",
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

    // VM0019 held the guard on the profile declaration surface: FromProfile, UntilProfile and
    // Profiles shipped before the feature behind them, so setting one had to be an error rather
    // than a silent no-op. Those properties were withdrawn for 1.0.0, so writing one is now
    // CS0117 from the compiler and there is nothing left for this to say.
    //
    // VM0011-VM0015 and VM0020 stay reserved for profile *semantics* - a profile argument that is
    // not a profile, a range that admits nothing, a cyclic chain. VM0019 is not reserved: it
    // described the feature's absence, and the feature returning is what retires the id for good.

    public static readonly DiagnosticDescriptor RegexMemberUnusable = Descriptor(
        "VM0018", "Referenced regex member is unusable",
        "'{0}.{1}' {2}, so the pattern on '{3}' cannot be emitted", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RecordParameterMissingPropertyTarget = Descriptor(
        "VM0051", "Constraint on a record parameter has no effect",
        "'{0}' is on a record parameter without the property: target, so it lands on the parameter and is never evaluated. Write [property: {0}]",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// One descriptor with distinct closing sentences rather than several descriptors: the
    /// catalogue keys an id to one declaration, and an .editorconfig override addresses the id.
    /// The tail carries what actually happened - invoked, or ignored under
    /// <c>ValidationModules_DataAnnotations=Ignore</c> - and the rare attribute whose arguments
    /// cannot be rendered reports the same id back at Warning with the not-enforced tail.
    /// </summary>
    /// <remarks>
    /// Info at the default, matching VM0063's reasoning inverted: the attribute <i>is</i> enforced,
    /// by constructing it once and invoking it - the only faithful reading of user code - so there
    /// is nothing to fix, only the cost model worth knowing.
    /// </remarks>
    public static readonly DiagnosticDescriptor CustomValidationAttribute = Descriptor(
        "VM0060", "Custom ValidationAttribute is invoked, not compiled",
        "'{0}' on '{1}' derives from ValidationAttribute, so its check is user code. {2}",
        DiagnosticSeverity.Info);

    /// <summary>VM0060's tail when the attribute compiles to an invocation.</summary>
    public const string CustomValidationInvokeTail =
        "It is constructed once and invoked with DataAnnotations semantics, so this property pays " +
        "DataAnnotations' costs: a ValidationContext per check, and a box if the value is a value " +
        "type";

    /// <summary>VM0060's tail when the attribute's arguments cannot be rendered.</summary>
    public const string CustomValidationEnforceTail =
        "It is not enforced; move the rule to a constraint or an IAsyncValidatorFor<T>";

    /// <summary>VM0060's tail under <c>ValidationModules_DataAnnotations=Ignore</c>.</summary>
    public const string CustomValidationIgnoreTail =
        "ValidationModules is ignoring it because ValidationModules_DataAnnotations is set to Ignore; another validation system may still enforce it";

    public static readonly DiagnosticDescriptor CrossFieldAttribute = Descriptor(
        "VM0061", "Cross-field DataAnnotations attribute is not compiled",
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
        "VM0063", "Format DataAnnotations attribute is compiled with its BCL semantics",
        "'{0}' on '{1}' compiles to the DataAnnotations check: {2}. Declare a [Pattern] instead if you want a stricter rule",
        DiagnosticSeverity.Info);

    public static readonly DiagnosticDescriptor LengthOnUnsupportedMember = Descriptor(
        "VM0064", "Length constraint requires a string or a collection",
        "'{0}' applies to strings and collections; '{1}' is '{2}'", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor RangeBoundsNotParseable = Descriptor(
        "VM0065", "Range bounds do not match the member type",
        "The bounds on '{0}' do not parse as '{1}'", DiagnosticSeverity.Error);

    /// <summary>
    /// The same multi-tail arrangement as VM0060, for the same reason.
    /// </summary>
    public static readonly DiagnosticDescriptor ValidatableObjectCompiled = Descriptor(
        "VM0067", "IValidatableObject is invoked after every other rule passes",
        "'{0}' implements IValidatableObject; {1}",
        DiagnosticSeverity.Info);

    /// <summary>VM0067's tail when the DataAnnotations front end is on.</summary>
    public const string ValidatableObjectEnforceTail =
        "the generated validator calls its Validate method after every other rule on the type has " +
        "passed, exactly as Validator.TryValidateObject sequences it, and the type keeps no " +
        "boolean fast path";

    /// <summary>VM0067's tail under <c>ValidationModules_DataAnnotations=Ignore</c>.</summary>
    public const string ValidatableObjectIgnoreTail =
        "ValidationModules is ignoring its Validate method because ValidationModules_DataAnnotations is set to Ignore; another validation system may still call it";

    /// <summary>
    /// <c>[CustomValidation]</c> whose target cannot be called: the type or method does not
    /// resolve, or the signature is not one DataAnnotations would accept from here.
    /// </summary>
    /// <remarks>
    /// An error rather than a silently dropped rule, and reported with the reason in the tail -
    /// the same arrangement VM0018 gives an unusable regex member. One deliberate narrowing from
    /// DataAnnotations is caught here at build time instead of at run time: a value parameter
    /// that matches neither the member's type nor <c>object</c> relies on
    /// <c>[CustomValidation]</c>'s runtime string conversion, which this library does not do.
    /// </remarks>
    public static readonly DiagnosticDescriptor CustomValidationTargetUnusable = Descriptor(
        "VM0080", "[CustomValidation] target is unusable",
        "'{0}' on '{1}' cannot be compiled: {2}",
        DiagnosticSeverity.Error);

    /// <summary>
    /// A custom attribute configures resource-based error messages, whose lookup reflects at run
    /// time - the one part of an invoked attribute the trimmer can break.
    /// </summary>
    public static readonly DiagnosticDescriptor ResourceErrorMessageUnderTrimming = Descriptor(
        "VM0081", "Resource-based ErrorMessage resolves reflectively",
        "'{0}' on '{1}' sets ErrorMessageResourceType, which DataAnnotations resolves with " +
        "reflection when the message is formatted. Under trimming or Native AOT the resource " +
        "property may be removed; set ErrorMessage, or keep the resource type rooted",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// A <c>CustomConstraintAttribute</c> subclass whose <c>IsValid</c> cannot be compiled: the
    /// method is missing or the wrong shape, its parameters do not line up with the constructor,
    /// or the declaration sets a property the static check has no way to receive.
    /// </summary>
    /// <remarks>
    /// An error with the reason in the tail, the VM0080 arrangement: the whole point of the native
    /// custom shape is that a mistake in it is a build error naming the fix, where the invoked
    /// DataAnnotations shape can only discover one at run time.
    /// </remarks>
    public static readonly DiagnosticDescriptor CustomConstraintUnusable = Descriptor(
        "VM0082", "Custom constraint attribute is unusable",
        "'{0}' on '{1}' cannot be compiled: {2}",
        DiagnosticSeverity.Error);

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
        "VM0079", "A generic type cannot have a generated validator",
        "'{0}' is generic, and a validator for it could not be registered - the service type has its parameter nested inside a construction, which no container can resolve without MakeGenericType. Declare the constraints on a closed type instead ('{0}<Order>' written out as its own type), or validate the payload's own type and leave the envelope unconstrained",
        DiagnosticSeverity.Error);

    /// <summary>
    /// The message used to end "pass field: explicitly", which is the one fix that does not work.
    /// A rule is emitted inside its anchored property's chain so both engines agree on ordering
    /// (§4.2), so <c>field:</c> renames the error and does not detach the rule - passing it leaves
    /// this firing, and the old wording sent the reader round the loop a second time.
    /// </summary>
    /// <summary>
    /// A condition that folds to a constant is either noise or a rule that can never fire.
    /// </summary>
    /// <remarks>
    /// The one analysis here that no runtime library can offer: a described engine sees a delegate
    /// and cannot know what it returns without calling it, where the generator has the expression.
    /// </remarks>
    public static readonly DiagnosticDescriptor ConstantCondition = Descriptor(
        "VM0034", "Condition is constant",
        "This condition always evaluates to {0}, so {1}",
        DiagnosticSeverity.Warning);

    /// <summary>
    /// A lifted predicate cannot reach a <c>private</c> member of the rules class.
    /// </summary>
    /// <remarks>
    /// Lifting is what lets a predicate keep its declaring file's using directives, and the cost is
    /// that the method ends up in a different class. A non-private member is reached by qualifying
    /// it; a private one cannot be reached at all, and a compile-time constant is the only thing
    /// that can be carried across by value. Reported here rather than left to surface as CS0122
    /// inside generated code.
    /// </remarks>
    public static readonly DiagnosticDescriptor PredicateReferencesPrivateMember = Descriptor(
        "VM0078", "Predicate references a private member of the rules class",
        "'{0}' is private, and this predicate is compiled into a separate class that cannot reach " +
        "it. Make it internal, or declare it as a const",
        DiagnosticSeverity.Error);

    /// <summary>
    /// Continues the rules-DSL block that ends at VM0075.
    /// </summary>
    public static readonly DiagnosticDescriptor EmptyConditionalBlock = Descriptor(
        "VM0076", "Conditional block declares no rules",
        "The {0} block in '{1}' declares no rules, so the condition guards nothing. Almost always " +
        "a rule that was moved out and left the block behind",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor ConditionAppliesToNoRules = Descriptor(
        "VM0077", "Condition applies to no rules",
        "This {0} terminates a statement that declared no constraints, so it conditions nothing",
        DiagnosticSeverity.Warning);

    public static readonly DiagnosticDescriptor EnsureHasNoField = Descriptor(
        "VM0075", "Ensure has no field",
        "The predicate in '{0}.Describe' reads no property of its parameter, so the rule has no " +
        "property to be anchored to. Rewrite it to read the property it is about; field: renames " +
        "the error but does not anchor the rule, so passing it does not resolve this",
        DiagnosticSeverity.Error);

}
