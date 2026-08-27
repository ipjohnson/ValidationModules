using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using ValidationModules.SourceGenerator.Impl.Models;

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
/// Messages are composed by the runtime through the <c>ctx.Report*</c> helpers rather than emitted as
/// literals here. That is worth 107 of the 313 native bytes a constraint site would otherwise cost,
/// because every message embeds its field name and so nothing deduplicates in the string heap. A
/// constraint carrying an explicit message is the exception and emits a literal <c>ctx.Report</c>.
/// </para>
/// </remarks>
public sealed class ValidatorEmitter {

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

        public void Declare(StringBuilder builder) {
            foreach (var (name, expression) in _declarations) {
                builder.AppendLine($"        var {name} = {expression};");
            }
        }
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
    public string Emit(ValidatedTypeModel model, bool withDynamicAdapter = false) {
        var builder = new StringBuilder();
        var patterns = new List<(string Field, ConstraintModel Constraint)>();

        // One field per distinct subtype validator this type dispatches to, shared across every
        // property that dispatches to it. Indexed rather than named after the type: two subtypes in
        // different namespaces can share a simple name, and the case arm beside each use already
        // says which type it is.
        var dispatchers = new List<string>();

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("using System.Text.RegularExpressions;");
        builder.AppendLine("using ValidationModules;");
        builder.AppendLine();
        if (model.Namespace.Length > 0) {
            builder.AppendLine($"namespace {model.Namespace};");
            builder.AppendLine();
        }
        // internal for an internal model: a public class cannot take a less accessible type as a
        // method parameter (CS0051), and the error would land inside generated code.
        var accessibility = model.IsPublic ? "public" : "internal";

        builder.AppendLine($"{accessibility} sealed partial class {model.ValidatorName} : IValidatorFor<{model.QualifiedTypeName}> {{");
        builder.AppendLine();
        EmitNestedDependencies(builder, model);

        var body = new StringBuilder();
        var fast = new StringBuilder();
        var bodyConditions = new ConditionScope();
        var fastConditions = new ConditionScope();

        foreach (var property in model.Properties) {
            EmitProperty(body, fast, property, model, patterns, bodyConditions, fastConditions, dispatchers);
        }

        // Applied rules own no property, so they run once every property has been walked. Ordering
        // them last rather than at their declaration point is §19.7: they are the only rules whose
        // position in the body says nothing about which field they concern.
        foreach (var rule in model.AppliedRules) {
            body.AppendLine($"        if ({rule}(ref ctx, value).ShouldStop) return ValidationFlow.Stop;");
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
            var options = constraint.RegexOptions != 0
                ? $", (RegexOptions){constraint.RegexOptions}"
                : string.Empty;

            builder.AppendLine(
                $"    private static readonly Regex {field} = new Regex({Quote(expression)}{options});");
            builder.AppendLine();
        }

        for (var i = 0; i < dispatchers.Count; i++) {
            // Lazily created: eager construction would allocate on every branch that is never
            // taken, and a validator costs 2.4 ns / 24 B to build. The race on first use is benign -
            // two threads build equivalent validators and one wins - which is the same reasoning
            // the nested-validator arrays already rely on.
            builder.AppendLine($"    private {dispatchers[i]}? _dispatch{i};");
            builder.AppendLine();
        }

        builder.AppendLine($"    public ValidationFlow Validate(ref ValidationContext ctx, {model.QualifiedTypeName} value) {{");
        bodyConditions.Declare(builder);
        builder.Append(body);
        builder.AppendLine();
        builder.AppendLine("        return ValidationFlow.Continue;");
        builder.AppendLine("    }");

        // An applied rule is handed the context and owns what it records, so there is no condition
        // to test without one. A type carrying any falls back to IValidatorFor<T>.IsValid, which
        // walks properly - correct, just not free.
        // A Runtime descent resolves through the provider on the context, and IsValid has no
        // context - so a type carrying one falls back to IValidatorFor<T>.IsValid, which walks
        // Validate properly. Correct, just not free, and the same trade an applied rule already
        // makes.
        var dispatchesDynamically = model.Properties.Any(p => p.Polymorphism == PolymorphismMode.Runtime);

        if (model.AppliedRules.Count == 0 && !dispatchesDynamically) {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder.AppendLine("    /// The same tests as <see cref=\"Validate\"/>, returning at the first failure and building");
            builder.AppendLine("    /// no path, message or error record - a caller wanting only a boolean pays for nothing else.");
            builder.AppendLine("    /// </summary>");
            builder.AppendLine($"    public bool IsValid({model.QualifiedTypeName} value) {{");
            fastConditions.Declare(builder);
            builder.Append(fast);
            builder.AppendLine("        return true;");
            builder.AppendLine("    }");
        }

        builder.AppendLine("}");

        if (withDynamicAdapter) {
            EmitDynamicAdapter(builder, model);
        }

        return builder.ToString();
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
    private static void EmitDynamicAdapter(StringBuilder builder, ValidatedTypeModel model) {
        var type = model.QualifiedTypeName;
        var array = $"IValidatorFor<{type}>[]";

        builder.AppendLine();
        builder.AppendLine($"/// <summary>Reaches <see cref=\"{model.ValidatorName}\"/> by runtime type.</summary>");
        builder.AppendLine($"internal sealed class {model.TypeName}DynamicValidator : IDynamicValidator {{");
        builder.AppendLine("    private readonly global::System.IServiceProvider _services;");
        builder.AppendLine($"    private {array}? _validators;");
        builder.AppendLine();
        builder.AppendLine(
            $"    public {model.TypeName}DynamicValidator(global::System.IServiceProvider services) {{");
        builder.AppendLine("        _services = services;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine($"    public global::System.Type ValidatedType => typeof({type});");
        builder.AppendLine();
        builder.AppendLine("    // The race on first use is benign: two threads build equivalent arrays and one wins.");
        builder.AppendLine($"    private {array} Validators =>");
        builder.AppendLine("        _validators ??= global::System.Linq.Enumerable.ToArray(");
        builder.AppendLine("            global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions");
        builder.AppendLine($"                .GetServices<IValidatorFor<{type}>>(_services));");
        builder.AppendLine();
        builder.AppendLine("    public ValidationFlow Validate(ref ValidationContext context, object value) {");
        builder.AppendLine($"        var typed = ({type})value;");
        builder.AppendLine("        var validators = Validators;");
        builder.AppendLine();
        builder.AppendLine("        for (var i = 0; i < validators.Length; i++) {");
        builder.AppendLine("            if (validators[i].Validate(ref context, typed).ShouldStop) {");
        builder.AppendLine("                return ValidationFlow.Stop;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return ValidationFlow.Continue;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public bool IsValid(object value) {");
        builder.AppendLine($"        var typed = ({type})value;");
        builder.AppendLine("        var validators = Validators;");
        builder.AppendLine();
        builder.AppendLine("        for (var i = 0; i < validators.Length; i++) {");
        builder.AppendLine("            if (!validators[i].IsValid(typed)) {");
        builder.AppendLine("                return false;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return true;");
        builder.AppendLine("    }");
        builder.AppendLine("}");
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
    private static void EmitNestedDependencies(StringBuilder builder, ValidatedTypeModel model) {
        var nested = model.Properties.Where(p => p.ElementValidatorName is not null).ToList();

        if (nested.Count == 0) {
            builder.AppendLine($"    public {model.ValidatorName}() {{ }}");
            builder.AppendLine();
            return;
        }

        foreach (var property in nested) {
            builder.AppendLine(
                $"    private IValidatorFor<{ElementType(property)}>[]? {Field(property)};");
        }

        builder.AppendLine();
        builder.AppendLine("    /// <summary>Resolved from the container: the full set for each nested type.</summary>");
        builder.AppendLine($"    public {model.ValidatorName}(");

        for (var i = 0; i < nested.Count; i++) {
            var comma = i == nested.Count - 1 ? ") {" : ",";
            builder.AppendLine(
                $"        System.Collections.Generic.IEnumerable<IValidatorFor<{ElementType(nested[i])}>> {Parameter(nested[i])}{comma}");
        }

        foreach (var property in nested) {
            // Empty means absent, not "validate nothing". A container that has no
            // IValidatorFor<TNested> registered - the usual cause being a second assembly whose
            // AddXValidators() was never called - injects an empty sequence, and storing that
            // non-null array would leave the ??= fallback below unreachable. The nested value would
            // then be skipped in silence while every other constraint still reported, which reads
            // as validation working. Falling back to the generated validator is what the
            // parameterless constructor already does for the standalone case.
            builder.AppendLine($"        var resolved{property.PropertyName} = System.Linq.Enumerable.ToArray({Parameter(property)});");
            builder.AppendLine($"        {Field(property)} = resolved{property.PropertyName}.Length == 0 ? null : resolved{property.PropertyName};");
        }

        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Standalone: nested types fall back to their own generated validators.</summary>");
        builder.AppendLine($"    public {model.ValidatorName}() {{ }}");
        builder.AppendLine();

        foreach (var property in nested) {
            // A property that nests its own type resolves to this instance rather than a new one,
            // which is both correct and the cheapest way to terminate the common cycle.
            var fallback = property.ElementValidatorName == $"global::{Qualify(model)}"
                ? "this"
                : $"new {property.ElementValidatorName}()";

            builder.AppendLine(
                $"    private IValidatorFor<{ElementType(property)}>[] {Accessor(property)} =>");
            builder.AppendLine(
                $"        {Field(property)} ??= new IValidatorFor<{ElementType(property)}>[] {{ {fallback} }};");
            builder.AppendLine();
        }
    }

    /// <summary>
    /// The type a nested property's validators are for. A collection or dictionary carries its
    /// element type; a plain object's element type is the property's own, which the front end
    /// leaves null because nothing needed it before now.
    /// </summary>
    private static string ElementType(ValidatedPropertyModel property) =>
        property.ElementTypeName ?? property.TypeName;

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
    /// Reserved keywords only. <c>value</c>, <c>record</c> and <c>var</c> are contextual and are
    /// legal identifiers as they stand - <see cref="SyntaxFacts.GetKeywordKind"/> returns
    /// <see cref="SyntaxKind.None"/> for them, which is the distinction being relied on.
    /// </para>
    /// </remarks>
    private static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;

    private static string Qualify(ValidatedTypeModel model) =>
        model.Namespace.Length == 0 ? model.ValidatorName : $"{model.Namespace}.{model.ValidatorName}";

    private static void EmitProperty(
        StringBuilder builder,
        StringBuilder fast,
        ValidatedPropertyModel property,
        ValidatedTypeModel model,
        List<(string, ConstraintModel)> patterns,
        ConditionScope conditions,
        ConditionScope fastConditions,
        List<string> dispatchers) {

        var access = $"value.{Escape(property.PropertyName)}";
        var field = Quote(property.FieldName);
        var required = property.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.Required);

        // Every non-Required test is computed before anything is written, because whether the
        // Required check is worth hoisting into a local depends on whether anything ends up
        // chaining off it, and TestFor can decline a constraint outright.
        var others = new List<(ConstraintModel Constraint, string Test)>();

        foreach (var constraint in property.Constraints) {
            if (constraint.Kind != ConstraintKind.Required
                && TestFor(access, property, constraint, model, patterns) is { } test) {
                others.Add((constraint, test));
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
                builder.AppendLine($"        var {missing} = {requiredTest};");
                guard = missing;
            }

            builder.AppendLine(
                $"        if ({Conjoin(guard, Report(field, required, "ReportRequired", ""))}) return ValidationFlow.Stop;");
            fast.AppendLine(
                $"        if ({Guarded(fastConditions, required.Condition, RequiredTest(access, property, required))}) return false;");
        }

        foreach (var (constraint, test) in others) {
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

            var reportedField = constraint.Field is { } renamed ? Quote(renamed) : field;

            builder.AppendLine(
                $"        if ({Conjoin(string.Join(" && ", conjuncts), ReportFor(reportedField, constraint, property))})" +
                " return ValidationFlow.Stop;");

            // No guard on the boolean path: a failed Required has already returned, so anything
            // still running has a value to test.
            //
            // A warning or an info does not make a value invalid, so the boolean path skips it
            // rather than testing it: running the check and ignoring the answer would be the same
            // result at a cost, and returning false on it would be wrong.
            if (constraint.Severity is null) {
                fast.AppendLine($"        if ({Guarded(fastConditions, constraint.Condition, test)}) return false;");
            }
        }

        EmitNested(builder, fast, property, access, conditions, fastConditions, dispatchers, model.TypeName);
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
        StringBuilder builder,
        ValidatedPropertyModel property,
        string value,
        string context,
        string indent,
        string validators,
        string owner,
        List<string> dispatchers,
        bool boolean) {

        if (property.Polymorphism == PolymorphismMode.Runtime) {
            // The boolean path is not emitted for a type that dispatches dynamically, so this only
            // ever runs for Validate. Guarded rather than assumed, so that a future caller cannot
            // quietly get a services-less lookup.
            if (!boolean) {
                builder.AppendLine(
                    $"{indent}if (global::ValidationModules.DynamicValidation.Validate(" +
                    $"ref {context}, {value}, {Quote(property.FieldName)}, {Quote(owner)})" +
                    ".ShouldStop) return ValidationFlow.Stop;");
            }

            return;
        }

        var subtypes = property.Subtypes;

        if (property.Polymorphism != PolymorphismMode.CompileTime || subtypes.Count == 0) {
            EmitDeclaredCall(builder, property, value, context, indent, validators, boolean);
            return;
        }

        builder.AppendLine($"{indent}switch ({value}) {{");

        foreach (var subtype in subtypes) {
            var index = dispatchers.IndexOf(subtype.ValidatorName);

            if (index < 0) {
                dispatchers.Add(subtype.ValidatorName);
                index = dispatchers.Count - 1;
            }

            var call = boolean
                ? $"if (!(_dispatch{index} ??= new()).IsValid(__typed)) return false;"
                : $"if ((_dispatch{index} ??= new()).Validate(ref {context}, __typed).ShouldStop) " +
                  "return ValidationFlow.Stop;";

            builder.AppendLine($"{indent}    case {subtype.QualifiedTypeName} __typed: {call} break;");
        }

        builder.AppendLine($"{indent}    default: {{");
        EmitDeclaredCall(builder, property, value, context, indent + "        ", validators, boolean);
        builder.AppendLine($"{indent}        break;");
        builder.AppendLine($"{indent}    }}");
        builder.AppendLine($"{indent}}}");
    }

    /// <summary>The descent as it has always been: every injected validator for the declared type.</summary>
    private static void EmitDeclaredCall(
        StringBuilder builder,
        ValidatedPropertyModel property,
        string value,
        string context,
        string indent,
        string validators,
        bool boolean) {

        builder.AppendLine($"{indent}var {validators} = {Accessor(property)};");
        builder.AppendLine($"{indent}for (var vi = 0; vi < {validators}.Length; vi++) {{");

        builder.AppendLine(boolean
            ? $"{indent}    if (!{validators}[vi].IsValid({value})) return false;"
            : $"{indent}    if ({validators}[vi].Validate(ref {context}, {value}).ShouldStop) return ValidationFlow.Stop;");

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitNested(
        StringBuilder builder,
        StringBuilder fast,
        ValidatedPropertyModel property,
        string access,
        ConditionScope conditions,
        ConditionScope fastConditions,
        List<string> dispatchers,
        string owner) {
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

            builder.AppendLine($"        if ({Enter(conditions, $"{access} is {{ }} {entries}")}) {{");
            builder.AppendLine($"            foreach (var pair in {entries}) {{");
            builder.AppendLine("                if (pair.Value is not null) {");
            builder.AppendLine($"                    var entryCtx = ctx.PushKey({Quote(property.FieldName)}, pair.Key?.ToString() ?? \"\");");
            EmitDescent(builder, property, "pair.Value", "entryCtx", "                    ", "entryValidators", owner, dispatchers, boolean: false);
            builder.AppendLine("                }");
            builder.AppendLine("            }");
            builder.AppendLine("        }");

            fast.AppendLine($"        if ({Enter(fastConditions, $"{access} is {{ }} {entries}")}) {{");
            fast.AppendLine($"            foreach (var pair in {entries}) {{");
            fast.AppendLine("                if (pair.Value is not null) {");
            EmitDescent(fast, property, "pair.Value", string.Empty, "                    ", "entryValidators", owner, dispatchers, boolean: true);
            fast.AppendLine("                }");
            fast.AppendLine("            }");
            fast.AppendLine("        }");
            return;
        }

        if (property.Shape == PropertyShape.Object) {
            builder.AppendLine($"        if ({Enter(conditions, $"{access} is {{ }} nested{property.PropertyName}")}) {{");
            builder.AppendLine($"            var ctx{property.PropertyName} = ctx.Push({Quote(property.FieldName)});");
            EmitDescent(
                builder, property, $"nested{property.PropertyName}", $"ctx{property.PropertyName}",
                "            ", $"validators{property.PropertyName}", owner, dispatchers, boolean: false);
            builder.AppendLine("        }");

            fast.AppendLine($"        if ({Enter(fastConditions, $"{access} is {{ }} nested{property.PropertyName}")}) {{");
            EmitDescent(
                fast, property, $"nested{property.PropertyName}", string.Empty,
                "            ", $"validators{property.PropertyName}", owner, dispatchers, boolean: true);
            fast.AppendLine("        }");
            return;
        }

        var items = $"items{property.PropertyName}";
        var index = $"i{property.PropertyName}";

        builder.AppendLine($"        if ({Enter(conditions, $"{access} is {{ }} {items}")}) {{");

        if (property.IsIndexable) {
            // A for loop rather than foreach: enumerating an interface-typed collection boxes the
            // struct enumerator, and a clean pass over a collection property would then allocate.
            builder.AppendLine($"            for (var {index} = 0; {index} < {items}.{property.CountAccessor}; {index}++) {{");
            builder.AppendLine($"                var element = {items}[{index}];");
        } else {
            builder.AppendLine($"            var {index} = 0;");
            builder.AppendLine($"            foreach (var element in {items}) {{");
        }

        builder.AppendLine("                if (element is not null) {");
        builder.AppendLine($"                    var elementCtx = ctx.PushIndex({Quote(property.FieldName)}, {index});");
        EmitDescent(builder, property, "element", "elementCtx", "                    ", "elementValidators", owner, dispatchers, boolean: false);
        builder.AppendLine("                }");

        if (!property.IsIndexable) {
            builder.AppendLine($"                {index}++;");
        }

        builder.AppendLine("            }");
        builder.AppendLine("        }");

        fast.AppendLine($"        if ({Enter(fastConditions, $"{access} is {{ }} {items}")}) {{");

        if (property.IsIndexable) {
            fast.AppendLine($"            for (var {index} = 0; {index} < {items}.{property.CountAccessor}; {index}++) {{");
            fast.AppendLine($"                var element = {items}[{index}];");
        } else {
            fast.AppendLine($"            foreach (var element in {items}) {{");
        }

        fast.AppendLine("                if (element is not null) {");
        EmitDescent(fast, property, "element", string.Empty, "                    ", "elementValidators", owner, dispatchers, boolean: true);
        fast.AppendLine("                }");
        fast.AppendLine("            }");
        fast.AppendLine("        }");
    }

    private static string RequiredTest(string access, ValidatedPropertyModel property, ConstraintModel constraint) {
        if (property.IsString) {
            return constraint.AllowEmptyStrings
                ? $"{access} is null"
                : $"string.IsNullOrWhiteSpace({access})";
        }

        return property.IsNullableValueType ? $"{access} is null" : $"{access} is null";
    }

    private static string? TestFor(
        string access,
        ValidatedPropertyModel property,
        ConstraintModel constraint,
        ValidatedTypeModel model,
        List<(string, ConstraintModel)> patterns) {

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
                $", {Quote(string.Join(", ", Displays(constraint)))}"),
            // A flags value is a combination, so "must be one of" would be wrong about what the
            // type accepts. Says which flags exist instead.
            ConstraintKind.EnumDefined when constraint.FlagsMask is not null =>
                $"ctx.Report({field}, ValidationCodes.Enum, " +
                $"{Quote($"{Unquote(field)} must be a combination of: {string.Join(", ", Displays(constraint))}.")})",
            ConstraintKind.EnumDefined => Report(field, constraint, "ReportAllowedValues",
                $", {Quote(string.Join(", ", Displays(constraint)))}"),

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
    private static string Report(string field, ConstraintModel constraint, string helper, string arguments) {
        // Omitted entirely at the default rather than passed as ValidationSeverity.Error, so the
        // emitted line stays the one a reader would have written by hand.
        var severity = constraint.Severity is { } member ? $", ValidationSeverity.{member}" : string.Empty;

        if (constraint.Message is { } message) {
            var code = constraint.Code is { } custom ? Quote(custom) : CodeConstant(constraint.Kind);
            return $"ctx.Report({field}, {code}, {Quote(message)}{severity})";
        }

        return $"ctx.{helper}({field}{arguments}{severity})";
    }

    private static string CodeConstant(ConstraintKind kind) => kind switch {
        ConstraintKind.Required => "ValidationCodes.Required",
        ConstraintKind.StringLength => "ValidationCodes.StringLength",
        ConstraintKind.Range => "ValidationCodes.Range",
        ConstraintKind.Pattern => "ValidationCodes.Pattern",
        ConstraintKind.AllowedValues => "ValidationCodes.Enum",
        ConstraintKind.EnumDefined => "ValidationCodes.Enum",
        ConstraintKind.MultipleOf => "ValidationCodes.MultipleOf",
        ConstraintKind.UniqueItems => "ValidationCodes.UniqueItems",
        ConstraintKind.Predicate => "ValidationCodes.Predicate",
        _ => "ValidationCodes.ArrayBounds",
    };

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

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
