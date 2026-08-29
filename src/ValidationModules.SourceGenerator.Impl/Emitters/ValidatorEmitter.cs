using System.Linq;
using System.Text.RegularExpressions;
using CSharpAuthor;
using ValidationModules.SourceGenerator.Impl.Models;
using static CSharpAuthor.SyntaxHelpers;
using static ValidationModules.SourceGenerator.Impl.Emitters.EmitterOutput;

namespace ValidationModules.SourceGenerator.Impl.Emitters;

/// <summary>
/// Turns a <see cref="ValidatedTypeModel"/> into a validator: one class, one method, straight-line
/// code, no rule graph to walk and nothing built per call.
/// </summary>
/// <remarks>
/// <para>
/// Every front-end feeds this, so a rule's origin cannot change the code that comes out.
/// </para>
/// <para>
/// Authored with CSharpAuthor - plan §2. Declarations go through the type model, so every type the
/// file mentions is written <c>global::</c>-qualified and the file carries no using directives at
/// all: nothing a consumer declares - their own <c>ValidationFlow</c>, their own <c>Regex</c> - can
/// capture a name in generated code. The tests and reports stay composed as expression strings,
/// because they are built from IR data; every type name inside one is spelled fully qualified for
/// the same reason.
/// </para>
/// <para>
/// Messages are composed by the runtime through the <c>ctx.Report*</c> helpers rather than emitted as
/// literals here. That is worth 107 of the 313 native bytes a constraint site would otherwise cost,
/// because every message embeds its field name and so nothing deduplicates in the string heap. A
/// constraint carrying an explicit message is the exception and emits a literal <c>ctx.Report</c>.
/// </para>
/// </remarks>
public sealed class ValidatorEmitter {

    /// <summary>The one enum both engine paths return, spelled the way generated code says it.</summary>
    private const string Flow = "global::ValidationModules.ValidationFlow";

    private const string Codes = "global::ValidationModules.ValidationCodes";

    /// <summary>The bridge that runs custom DataAnnotations surfaces. See DataAnnotationsSupport.</summary>
    private const string DataAnnotations = "global::ValidationModules.DataAnnotationsSupport";

    private const string SeverityEnum = "global::ValidationModules.ValidationSeverity";

    /// <summary>
    /// Where the composed <c>ReportStringLength</c>-style helpers live. They are extension methods,
    /// and the generated file deliberately carries no using directives - a <c>global::</c> name
    /// cannot reach an extension method, so they are called in static form with the context as the
    /// first argument. That also removes the one lookup a consumer could still capture: an
    /// extension of their own, declared in the validated type's namespace, would outrank a
    /// using-imported one without either file naming it.
    /// </summary>
    private const string ContextExtensions = "global::ValidationModules.ValidationContextExtensions";

    /// <summary>
    /// The distinct conditions one method body references, each hoisted into a local evaluated once
    /// before any of them is tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hoisting is not an optimization. A condition may read live static state, so evaluating it
    /// once per pass and once per guarded constraint are observably different answers rather than
    /// two spellings of the same one. Once per pass is what both engines owe.
    /// </para>
    /// <para>
    /// Per method, not per type. <c>IsValid</c> skips Warning and Info constraints, so a condition
    /// only those reference must not be declared in it - an unused local is a warning, and this
    /// solution is warning-free.
    /// </para>
    /// <para>
    /// Keyed by the condition's text, which is what makes two constraints naming one predicate
    /// share a local. The front ends produce different-looking strings - <c>value.IsAuto</c> from
    /// an attribute, a lifted static call from the DSL - and the emitter cannot tell them apart,
    /// which is the point.
    /// </para>
    /// </remarks>
    private sealed class ConditionScope {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly List<(string Name, string Expression)> _declarations = new();

        public string Local(string condition) {
            if (_names.TryGetValue(condition, out var existing)) {
                return existing;
            }

            // A condition can be a conjunction, from nested blocks or a chained .When() written
            // inside one. Each conjunct is hoisted in its own right and the composite is built from
            // those locals, so a doubly-nested rule evaluates its outer condition once rather than
            // once per level. Splitting is safe because a conjunct is always a single call or a
            // negation of one - the front ends join with this separator and never produce it inside
            // an operand.
            var conjuncts = condition.Split(new[] { " && " }, StringSplitOptions.None);
            string expression;

            if (conjuncts.Length == 1) {
                expression = condition;
            } else {
                var parts = new string[conjuncts.Length];

                for (var i = 0; i < conjuncts.Length; i++) {
                    parts[i] = Local(conjuncts[i]);
                }

                expression = string.Join(" && ", parts);
            }

            // Numbered after its conjuncts are resolved, so the declarations read in dependency
            // order and a composite never references a local declared below it.
            var name = $"c{_declarations.Count}";

            _names[condition] = name;
            _declarations.Add((name, expression));

            return name;
        }

        /// <summary>In dependency order, for declaring at the top of the method they belong to.</summary>
        public IReadOnlyList<(string Name, string Expression)> Declarations => _declarations;
    }

    /// <summary>Prefixes a test with its hoisted condition, when it has one.</summary>
    private static string Guarded(ConditionScope scope, string? condition, string test) =>
        condition is null ? test : $"{scope.Local(condition)} && ({test})";

    /// <param name="model">The type to emit a validator for.</param>
    /// <param name="withDynamicAdapter">
    /// Whether to emit the <c>IDynamicValidator</c> adapter beside it. Assembly-wide rather than
    /// per type: the adapters are only useful when something dispatches dynamically, and rooting
    /// them all would charge every consumer for a mode most never use.
    /// </param>
    /// <param name="failFast">
    /// Whether a rule that fails returns rather than falling through to the next one. On by
    /// default; <c>ValidationModules_FailFast</c> turns it off, and a validator emitted without it
    /// evaluates every rule regardless of the collector's
    /// <c>ValidationStopMode</c> - the answer is the same, the work is not.
    /// </param>
    /// <param name="style">
    /// Where the braces go, from the shared <c>GeneratedCodeStyle</c> build property. Purely a
    /// serialization decision - the tree this builds is style-independent, which is what makes the
    /// property safe to flip on a whim.
    /// </param>
    /// <param name="fieldNamer">
    /// The <c>ValidationModules_FieldNaming</c> value the literals were baked with. The
    /// DataAnnotations bridge receives the same policy's runtime instance, so a member name a
    /// custom rule reports at run time lands on the path a compiled constraint would have used.
    /// </param>
    public string Emit(
        ValidatedTypeModel model,
        bool withDynamicAdapter = false,
        bool failFast = true,
        NestingGraph? nesting = null,
        BraceStyle style = BraceStyle.Allman,
        string? fieldNamer = null) {

        var graph = nesting ?? NestingGraph.Empty;
        var patterns = new List<(string Field, ConstraintModel Constraint)>();

        // [FileExtensions] sets, hoisted into static fields the way patterns are: the set is a
        // compile-time constant and the check walks it, so building the array per call would
        // allocate on every pass over the property.
        var extensionSets = new List<(string Field, ConstraintModel Constraint)>();

        // Custom ValidationAttribute instances, also hoisted: constructed once from their
        // compile-time-constant arguments, never per pass - the "rule graphs are built once" rule
        // applied to an attribute that is itself the rule.
        var customAttributes = new List<(string Field, ConstraintModel Constraint)>();

        // IConstraintFor<T> instances, hoisted for the same reason - except the ones marked
        // [PerValidationInstance], which are constructed at the check and never land here.
        var instanceConstraints = new List<(string Field, ConstraintModel Constraint)>();

        // One field per distinct subtype validator this type dispatches to, shared across every
        // property that dispatches to it. Indexed rather than named after the type: two subtypes in
        // different namespaces can share a simple name, and the case arm beside each use already
        // says which type it is.
        var dispatchers = new List<string>();

        var file = GeneratedFile(model.Namespace);

        // internal for an internal model: a public class cannot take a less accessible type as a
        // method parameter (CS0051), and the error would land inside generated code.
        var accessibility = model.IsPublic ? ComponentModifier.Public : ComponentModifier.Internal;

        var validator = file.AddClass(model.ValidatorName);

        validator.Modifiers = accessibility | ComponentModifier.Sealed | ComponentModifier.Partial;
        validator.AddBaseType(ValidatorFor(TypeRef(model.QualifiedTypeName)));

        EmitNestedDependencies(validator, model, graph);

        var body = new StatementBuffer();
        var fast = new StatementBuffer();
        var bodyConditions = new ConditionScope();
        var fastConditions = new ConditionScope();

        foreach (var property in model.Properties) {
            EmitProperty(
                body, fast, property, model, patterns, extensionSets, customAttributes,
                instanceConstraints, bodyConditions, fastConditions, dispatchers, failFast, fieldNamer);
        }

        // Applied rules own no property, so they run once every property has been walked. Ordering
        // them last rather than at their declaration point is §19.7: they are the only rules whose
        // position in the body says nothing about which field they concern.
        foreach (var rule in model.AppliedRules) {
            if (failFast) {
                body.If($"{rule}(ref ctx, value).ShouldStop").Return($"{Flow}.Stop");
            } else {
                body.AddIndentedStatement($"{rule}(ref ctx, value)");
            }
        }

        // IValidatableObject runs last and only when nothing else failed, which is
        // Validator.TryValidateObject's sequencing: object-level validation is the rule the type
        // wrote for "everything else is fine".
        if (model.ImplementsValidatableObject) {
            var call = $"{DataAnnotations}.ValidateObject(ref ctx, value, {NamerInstance(fieldNamer)})";

            if (failFast) {
                body.If($"!ctx.HasErrors && {call}.ShouldStop").Return($"{Flow}.Stop");
            } else {
                body.If("!ctx.HasErrors").AddIndentedStatement(call);
            }
        }

        foreach (var (field, constraint) in patterns) {
            var expression = constraint.Anchored
                ? $@"\A(?:{constraint.Pattern})\z"
                : constraint.Pattern!;

            // A static readonly Regex, not [GeneratedRegex]. Plan §2 mandates the latter and it is
            // unavailable here, for a narrower reason than it first appears. Post-initialization
            // output IS visible to other generators, so a [GeneratedRegex] emitted that way would be
            // implemented; output from RegisterSourceOutput is not, and fails with CS8795. Both
            // verified. Post-initialization cannot help: it runs before anything has been examined
            // and can read neither the compilation nor additional files, while a pattern is always
            // user data. See API-SURFACE.md §13.6.
            //
            // What §2 was actually protecting against is intact. The instance is built once at type
            // initialization rather than per validation call, and RegexOptions.Compiled is never
            // passed, so nothing goes through Reflection.Emit and the result stays AOT-clean. The
            // cost is an interpreted match rather than a source-generated one.
            // The options argument is omitted entirely when there is nothing to say, rather than
            // passed as RegexOptions.None. It is not cosmetic: the single-argument constructor lets
            // ILC prove RegexOptions.Compiled is never set and trim the RegexCompiler path with it,
            // and passing the enum defeats that. Measured at 713 KB on a published AOT binary -
            // more than the regex engine itself costs.
            // A timeout is the attribute's only ReDoS mitigation, and it needs the three-argument
            // constructor - so it has to pass options too, giving up the trim above. That is the
            // trade the author asked for by setting it, and it is paid only where it was set.
            var arguments = new List<object> { QuoteString(expression) };

            if (constraint.MatchTimeoutMilliseconds > 0) {
                arguments.Add(StaticCast(typeof(RegexOptions), constraint.RegexOptions));
                arguments.Add(Invoke(typeof(TimeSpan), "FromMilliseconds", constraint.MatchTimeoutMilliseconds));
            } else if (constraint.RegexOptions != 0) {
                arguments.Add(StaticCast(typeof(RegexOptions), constraint.RegexOptions));
            }

            var pattern = validator.AddField(TypeDefinition.Get(typeof(Regex)), field);

            pattern.Modifiers = ComponentModifier.Private | ComponentModifier.Static | ComponentModifier.Readonly;
            pattern.InitializeValue = New(TypeDefinition.Get(typeof(Regex)), arguments.ToArray());
        }

        foreach (var (field, constraint) in extensionSets) {
            var set = validator.AddField(TypeDefinition.Get(typeof(string)).MakeArray(), field);

            set.Modifiers = ComponentModifier.Private | ComponentModifier.Static | ComponentModifier.Readonly;
            set.InitializeValue = NewArray(
                TypeDefinition.Get(typeof(string)), constraint.Values.Cast<object>().ToArray());
        }

        foreach (var (field, constraint) in customAttributes) {
            // Held as the base type rather than the concrete attribute: the bridge takes
            // ValidationAttribute, and the construction on the right already names the real class.
            var instance = validator.AddField(
                TypeDefinition.Get("System.ComponentModel.DataAnnotations", "ValidationAttribute"), field);

            instance.Modifiers = ComponentModifier.Private | ComponentModifier.Static | ComponentModifier.Readonly;
            instance.InitializeValue = new CodeOutputComponent(constraint.CustomConstruction!) { Indented = false };
        }

        foreach (var (field, constraint) in instanceConstraints) {
            // Held as the concrete attribute class, unlike the bridge fields above: there is no
            // bridge, the calls bind on the class, and a public implicit implementation stays a
            // direct - inlineable - call. The sites the class cannot bind go through a cast the
            // front end already decided on.
            var instance = validator.AddField(TypeRef(constraint.InstanceType!), field);

            instance.Modifiers = ComponentModifier.Private | ComponentModifier.Static | ComponentModifier.Readonly;
            instance.InitializeValue = new CodeOutputComponent(constraint.CustomConstruction!) { Indented = false };
        }

        for (var i = 0; i < dispatchers.Count; i++) {
            // Lazily created: eager construction would allocate on every branch that is never
            // taken, and a validator costs 2.4 ns / 24 B to build. The race on first use is benign -
            // two threads build equivalent validators and one wins - which is the same reasoning
            // the nested-validator arrays already rely on.
            validator.AddField(TypeRef(dispatchers[i]).MakeNullable(), $"_dispatch{i}");
        }

        var validate = validator.AddMethod("Validate");

        validate.SetReturnType(TypeDefinition.Get("ValidationModules", "ValidationFlow"));
        validate.AddParameter(TypeDefinition.Get("ValidationModules", "ValidationContext"), "ctx")
            .Modifier = ParameterModifier.Ref;
        validate.AddParameter(TypeRef(model.QualifiedTypeName), "value");

        foreach (var (name, expression) in bodyConditions.Declarations) {
            validate.Assign(expression).ToVar(name);
        }

        validate.Add(body);
        BlankLine(validate);
        validate.Return($"{Flow}.Continue");

        // An applied rule is handed the context and owns what it records, so there is no condition
        // to test without one. A type carrying any falls back to IValidatorFor<T>.IsValid, which
        // walks properly - correct, just not free.
        // A Runtime descent resolves through the provider on the context, and IsValid has no
        // context - so a type carrying one falls back to IValidatorFor<T>.IsValid, which walks
        // Validate properly. Correct, just not free, and the same trade an applied rule already
        // makes.
        var dispatchesDynamically = model.Properties.Any(p => p.Polymorphism == PolymorphismMode.Runtime);

        // A type that nests itself falls back for a third reason, and a worse one than being slow.
        // The straight-line form calls the nested validator's IsValid directly, and nothing on that
        // path counts depth - the guard lives on the collector, which IsValid never builds. So a
        // caller's cyclic data recursed until the stack went, and a StackOverflowException cannot be
        // caught: the process aborted, out of the entry point documented for hot paths, while
        // Validate on the same value threw InvalidOperationException and named the cycle. The
        // interface default walks Validate into a throwaway collector, so it inherits the guard.
        var nestsItself = model.Properties.Any(property => NestsItsOwnType(property, model))
            || graph.ParticipatesInACycle(model);

        // An IValidatableObject type falls back for the applied-rules reason: its object-level
        // rule is gated on the whole pass being clean, which a boolean path with no collector
        // cannot know. The interface default walks Validate into a throwaway collector and keeps
        // the sequencing.
        if (model.AppliedRules.Count == 0 && !dispatchesDynamically && !nestsItself &&
            !model.ImplementsValidatableObject) {
            var isValid = validator.AddMethod("IsValid");

            isValid.Comment =
                "The same tests as Validate, returning at the first failure and building\n" +
                "no path, message or error record - a caller wanting only a boolean pays for nothing else.";
            isValid.SetReturnType(typeof(bool));
            isValid.AddParameter(TypeRef(model.QualifiedTypeName), "value");

            foreach (var (name, expression) in fastConditions.Declarations) {
                isValid.Assign(expression).ToVar(name);
            }

            isValid.Add(fast);
            isValid.Return("true");
        }

        if (withDynamicAdapter) {
            EmitDynamicAdapter(file, model);
        }

        return Render(file, style);
    }

    /// <summary>
    /// The <c>IDynamicValidator</c> adapter for this type: how a Runtime descent reaches it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Emitted for every validated type, including ones with nothing of their own to check. That is
    /// what makes a registry miss unambiguous - it can only mean the declaring assembly never
    /// registered, never that the type had no rules - so the throw can be unconditional and can say
    /// what to do about it.
    /// </para>
    /// <para>
    /// Internal regardless of the validated type's accessibility. Nothing outside the assembly names
    /// it; it is reached as <c>IDynamicValidator</c>, so there is no CS0051 to avoid.
    /// </para>
    /// <para>
    /// Resolves the injected set rather than constructing a validator, so a separately registered
    /// <c>IValidatorFor&lt;T&gt;</c> composes here exactly as it does on a static descent. That is
    /// the difference from CompileTime, which consults no container and so cannot.
    /// </para>
    /// <para>
    /// <b>Resolved on first use, not in the constructor</b>, for the same reason the nested
    /// validator arrays are. A self-referential model - a Node whose Child is a Node - has a
    /// validator that depends on <c>IEnumerable&lt;IValidatorFor&lt;Node&gt;&gt;</c>, which is the
    /// service it is itself registered under. Taking that in an adapter constructor makes the cycle
    /// eager, and building the registry resolves every adapter at once, so one self-referential type
    /// anywhere in the assembly would fail the whole container. Demand-driven resolution terminates
    /// because nothing is built until a value actually descends.
    /// </para>
    /// </remarks>
    private static void EmitDynamicAdapter(CSharpFileDefinition file, ValidatedTypeModel model) {
        var validated = TypeRef(model.QualifiedTypeName);
        var validators = ValidatorFor(validated).MakeArray();

        var adapter = file.AddClass($"{model.TypeName}DynamicValidator");

        adapter.Modifiers = ComponentModifier.Internal | ComponentModifier.Sealed;
        adapter.Comment = $"Reaches {model.ValidatorName} by runtime type.";
        adapter.AddBaseType(TypeDefinition.Get("ValidationModules", "IDynamicValidator"));

        adapter.AddField(typeof(IServiceProvider), "_services").Modifiers =
            ComponentModifier.Private | ComponentModifier.Readonly;
        adapter.AddField(validators.MakeNullable(), "_validators");

        var constructor = adapter.AddConstructor();

        constructor.AddParameter(typeof(IServiceProvider), "services");
        constructor.Assign("services").To("_services");

        var validatedType = adapter.AddProperty(typeof(Type), "ValidatedType");

        validatedType.Set = null;
        validatedType.Get.LambdaSyntax = true;
        validatedType.Get.AddIndentedStatement(TypeOf(validated));

        var resolved = adapter.AddProperty(validators, "Validators");

        resolved.Modifiers = ComponentModifier.Private;
        resolved.AddHeaderComment("The race on first use is benign: two threads build equivalent arrays and one wins.");
        resolved.Set = null;
        resolved.Get.LambdaSyntax = true;

        var resolve = NullCoalesceEqual(
            "_validators",
            Invoke(typeof(System.Linq.Enumerable), "ToArray",
                InvokeGeneric(
                    TypeDefinition.Get("Microsoft.Extensions.DependencyInjection", "ServiceProviderServiceExtensions"),
                    "GetServices",
                    new[] { ValidatorFor(validated) },
                    "_services")));

        resolve.PrintParentheses = false;
        resolved.Get.AddIndentedStatement(resolve);

        var validate = adapter.AddMethod("Validate");

        validate.SetReturnType(TypeDefinition.Get("ValidationModules", "ValidationFlow"));
        validate.AddParameter(TypeDefinition.Get("ValidationModules", "ValidationContext"), "context")
            .Modifier = ParameterModifier.Ref;
        validate.AddParameter(typeof(object), "value");
        validate.Assign(StaticCast(validated, "value")).ToVar("typed");
        validate.Assign("Validators").ToVar("validators");
        BlankLine(validate);

        var walk = validate.For("i", 0, "validators.Length");

        walk.If("validators[i].Validate(ref context, typed).ShouldStop").Return($"{Flow}.Stop");
        BlankLine(validate);
        validate.Return($"{Flow}.Continue");

        var isValid = adapter.AddMethod("IsValid");

        isValid.SetReturnType(typeof(bool));
        isValid.AddParameter(typeof(object), "value");
        isValid.Assign(StaticCast(validated, "value")).ToVar("typed");
        isValid.Assign("Validators").ToVar("validators");
        BlankLine(isValid);

        var check = isValid.For("i", 0, "validators.Length");

        check.If("!validators[i].IsValid(typed)").Return("false");
        BlankLine(isValid);
        isValid.Return("true");
    }

    /// <summary>
    /// The constructor pair, and the fields holding whatever validates each nested property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nested validators are injected, not reached statically.</b> The container owns the graph,
    /// so registering a second <c>IValidatorFor&lt;Address&gt;</c> composes with the generated one
    /// wherever an Address is reached - as a property of a Pet exactly as much as on its own. The
    /// set is resolved once when the singleton is built rather than per descent.
    /// </para>
    /// <para>
    /// <b>Held as arrays rather than as the injected IEnumerable.</b> Enumerating an
    /// interface-typed sequence boxes the enumerator, which would allocate on a clean pass at every
    /// nested property - the one thing the runtime promises it does not do. Materialised once in
    /// the constructor and walked with a for loop.
    /// </para>
    /// <para>
    /// <b>The parameterless constructor fills in lazily, and that is not a micro-optimization.</b>
    /// Building the defaults eagerly would recurse forever on a self-referential model - a Node
    /// whose Child is a Node constructs a NodeValidator whose constructor constructs a
    /// NodeValidator - and a StackOverflowException cannot be caught. Demand-driven initialisation
    /// terminates because nothing is built until a value actually descends, at which point the
    /// depth guard bounds it. The race on first use is benign: two threads build equivalent arrays
    /// and one wins.
    /// </para>
    /// </remarks>
    private static void EmitNestedDependencies(
        ClassDefinition validator, ValidatedTypeModel model, NestingGraph graph) {
        var nested = model.Properties.Where(p => p.ElementValidatorName is not null).ToList();

        if (nested.Count == 0) {
            validator.AddConstructor();
            return;
        }

        foreach (var property in nested) {
            validator.AddField(ValidatorFor(ElementTypeRef(property)).MakeArray().MakeNullable(), Field(property));
        }

        // A property nesting its own type is deliberately not asked for from the container. Asking
        // would make this validator a dependency of itself - MS.DI answers
        // IEnumerable<IValidatorFor<T>> by constructing IValidatorFor<T>, which is this - and
        // reports a circular dependency. ASP.NET Core turns ValidateOnBuild on in Development, so
        // that is not a lazy failure on first use: the application does not start. Category trees,
        // comment threads, BOMs and org charts are all this shape, and nesting.md documents it as
        // supported.
        //
        // Nothing is lost by leaving it out. The field stays null, and the accessor's fallback
        // already resolves `this` for a self-nesting property - which is the same instance the
        // container would have handed back, since generated validators are registered as
        // singletons.
        var injected = nested
            .Where(property => !NestsItsOwnType(property, model) &&
                !graph.DescentReturnsToDeclarer(model, property))
            .ToList();

        if (injected.Count > 0) {
            var resolving = validator.AddConstructor();

            resolving.Comment = "Resolved from the container: the full set for each nested type.";

            foreach (var property in injected) {
                // CSharpAuthor escapes the declared name, so an ordinary Object, Event or Default
                // property lands on @object, @event or @default without help; the body below spells
                // the same escape through Parameter() so the two agree.
                resolving.AddParameter(
                    EnumerableOf(ValidatorFor(ElementTypeRef(property))), Camel(property.PropertyName));
            }

            foreach (var property in injected) {
                // Empty means absent, not "validate nothing". A container that has no
                // IValidatorFor<TNested> registered - the usual cause being a second assembly whose
                // AddXValidators() was never called - injects an empty sequence, and storing that
                // non-null array would leave the ??= fallback below unreachable. The nested value
                // would then be skipped in silence while every other constraint still reported,
                // which reads as validation working. Falling back to the generated validator is what
                // the parameterless constructor already does for the standalone case.
                resolving
                    .Assign(Invoke(typeof(System.Linq.Enumerable), "ToArray", Parameter(property)))
                    .ToVar($"resolved{property.PropertyName}");
                resolving
                    .Assign($"resolved{property.PropertyName}.Length == 0 ? null : resolved{property.PropertyName}")
                    .To(Field(property));
            }
        }

        var standalone = validator.AddConstructor();

        standalone.Comment = "Standalone: nested types fall back to their own generated validators.";

        foreach (var property in nested) {
            // A property that nests its own type resolves to this instance rather than a new one,
            // which is both correct and the cheapest way to terminate the common cycle.
            var fallback = NestsItsOwnType(property, model)
                ? ThisInstance()
                : (IOutputComponent)New(TypeRef(property.ElementValidatorName!));

            var accessor = validator.AddProperty(ValidatorFor(ElementTypeRef(property)).MakeArray(), Accessor(property));

            accessor.Modifiers = ComponentModifier.Private;
            accessor.Set = null;
            accessor.Get.LambdaSyntax = true;

            var fill = NullCoalesceEqual(
                Field(property), NewArray(ValidatorFor(ElementTypeRef(property)), fallback));

            fill.PrintParentheses = false;
            accessor.Get.AddIndentedStatement(fill);
        }
    }

    /// <summary>
    /// Whether this property nests the very type being validated, so that the validator it descends
    /// through is the one declaring it.
    /// </summary>
    private static bool NestsItsOwnType(ValidatedPropertyModel property, ValidatedTypeModel model) =>
        property.ElementValidatorName == $"global::{Qualify(model)}";

    /// <summary>
    /// The type a nested property's validators are for. A collection or dictionary carries its
    /// element type; a plain object's element type is the property's own, which the front end
    /// leaves null because nothing needed it before now.
    /// </summary>
    private static string ElementType(ValidatedPropertyModel property) =>
        property.ElementTypeName ?? property.TypeName;

    private static ITypeDefinition ElementTypeRef(ValidatedPropertyModel property) =>
        TypeRef(ElementType(property));

    private static string Field(ValidatedPropertyModel property) => $"_{Camel(property.PropertyName)}Validators";

    /// <summary>
    /// The constructor parameter carrying this property's nested validators.
    /// </summary>
    /// <remarks>
    /// Escaped, and this is the case that does not need a verbatim identifier anywhere in the
    /// consumer's source to reach: the parameter is the property name camel-cased, so an ordinary
    /// <c>Object</c>, <c>Event</c> or <c>Default</c> property lands on <c>object</c>, <c>event</c>
    /// or <c>default</c>. <see cref="Field"/> and <see cref="Accessor"/> need no escape because
    /// both wrap the name in affixes, and no keyword survives having <c>_</c> or <c>Validators</c>
    /// stuck to it.
    /// </remarks>
    private static string Parameter(ValidatedPropertyModel property) => Escape(Camel(property.PropertyName));

    private static string Accessor(ValidatedPropertyModel property) => $"{property.PropertyName}Validators";

    private static string Camel(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    /// <summary>
    /// An identifier as it has to be written to be parsed back as one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A property declared <c>@object</c> has the CLR name <c>object</c>: the <c>@</c> is C# syntax
    /// meaning "read this as an identifier", not part of the name, so the model does not carry it
    /// and the emitter has to put it back. <b>Only where the name is emitted as code.</b> The same
    /// name also reaches the wire as a string literal, where an <c>@</c> would corrupt every
    /// payload the property appears in, and reaches composite identifiers like
    /// <c>nestedHome</c>, where it would not parse at all. That is why this is applied at the two
    /// use sites rather than folded into the model.
    /// </para>
    /// <para>
    /// Reserved keywords only, which is <see cref="CSharpIdentifier.Escape"/>'s contract too -
    /// <c>value</c>, <c>record</c> and <c>var</c> are contextual, legal identifiers as they stand,
    /// and are left alone. Sharing the helper keeps the names this emitter writes into expression
    /// strings spelled exactly as CSharpAuthor spells the declarations they refer to.
    /// </para>
    /// </remarks>
    private static string Escape(string identifier) => CSharpIdentifier.Escape(identifier);

    private static string Qualify(ValidatedTypeModel model) =>
        model.Namespace.Length == 0 ? model.ValidatorName : $"{model.Namespace}.{model.ValidatorName}";

    private static void EmitProperty(
        StatementBuffer builder,
        StatementBuffer fast,
        ValidatedPropertyModel property,
        ValidatedTypeModel model,
        List<(string, ConstraintModel)> patterns,
        List<(string, ConstraintModel)> extensionSets,
        List<(string, ConstraintModel)> customAttributes,
        List<(string, ConstraintModel)> instanceConstraints,
        ConditionScope conditions,
        ConditionScope fastConditions,
        List<string> dispatchers,
        bool failFast,
        string? fieldNamer) {

        var access = $"value.{Escape(property.PropertyName)}";
        var field = QuoteString(property.FieldName);
        var required = property.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.Required);

        // Every non-Required test is computed before anything is written, because whether the
        // Required check is worth hoisting into a local depends on whether anything ends up
        // chaining off it, and TestFor can decline a constraint outright. A custom DataAnnotations
        // rule is not a test at all but a flow-returning call that reports for itself, so it
        // carries its boolean-path form beside it; for everything else the test serves both paths.
        var others = new List<(ConstraintModel Constraint, string Test, string? BooleanTest)>();

        foreach (var constraint in property.Constraints) {
            if (constraint.Kind == ConstraintKind.Required) {
                continue;
            }

            if (constraint.Kind is ConstraintKind.CustomAttribute or ConstraintKind.CustomValidationMethod
                or ConstraintKind.CustomInstance) {
                var (flow, boolean) = CustomCalls(
                    access, property, constraint, customAttributes, instanceConstraints, fieldNamer);

                others.Add((constraint, flow, boolean));
                continue;
            }

            if (TestFor(access, property, constraint, model, patterns, extensionSets) is { } test) {
                others.Add((constraint, test, null));
            }
        }

        // Holds "this property failed Required", or null when there is no Required to fail. Every
        // other constraint on the property tests this local.
        //
        // What it replaces was an `else if` chain, and the difference is not cosmetic. Chaining
        // constraint N off constraint N-1 means the second failing constraint on a field is never
        // reached, so the generated engine reported one error per field where the runtime engine
        // reported all of them - a silent divergence between the two, on any field carrying two
        // constraints that both fail. §4.2 permits exactly one short-circuit, a failed Required,
        // and this was not it.
        //
        // A local rather than an `else` block because constraints must stay in declaration order:
        // a predicate is never guarded (below) and may sit between two constraints that are, which
        // no single contiguous `else` can express.
        string? missing = null;

        if (required is not null) {
            // A guarded Required suppresses only when it runs. If its condition is false the test
            // is false, nothing is recorded, and nothing on the field is suppressed - which is the
            // correct reading of §4.2 and needs no special case, because the condition is simply
            // part of the test.
            var requiredTest = Guarded(conditions, required.Condition, RequiredTest(access, property, required));
            var guard = requiredTest;

            if (others.Any(o => o.Constraint.Kind != ConstraintKind.Predicate)) {
                missing = $"missing{property.PropertyName}";
                builder.Assign(requiredTest).ToVar(missing);
                guard = missing;
            }

            AddRule(builder, guard, Report(field, required, "ReportRequired", ""), failFast);
            fast.If(Guarded(fastConditions, required.Condition, RequiredTest(access, property, required)))
                .Return("false");
        }

        foreach (var (constraint, test, booleanTest) in others) {
            // A custom rule reports for itself, so its call is the whole rule: guarded like any
            // other constraint - DataAnnotations also skips a property's remaining attributes
            // after Required fails. A DataAnnotations rule is never null-guarded, because the
            // attribute owns its null semantics and most pass null deliberately; an
            // IConstraintFor<T> check is, because its contract says null never arrives - the same
            // guard-and-skip every structural constraint gets.
            if (booleanTest is not null) {
                var guards = new List<string>();

                if (constraint.Condition is { } guard) {
                    guards.Add(conditions.Local(guard));
                }

                if (missing is not null) {
                    guards.Add($"!{missing}");
                }

                if (constraint.Kind == ConstraintKind.CustomInstance &&
                    (property.IsReferenceType || property.IsNullableValueType)) {
                    guards.Add($"{access} is not null");
                }

                if (failFast) {
                    var prefix = guards.Count == 0 ? string.Empty : string.Join(" && ", guards) + " && ";

                    builder.If($"{prefix}{test}.ShouldStop").Return($"{Flow}.Stop");
                } else if (guards.Count == 0) {
                    builder.AddIndentedStatement(test);
                } else {
                    builder.If(string.Join(" && ", guards)).AddIndentedStatement(test);
                }

                fast.If(Guarded(fastConditions, constraint.Condition, booleanTest)).Return("false");
                continue;
            }

            // Guarding on the Required result is an optimization, not the suppression mechanism -
            // the collector drops anything on a field that already failed required (§4.3). It earns
            // its place by not evaluating a length or pattern test against a value known to be null.
            //
            // A predicate is never guarded. It may read fields other than the one it is anchored
            // to, so a Required failure on the anchor says nothing about whether it would fail, and
            // skipping it would make this engine report fewer errors than the runtime one.
            var guarded = missing is not null && constraint.Kind != ConstraintKind.Predicate;
            var conjuncts = new List<string>();

            if (constraint.Condition is { } condition) {
                conjuncts.Add(conditions.Local(condition));
            }

            if (guarded) {
                conjuncts.Add($"!{missing}");
            }

            // The test is parenthesized rather than trusted to bind, once anything precedes it.
            // Most produce a top-level `&&` or a bracketed group, but `!missing && a || b` would
            // parse as `(!missing && a) || b` and silently widen the constraint - the same class of
            // quiet wrong answer as the else-if chain this replaced.
            conjuncts.Add(conjuncts.Count == 0 ? test : $"({test})");

            var reportedField = constraint.Field is { } renamed ? QuoteString(renamed) : field;

            AddRule(builder, string.Join(" && ", conjuncts), ReportFor(reportedField, constraint, property), failFast);

            // No guard on the boolean path: a failed Required has already returned, so anything
            // still running has a value to test.
            //
            // A warning or an info does not make a value invalid, so the boolean path skips it
            // rather than testing it: running the check and ignoring the answer would be the same
            // result at a cost, and returning false on it would be wrong.
            if (constraint.Severity is null) {
                fast.If(Guarded(fastConditions, constraint.Condition, test)).Return("false");
            }
        }

        EmitNested(
            builder, fast, property, access, conditions, fastConditions, dispatchers, model.TypeName, failFast);
    }

    /// <summary>
    /// Writes the call into a nested value: either straight through the injected validators, or a
    /// type switch over the subtypes a <c>CompileTime</c> descent dispatches to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one arm runs. The declared type's validators sit in <c>default</c> rather than after
    /// the switch, because each subtype validator already checks everything it inherits - running
    /// both would report the base's failures twice. That is what makes inherited constraint
    /// collection a prerequisite for this rather than a companion to it.
    /// </para>
    /// <para>
    /// The subtype arms call a field rather than an injected set, which is the documented cost of
    /// <c>CompileTime</c>: no container is consulted, so a separately registered validator for a
    /// subtype does not compose. <c>Runtime</c> is the mode that resolves through the provider.
    /// </para>
    /// </remarks>
    private static void EmitDescent(
        BaseBlockDefinition block,
        ValidatedPropertyModel property,
        string value,
        string context,
        string validators,
        string owner,
        List<string> dispatchers,
        bool boolean,
        bool failFast = true) {

        if (property.Polymorphism == PolymorphismMode.Runtime) {
            // The boolean path is not emitted for a type that dispatches dynamically, so this only
            // ever runs for Validate. Guarded rather than assumed, so that a future caller cannot
            // quietly get a services-less lookup.
            if (!boolean) {
                var dynamicCall =
                    $"global::ValidationModules.DynamicValidation.Validate(" +
                    $"ref {context}, {value}, {QuoteString(property.FieldName)}, {QuoteString(owner)})";

                if (failFast) {
                    block.If($"{dynamicCall}.ShouldStop").Return($"{Flow}.Stop");
                } else {
                    block.AddIndentedStatement(dynamicCall);
                }
            }

            return;
        }

        var subtypes = property.Subtypes;

        if (property.Polymorphism != PolymorphismMode.CompileTime || subtypes.Count == 0) {
            EmitDeclaredCall(block, property, value, context, validators, boolean, failFast);
            return;
        }

        var dispatch = block.Switch(value);

        foreach (var subtype in subtypes) {
            var index = dispatchers.IndexOf(subtype.ValidatorName);

            if (index < 0) {
                dispatchers.Add(subtype.ValidatorName);
                index = dispatchers.Count - 1;
            }

            // The case pattern is IR data - a qualified type plus the binding - so the arm's label
            // stays composed text the same way the tests are.
            var arm = dispatch.AddCase(
                new CodeOutputComponent($"{subtype.QualifiedTypeName} __typed") { Indented = false });

            if (boolean) {
                arm.If($"!(_dispatch{index} ??= new()).IsValid(__typed)").Return("false");
            } else if (failFast) {
                arm.If($"(_dispatch{index} ??= new()).Validate(ref {context}, __typed).ShouldStop")
                    .Return($"{Flow}.Stop");
            } else {
                arm.AddIndentedStatement($"(_dispatch{index} ??= new()).Validate(ref {context}, __typed)");
            }

            arm.Break();
        }

        var declared = dispatch.AddDefault();

        EmitDeclaredCall(declared, property, value, context, validators, boolean, failFast);
        declared.Break();
    }

    /// <summary>The descent as it has always been: every injected validator for the declared type.</summary>
    private static void EmitDeclaredCall(
        BaseBlockDefinition block,
        ValidatedPropertyModel property,
        string value,
        string context,
        string validators,
        bool boolean,
        bool failFast = true) {

        block.Assign(Accessor(property)).ToVar(validators);

        var walk = block.For("vi", 0, $"{validators}.Length");

        if (boolean) {
            walk.If($"!{validators}[vi].IsValid({value})").Return("false");
        } else if (failFast) {
            walk.If($"{validators}[vi].Validate(ref {context}, {value}).ShouldStop").Return($"{Flow}.Stop");
        } else {
            walk.AddIndentedStatement($"{validators}[vi].Validate(ref {context}, {value})");
        }
    }

    private static void EmitNested(
        StatementBuffer builder,
        StatementBuffer fast,
        ValidatedPropertyModel property,
        string access,
        ConditionScope conditions,
        ConditionScope fastConditions,
        List<string> dispatchers,
        string owner,
        bool failFast) {
        if (property.ElementValidatorName is null) {
            return;
        }

        // The guard is a separate concern from what the descent then does, and deliberately so:
        // this is the seam polymorphic dispatch drops its type switch into, inside the same
        // ctx.Push. Conditions decide whether to descend; dispatch decides which validator runs.
        //
        // Written as a wrapper rather than a prefix because the pattern's variable comes last -
        // "c0 && (value.X is { } nested)" is what has to come out, and the binding is still
        // definite inside the true branch.
        string Enter(ConditionScope scope, string test) =>
            property.Condition is null ? test : $"{scope.Local(property.Condition)} && ({test})";

        if (property.Shape == PropertyShape.Dictionary) {
            var entries = $"entries{property.PropertyName}";

            var descend = builder.If(Enter(conditions, $"{access} is {{ }} {entries}"));
            var pairs = descend.ForEach("pair", new CodeOutputComponent(entries) { Indented = false });
            var present = pairs.If("pair.Value is not null");

            present.Assign($"ctx.PushKey({QuoteString(property.FieldName)}, pair.Key?.ToString() ?? \"\")")
                .ToVar("entryCtx");
            EmitDescent(present, property, "pair.Value", "entryCtx", "entryValidators", owner, dispatchers,
                boolean: false, failFast);

            var check = fast.If(Enter(fastConditions, $"{access} is {{ }} {entries}"));
            var checkPairs = check.ForEach("pair", new CodeOutputComponent(entries) { Indented = false });
            var checkPresent = checkPairs.If("pair.Value is not null");

            EmitDescent(checkPresent, property, "pair.Value", string.Empty, "entryValidators", owner, dispatchers,
                boolean: true);
            return;
        }

        if (property.Shape == PropertyShape.Object) {
            var descend = builder.If(Enter(conditions, $"{access} is {{ }} nested{property.PropertyName}"));

            descend.Assign($"ctx.Push({QuoteString(property.FieldName)})").ToVar($"ctx{property.PropertyName}");
            EmitDescent(
                descend, property, $"nested{property.PropertyName}", $"ctx{property.PropertyName}",
                $"validators{property.PropertyName}", owner, dispatchers, boolean: false, failFast);

            var check = fast.If(Enter(fastConditions, $"{access} is {{ }} nested{property.PropertyName}"));

            EmitDescent(
                check, property, $"nested{property.PropertyName}", string.Empty,
                $"validators{property.PropertyName}", owner, dispatchers, boolean: true);
            return;
        }

        var items = $"items{property.PropertyName}";
        var index = $"i{property.PropertyName}";

        var walk = builder.If(Enter(conditions, $"{access} is {{ }} {items}"));

        EmitElementWalk(walk, property, items, index, owner, dispatchers, boolean: false, failFast, element => {
            element.Assign($"ctx.PushIndex({QuoteString(property.FieldName)}, {index})").ToVar("elementCtx");
        });

        var check2 = fast.If(Enter(fastConditions, $"{access} is {{ }} {items}"));

        EmitElementWalk(check2, property, items, index, owner, dispatchers, boolean: true, failFast: true,
            beforeDescent: null);
    }

    /// <summary>
    /// The loop over a collection's elements, shared by both engine paths: by index when the
    /// collection can be indexed, by enumerator with a hand-kept counter when it cannot.
    /// </summary>
    /// <remarks>
    /// A for loop rather than foreach where possible: enumerating an interface-typed collection
    /// boxes the struct enumerator, and a clean pass over a collection property would then allocate.
    /// </remarks>
    private static void EmitElementWalk(
        BaseBlockDefinition block,
        ValidatedPropertyModel property,
        string items,
        string index,
        string owner,
        List<string> dispatchers,
        bool boolean,
        bool failFast,
        Action<BaseBlockDefinition>? beforeDescent) {

        BaseBlockDefinition loop;

        if (property.IsIndexable) {
            loop = block.For(index, 0, $"{items}.{property.CountAccessor}");
            loop.Assign($"{items}[{index}]").ToVar("element");
        } else {
            if (!boolean) {
                block.Assign("0").ToVar(index);
            }

            loop = block.ForEach("element", new CodeOutputComponent(items) { Indented = false });
        }

        var present = loop.If("element is not null");

        beforeDescent?.Invoke(present);
        EmitDescent(
            present, property, "element", boolean ? string.Empty : "elementCtx", "elementValidators",
            owner, dispatchers, boolean, failFast);

        if (!property.IsIndexable && !boolean) {
            loop.AddIndentedStatement($"{index}++");
        }
    }

    private static string RequiredTest(string access, ValidatedPropertyModel property, ConstraintModel constraint) {
        if (property.IsString) {
            return constraint.AllowEmptyStrings
                ? $"{access} is null"
                : $"string.IsNullOrWhiteSpace({access})";
        }

        return property.IsNullableValueType ? $"{access} is null" : $"{access} is null";
    }

    /// <summary>
    /// The two forms of one custom rule: the flow-returning call the Validate body writes, and the
    /// boolean test the fast path writes. Built together because an attribute's pair shares the
    /// hoisted instance field.
    /// </summary>
    private static (string Flow, string Boolean) CustomCalls(
        string access,
        ValidatedPropertyModel property,
        ConstraintModel constraint,
        List<(string, ConstraintModel)> customAttributes,
        List<(string, ConstraintModel)> instanceConstraints,
        string? fieldNamer) {

        var fieldLiteral = QuoteString(property.FieldName);
        var memberLiteral = QuoteString(property.PropertyName);
        var displayLiteral = QuoteString(property.DisplayName ?? property.PropertyName);

        // An IConstraintFor<T> check: the author's instance, its two members bound the cheapest
        // way each can be - on the class when it declares the method publicly, through the
        // interface when the default or an explicit implementation is what answers. The value is
        // unwrapped here; the null guard sits with the caller, where the boolean form carries its
        // own because the fast path has no guard list to join.
        if (constraint.Kind == ConstraintKind.CustomInstance) {
            var unwrapped = property.IsNullableValueType ? $"{access}.Value" : access;

            string instance;

            if (constraint.PerPassInstance) {
                // [PerValidationInstance]: constructed at the check, exactly as asked. VM0084
                // already told the author what that costs.
                instance = constraint.CustomConstruction!;
            } else {
                instance = $"{property.PropertyName}Constraint{instanceConstraints.Count}";
                instanceConstraints.Add((instance, constraint));
            }

            var validateTarget = constraint.ValidateThroughInterface
                ? $"(({constraint.InstanceInterface}){instance})"
                : instance;
            var isValidTarget = constraint.IsValidThroughInterface
                ? $"(({constraint.InstanceInterface}){instance})"
                : instance;

            var nullGuard = property.IsReferenceType || property.IsNullableValueType
                ? $"{access} is not null && "
                : string.Empty;

            return (
                $"{validateTarget}.Validate(ref ctx, {unwrapped}, {fieldLiteral})",
                $"{nullGuard}!{isValidTarget}.IsValid({unwrapped})");
        }

        if (constraint.Kind == ConstraintKind.CustomAttribute) {
            var instance = $"{property.PropertyName}Custom{customAttributes.Count}";

            customAttributes.Add((instance, constraint));

            return (
                $"{DataAnnotations}.Validate(ref ctx, {instance}, value, {access}, " +
                    $"{fieldLiteral}, {memberLiteral}, {displayLiteral})",
                $"!{DataAnnotations}.IsValid({instance}, value, {access}, {memberLiteral}, {displayLiteral})");
        }

        // [CustomValidation]: a direct static call, with a context built only for the overload
        // that asked for one. The boolean form reads the result itself - non-null is
        // DataAnnotations' spelling of failure - and hands its context no services, which is what
        // a boolean pass has.
        var accessor = constraint.CustomAccessor!;

        if (constraint.CustomTakesContext) {
            return (
                $"{DataAnnotations}.Apply(ref ctx, {accessor}({access}, " +
                    $"{DataAnnotations}.CreateContext(ctx.Services, value, {memberLiteral}, {displayLiteral})), " +
                    $"{fieldLiteral}, {NamerInstance(fieldNamer)})",
                $"{accessor}({access}, {DataAnnotations}.CreateContext(null, value, " +
                    $"{memberLiteral}, {displayLiteral})) is not null");
        }

        return (
            $"{DataAnnotations}.Apply(ref ctx, {accessor}({access}), {fieldLiteral}, {NamerInstance(fieldNamer)})",
            $"{accessor}({access}) is not null");
    }

    private static string? TestFor(
        string access,
        ValidatedPropertyModel property,
        ConstraintModel constraint,
        ValidatedTypeModel model,
        List<(string, ConstraintModel)> patterns,
        List<(string, ConstraintModel)> extensionSets) {

        var value = property.IsNullableValueType ? $"{access}.Value" : access;
        var guard = property.IsReferenceType || property.IsNullableValueType ? $"{access} is not null && " : string.Empty;

        switch (constraint.Kind) {
            // No null guard, deliberately. A predicate may read fields other than the one it is
            // anchored to - "x => x.Start < x.End" is pathed at start and reads both - so guarding on
            // the anchor would skip a rule that had nothing to do with it. Null is the author's, the
            // same as it is on the runtime path.
            case ConstraintKind.Predicate:
                return constraint.PredicateAccessor is { } predicate ? $"!{predicate}(value)" : null;

            case ConstraintKind.StringLength: {
                var tests = new List<string>();
                if (constraint.Min is { } min && min != "0") {
                    tests.Add($"{access}.Length < {min}");
                }

                if (constraint.Max is { } max && max != int.MaxValue.ToString()) {
                    tests.Add($"{access}.Length > {max}");
                }

                return tests.Count == 0 ? null : $"{guard}({string.Join(" || ", tests)})";
            }

            case ConstraintKind.ItemCount: {
                var tests = new List<string>();
                if (constraint.Min is { } min && min != "0") {
                    tests.Add($"{access}.{property.CountAccessor} < {min}");
                }

                if (constraint.Max is { } max && max != int.MaxValue.ToString()) {
                    tests.Add($"{access}.{property.CountAccessor} > {max}");
                }

                return tests.Count == 0 ? null : $"{guard}({string.Join(" || ", tests)})";
            }

            // Each bound is optional and an absent one emits nothing, so a spec that set only
            // `minimum` compiles to one comparison rather than two, the second of which could never
            // fail and whose bound the composed message would then quote back at the caller.
            case ConstraintKind.Range: {
                var tests = new List<string>();
                if (constraint.Min is { } min) {
                    tests.Add($"{value} {(constraint.ExclusiveMin ? "<=" : "<")} {min}");
                }

                if (constraint.Max is { } max) {
                    tests.Add($"{value} {(constraint.ExclusiveMax ? ">=" : ">")} {max}");
                }

                return tests.Count == 0 ? null : $"{guard}({string.Join(" || ", tests)})";
            }

            // An integral or decimal member divides exactly, so it keeps the straight-line form the
            // rest of this switch has. A double or float cannot: `0.3 % 0.01` is 0.00999999999999998
            // in binary floating point, so the check goes through the runtime, which converts to
            // decimal first. The divisor already arrives in the right denomination.
            case ConstraintKind.MultipleOf: {
                if (constraint.Divisor is not { } divisor) {
                    return null;
                }

                return constraint.DecimalDomain
                    ? $"{guard}!global::ValidationModules.ConstraintChecks.IsMultipleOf({value}, {divisor})"
                    : $"{guard}({value} % {divisor} != 0)";
            }

            // The only constraint here that is not a comparison. Typed on IEnumerable<T>, so a
            // property with no Count needs no separate path - the fallback the count constraints
            // have does not arise.
            case ConstraintKind.UniqueItems:
                return $"{guard}!global::ValidationModules.ConstraintChecks.AllUnique({access})";

            case ConstraintKind.Pattern: {
                // The reference form resolves to the consumer's own [GeneratedRegex], so nothing is
                // declared here and the regex engine is never rooted.
                if (constraint.RegexAccessor is { } accessor) {
                    return $"{guard}!{accessor}.IsMatch({access})";
                }

                var field = $"{property.PropertyName}Pattern{patterns.Count}";
                patterns.Add((field, constraint));
                return $"{guard}!{field}.IsMatch({access})";
            }

            // The DataAnnotations format checks, each a straight call into the runtime's
            // reproduction of the BCL's own semantics. Null passes, exactly as the attributes
            // read it, which is what the guard already says. [Url] on a System.Uri member picks
            // the Uri overload by ordinary resolution - the test's text is identical.
            case ConstraintKind.Email:
                return $"{guard}!global::ValidationModules.ConstraintChecks.IsEmail({access})";

            case ConstraintKind.Phone:
                return $"{guard}!global::ValidationModules.ConstraintChecks.IsPhone({access})";

            case ConstraintKind.Url:
                return $"{guard}!global::ValidationModules.ConstraintChecks.IsUrl({access})";

            case ConstraintKind.CreditCard:
                return $"{guard}!global::ValidationModules.ConstraintChecks.IsCreditCard({access})";

            case ConstraintKind.Base64:
                return $"{guard}!global::ValidationModules.ConstraintChecks.IsBase64({access})";

            case ConstraintKind.FileExtension: {
                if (constraint.Values.Count == 0) {
                    return null;
                }

                var field = $"{property.PropertyName}Extensions{extensionSets.Count}";
                extensionSets.Add((field, constraint));
                return $"{guard}!global::ValidationModules.ConstraintChecks.HasFileExtension({access}, {field})";
            }

            // A CustomConstraintAttribute's check: the author's own static method, called like a
            // built-in, with the constructor's constants following the member's value. Null-guarded
            // and unwrapped like every structural constraint - a null passes, [Required] is the
            // presence check.
            case ConstraintKind.CustomCheck: {
                if (constraint.CustomAccessor is not { } accessor) {
                    return null;
                }

                var arguments = constraint.Values.Count == 0
                    ? string.Empty
                    : ", " + string.Join(", ", constraint.Values);

                return $"{guard}!{accessor}({value}{arguments})";
            }

            // Membership against the declared members, or - on a [Flags] enum - whether any bit
            // outside them is set. Never Enum.IsDefined: the members were known at build time, and
            // the reflective form would box and search on a path that is otherwise a comparison.
            case ConstraintKind.EnumDefined: {
                if (constraint.FlagsMask is { } mask) {
                    return $"{guard}(({value} & ~{mask}) != 0)";
                }

                if (constraint.Values.Count == 0) {
                    return null;
                }

                return $"{guard}({string.Join(" && ", constraint.Values.Select(v => $"{value} != {v}"))})";
            }

            case ConstraintKind.AllowedValues: {
                if (constraint.Values.Count == 0) {
                    return null;
                }

                var comparisons = string.Join(" && ", constraint.Values.Select(v => $"{value} != {v}"));
                var anyMatch = string.Join(" || ", constraint.Values.Select(v => $"{value} == {v}"));
                return constraint.Negated ? $"{guard}({anyMatch})" : $"{guard}({comparisons})";
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// One rule: its test, its report, and - when the assembly emits fail-fast - the return that
    /// makes a stop actually skip what follows.
    /// </summary>
    /// <remarks>
    /// Without fail-fast the report is a statement whose answer is discarded, which is the shape
    /// this emitted before <c>ValidationFlow</c> existed. The collector still stops recording, so
    /// the two shapes answer alike under <c>StopOnFirstError</c>; only one of them stops working.
    /// </remarks>
    private static void AddRule(StatementBuffer block, string test, string report, bool failFast) {
        if (failFast) {
            block.If(Conjoin(test, report)).Return($"{Flow}.Stop");
        } else {
            block.If(test).AddIndentedStatement(report);
        }
    }

    /// <summary>
    /// Joins a rule's test to its report so the pair reads as one condition, and the report only
    /// runs when the test failed.
    /// </summary>
    /// <remarks>
    /// Everything reaching here is already an <c>&amp;&amp;</c> chain, so the join usually needs no
    /// brackets. A test carrying a top-level <c>||</c> is the exception and is wrapped: appending
    /// <c>&amp;&amp; report</c> to <c>a || b</c> would bind as <c>a || (b &amp;&amp; report)</c> and
    /// silently skip the report for half the failures - the same class of quiet wrong answer the
    /// conjunct bracketing above exists to remove.
    /// </remarks>
    private static string Conjoin(string test, string report) =>
        (HasTopLevelOr(test) ? $"({test})" : test) + $" && {report}.ShouldStop";

    private static bool HasTopLevelOr(string text) {
        var depth = 0;

        for (var i = 0; i < text.Length; i++) {
            switch (text[i]) {
                case '(': depth++; break;
                case ')': depth--; break;
                case '|' when depth == 0 && i + 1 < text.Length && text[i + 1] == '|': return true;
            }
        }

        return false;
    }

    private static string ReportFor(string field, ConstraintModel constraint, ValidatedPropertyModel property) =>
        constraint.Kind switch {
            ConstraintKind.StringLength => Report(field, constraint, "ReportStringLength", Bounds(constraint)),
            ConstraintKind.ItemCount => Report(field, constraint, "ReportItemCount", Bounds(constraint)),
            ConstraintKind.Range => RangeReport(field, constraint),
            ConstraintKind.MultipleOf => Report(field, constraint, "ReportMultipleOf", $", {constraint.Divisor}"),
            ConstraintKind.UniqueItems => Report(field, constraint, "ReportUniqueItems", ""),
            ConstraintKind.Pattern => Report(field, constraint, "ReportPattern", ""),
            ConstraintKind.AllowedValues => Report(field, constraint, "ReportAllowedValues",
                $", {QuoteString(string.Join(", ", Displays(constraint)))}"),
            ConstraintKind.Email => Report(field, constraint, "ReportEmail", ""),
            ConstraintKind.Phone => Report(field, constraint, "ReportPhone", ""),
            ConstraintKind.Url => Report(field, constraint, "ReportUrl", ""),
            ConstraintKind.CreditCard => Report(field, constraint, "ReportCreditCard", ""),
            ConstraintKind.Base64 => Report(field, constraint, "ReportBase64", ""),
            ConstraintKind.FileExtension => Report(field, constraint, "ReportFileExtension",
                $", {QuoteString(string.Join(", ", Displays(constraint)))}"),
            ConstraintKind.CustomCheck => Report(field, constraint, "ReportCustom", ""),
            // A flags value is a combination, so "must be one of" would be wrong about what the
            // type accepts. Says which flags exist instead.
            ConstraintKind.EnumDefined when constraint.FlagsMask is not null =>
                $"ctx.Report({field}, " +
                $"{(constraint.Code is { } flagsCode ? QuoteString(flagsCode) : $"{Codes}.Enum")}, " +
                $"{QuoteString(constraint.Message ?? $"{Unquote(field)} must be a combination of: {string.Join(", ", Displays(constraint))}.")})",
            ConstraintKind.EnumDefined => Report(field, constraint, "ReportAllowedValues",
                $", {QuoteString(string.Join(", ", Displays(constraint)))}"),

            // Always the literal branch: a predicate's message was rendered from its own source when
            // the front-end read it, so there is nothing here to compose and nothing the runtime
            // could compose it from.
            ConstraintKind.Predicate => Report(field, constraint, "Report", ""),
            _ => Report(field, constraint, "ReportRequired", ""),
        };

    /// <summary>
    /// The report call for a range, which has a different message per shape rather than one message
    /// with a bound the author never wrote standing in for the missing side.
    /// </summary>
    private static string RangeReport(string field, ConstraintModel constraint) => constraint switch {
        { Min: { } min, Max: { } max } => Report(field, constraint, "ReportRange", $", {min}, {max}"),
        { Min: { } min } => Report(field, constraint, "ReportRangeAtLeast", $", {min}"),
        { Max: { } max } => Report(field, constraint, "ReportRangeAtMost", $", {max}"),

        // Unreachable: a range with neither bound is VM0026 and never reaches the emitter.
        _ => Report(field, constraint, "ReportRequired", ""),
    };

    private static string Bounds(ConstraintModel constraint) =>
        $", {constraint.Min ?? "0"}, {constraint.Max ?? int.MaxValue.ToString()}";

    /// <summary>
    /// A constraint carrying an explicit message falls back to the literal overload: at that point
    /// the text is one the author chose rather than one the runtime owns.
    /// </summary>
    /// <remarks>
    /// A <c>Code</c> without a <c>Message</c> takes the helper path and passes the code to it. It
    /// used to be read only inside the message branch, so setting one alone was discarded in
    /// silence and a default code went out on the wire - against a documented contract, and with
    /// nothing to tell the author. Passing it here rather than switching to a literal keeps the
    /// composed default message, which is built from the constraint's own bounds and belongs to the
    /// runtime.
    /// </remarks>
    private static string Report(string field, ConstraintModel constraint, string helper, string arguments) {
        // Omitted entirely at the default rather than passed as ValidationSeverity.Error, so the
        // emitted line stays the one a reader would have written by hand.
        var severity = constraint.Severity is { } member ? $", {SeverityEnum}.{member}" : string.Empty;

        if (constraint.Message is { } message) {
            var code = constraint.Code is { } custom ? QuoteString(custom) : CodeConstant(constraint.Kind);
            return $"ctx.Report({field}, {code}, {QuoteString(message)}{severity})";
        }

        // Named, because severity sits between the bounds and this one and is itself omitted at the
        // default - so there is no positional spelling that works for both.
        var explicitCode = constraint.Code is { } declared ? $", code: {QuoteString(declared)}" : string.Empty;

        return $"{ContextExtensions}.{helper}(ctx, {field}{arguments}{severity}{explicitCode})";
    }

    private static string CodeConstant(ConstraintKind kind) => kind switch {
        ConstraintKind.Required => $"{Codes}.Required",
        ConstraintKind.StringLength => $"{Codes}.StringLength",
        ConstraintKind.Range => $"{Codes}.Range",
        ConstraintKind.Pattern => $"{Codes}.Pattern",
        ConstraintKind.AllowedValues => $"{Codes}.Enum",
        ConstraintKind.EnumDefined => $"{Codes}.Enum",
        ConstraintKind.MultipleOf => $"{Codes}.MultipleOf",
        ConstraintKind.UniqueItems => $"{Codes}.UniqueItems",
        ConstraintKind.Predicate => $"{Codes}.Predicate",
        ConstraintKind.Email => $"{Codes}.Email",
        ConstraintKind.Phone => $"{Codes}.Phone",
        ConstraintKind.Url => $"{Codes}.Url",
        ConstraintKind.CreditCard => $"{Codes}.CreditCard",
        ConstraintKind.Base64 => $"{Codes}.Base64",
        ConstraintKind.FileExtension => $"{Codes}.FileExtension",
        ConstraintKind.CustomCheck => $"{Codes}.Custom",
        _ => $"{Codes}.ArrayBounds",
    };

    private static string Unquote(string literal) =>
        literal.Length >= 2 && literal[0] == '"' ? literal.Substring(1, literal.Length - 2) : literal;

    /// <summary>
    /// The permitted set as a reader should see it: <c>Pro, Enterprise</c> rather than
    /// <c>global::My.Tier.Pro, global::My.Tier.Enterprise</c>.
    /// </summary>
    /// <remarks>
    /// Falls back to unquoting the literals when a front end supplied no displays, which is every
    /// front end but the native one - their values are strings and numbers, where unquoting is the
    /// whole of the transform.
    /// </remarks>
    private static IEnumerable<string> Displays(ConstraintModel constraint) =>
        constraint.ValueDisplays.Count == constraint.Values.Count && constraint.Values.Count > 0
            ? constraint.ValueDisplays
            : constraint.Values.Select(Unquote);
}
