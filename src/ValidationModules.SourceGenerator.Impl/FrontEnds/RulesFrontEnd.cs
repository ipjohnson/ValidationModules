using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ValidationModules.Rules;
using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Reads an <c>IValidationRulesFor&lt;T&gt;.Describe</c> body into the IR.
/// </summary>
/// <remarks>
/// <para>
/// The third front-end, and the only one that reads statements rather than attributes. It never
/// executes anything: the body is parsed as syntax and flattened, the same as a constraint attribute
/// is read out of metadata and flattened. What the runtime does with the same body is run it once -
/// see <c>DescribedValidator&lt;T&gt;</c> - and the two are pinned to the same output by sharing
/// <see cref="RuleText"/> and by reaching the same <c>ctx.Add*</c> helpers.
/// </para>
/// <para>
/// <b>Field names are resolved from the selector's syntax, not its text.</b> The runtime has only
/// the text <c>CallerArgumentExpression</c> hands it; here the semantic model is available, so the
/// property symbol is resolved properly and <c>[JsonPropertyName]</c> is honoured. That is the one
/// place the two engines can disagree, and API-SURFACE.md §19.9 documents it rather than pretending
/// otherwise.
/// </para>
/// <para>
/// <b>The body is a whitelist.</b> Anything that is not a rule declaration is VM0070 - a build error,
/// never something quietly skipped. A runnable body looks like ordinary code, so the half that cannot
/// be compiled has to break the build rather than behave differently on the two engines.
/// </para>
/// </remarks>
public sealed class RulesFrontEnd {
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly Func<string, string> _fieldNamer;

    public RulesFrontEnd(Func<string, string> fieldNamer) => _fieldNamer = fieldNamer;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Reads every rule declared by <paramref name="rulesClass"/>, or null if it is not a rules
    /// class.
    /// </summary>
    /// <param name="rulesClass">The candidate type.</param>
    /// <param name="compilation">Supplies the semantic model for the declaring syntax tree.</param>
    public RulesDeclaration? Build(INamedTypeSymbol rulesClass, Compilation compilation) {
        var contract = rulesClass.AllInterfaces.FirstOrDefault(i =>
            i.ConstructedFrom.ToDisplayString() == KnownTypes.ValidationRulesForInterface);

        if (contract is null || contract.TypeArguments.Length != 1) {
            return null;
        }

        if (contract.TypeArguments[0] is not INamedTypeSymbol target) {
            return null;
        }

        var describe = rulesClass.GetMembers("Describe")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.Parameters.Length == 1);

        if (describe?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not MethodDeclarationSyntax syntax) {
            return null;
        }

        var model = compilation.GetSemanticModel(syntax.SyntaxTree);
        var parameter = describe.Parameters[0].Name;
        var reader = new BodyReader(this, model, target, rulesClass, parameter);

        if (syntax.Body is { } block) {
            foreach (var statement in block.Statements) {
                reader.Read(statement);
            }
        } else if (syntax.ExpressionBody is { } arrow) {
            // An expression-bodied Describe is one rule, which is a shape worth accepting: a rules
            // class that says one thing should not have to open a block to say it.
            //
            // The expression is read where it stands. Wrapping it in a synthesized
            // ExpressionStatementSyntax to reuse the statement path looks harmless and is not: a
            // synthesized node belongs to no syntax tree, so the first GetSymbolInfo against it
            // throws ArgumentException ("Syntax node is not within syntax tree"). That surfaces as
            // CS8785 and takes down the whole compilation's output - every validator in the project
            // disappears and the only visible error is a missing MValidator type.
            reader.ReadExpression(arrow.Expression);
        }

        return reader.Finish();
    }

    private void Report(DiagnosticDescriptor descriptor, SyntaxNode node, params object?[] args) =>
        _diagnostics.Add(Diagnostic.Create(descriptor, node.GetLocation(), args));

    /// <summary>
    /// Walks one <c>Describe</c> body, accumulating rules per property plus the type-level ones.
    /// </summary>
    private sealed class BodyReader {
        private readonly RulesFrontEnd _owner;
        private readonly SemanticModel _model;
        private readonly INamedTypeSymbol _target;
        private readonly INamedTypeSymbol _rulesClass;
        private readonly string _parameter;

        private readonly List<DeclaredRule> _rules = new();
        private readonly List<string> _applied = new();
        private readonly List<LiftedPredicate> _predicates = new();

        /// <summary>The conditions of the blocks currently open around whatever is being read.</summary>
        private readonly List<string> _open = new();

        /// <summary>
        /// Separate counters, so that adding a condition to a rules class does not renumber its
        /// Ensure predicates - the lifted names appear in generated code people read.
        /// </summary>
        private int _liftedRules;

        private int _liftedConditions;

        /// <summary>
        /// The block a trailing <c>Otherwise</c> would negate, as the lifted call and its polarity.
        /// Otherwise reuses this rather than lifting a second method for the same lambda.
        /// </summary>
        private string? _lastBlockCall;

        private bool _lastBlockNegated;

        public BodyReader(
            RulesFrontEnd owner,
            SemanticModel model,
            INamedTypeSymbol target,
            INamedTypeSymbol rulesClass,
            string parameter) {

            _owner = owner;
            _model = model;
            _target = target;
            _rulesClass = rulesClass;
            _parameter = parameter;
        }

        public void Read(StatementSyntax statement) {
            if (statement is not ExpressionStatementSyntax { Expression: { } expression }) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, statement, _rulesClass.Name);
                return;
            }

            ReadExpression(expression, statement);
        }

        /// <summary>
        /// Reads one rule from its expression, which an expression-bodied <c>Describe</c> has
        /// without any enclosing statement to wrap it in.
        /// </summary>
        /// <param name="expression">The rule expression.</param>
        /// <param name="report">
        /// The node a diagnostic is anchored to. Defaults to the expression itself, which is what
        /// the arrow form wants; the statement form passes the whole statement so that the squiggle
        /// covers the trailing semicolon as it always has.
        /// </param>
        public void ReadExpression(ExpressionSyntax expression, SyntaxNode? report = null) {
            if (expression is not InvocationExpressionSyntax invocation) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, report ?? expression, _rulesClass.Name);
                return;
            }

            var chain = new List<InvocationExpressionSyntax>();

            if (!Unwind(invocation, chain)) {
                return;
            }

            // Innermost first, so an anchor established by the entry call is in hand before the
            // chained calls that inherit it.
            chain.Reverse();

            IPropertySymbol? anchor = null;
            var start = _rules.Count;

            foreach (var call in chain) {
                anchor = ReadCall(call, anchor, start);
            }
        }

        /// <summary>
        /// Collects the invocations of a chained statement, outermost first, and verifies the whole
        /// thing hangs off the builder parameter rather than off something else.
        /// </summary>
        private bool Unwind(ExpressionSyntax expression, List<InvocationExpressionSyntax> chain) {
            while (true) {
                switch (expression) {
                    case InvocationExpressionSyntax invocation:
                        chain.Add(invocation);
                        expression = invocation.Expression;
                        continue;

                    case MemberAccessExpressionSyntax member:
                        expression = member.Expression;
                        continue;

                    case IdentifierNameSyntax identifier when identifier.Identifier.Text == _parameter:
                        return true;

                    default:
                        _owner.Report(ValidationDiagnostics.NotARuleDeclaration, expression, _rulesClass.Name);
                        return false;
                }
            }
        }

        /// <summary>
        /// Reads one call in a chain and returns the property it leaves anchored.
        /// </summary>
        private IPropertySymbol? ReadCall(
            InvocationExpressionSyntax call, IPropertySymbol? inherited, int statementStart) {
            if (_model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
                return inherited;
            }

            var arguments = Arguments(call, method);
            var name = method.Name;

            if (name == "Apply") {
                ReadApply(call, arguments);
                return inherited;
            }

            if (name == "Ensure") {
                ReadEnsure(call, arguments);
                return inherited;
            }

            if (name is "When" or "Unless") {
                // Arity separates the two shapes cleanly: one argument terminates a statement, two
                // open a block. Nothing else has to tell them apart.
                if (method.Parameters.Length == 1) {
                    StampStatement(call, arguments, statementStart, negated: name == "Unless");
                } else {
                    ReadBlock(call, arguments, negated: name == "Unless");
                }

                return inherited;
            }

            if (name == "Otherwise") {
                ReadOtherwise(call, arguments);
                return inherited;
            }

            var anchor = arguments.TryGetValue("value", out var selector)
                ? PropertyOf(selector)
                : inherited;

            if (anchor is null) {
                _owner.Report(ValidationDiagnostics.SelectorNotAPath, call, _rulesClass.Name);
                return null;
            }

            var field = ExplicitField(arguments) ?? _owner._fieldNamer(anchor.Name);
            var constraint = ConstraintFor(name, arguments, call);

            if (constraint is not null) {
                AddRule(new DeclaredRule(anchor, field, constraint, Nesting.None));
            } else if (name is "Nested" or "Each") {
                AddRule(new DeclaredRule(anchor, field, null,
                    name == "Nested" ? Nesting.Object : Nesting.Elements));
            } else if (name != "For") {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
            }

            return anchor;
        }

        private ConstraintModel? ConstraintFor(
            string name, IReadOnlyDictionary<string, ExpressionSyntax> arguments, InvocationExpressionSyntax call) =>
            name switch {
                "Required" => new ConstraintModel(ConstraintKind.Required),
                "RequiredAllowingEmpty" => new ConstraintModel(ConstraintKind.Required, AllowEmptyStrings: true),
                "Length" => new ConstraintModel(
                    ConstraintKind.StringLength,
                    Min: Bound(arguments, "min", "0"),
                    Max: Bound(arguments, "max", int.MaxValue.ToString())),
                "Count" => new ConstraintModel(
                    ConstraintKind.ItemCount,
                    Min: Bound(arguments, "min", "0"),
                    Max: Bound(arguments, "max", int.MaxValue.ToString())),
                // Null rather than a "0" fallback for the bound a one-sided form does not take: an
                // omitted bound has to stay omitted all the way to the emitter, or it becomes a
                // comparison the author never wrote and a bound the message quotes back at a caller.
                "Range" => new ConstraintModel(
                    ConstraintKind.Range,
                    Min: OptionalBound(arguments, "min"),
                    Max: OptionalBound(arguments, "max")),
                "RangeAtLeast" => new ConstraintModel(
                    ConstraintKind.Range,
                    Min: OptionalBound(arguments, "min")),
                "RangeAtMost" => new ConstraintModel(
                    ConstraintKind.Range,
                    Max: OptionalBound(arguments, "max")),
                "Unique" => new ConstraintModel(ConstraintKind.UniqueItems),

                // The divisor is carried as written and resolved against the member's type by the
                // shared front-end pass, the same as a [MultipleOf] argument is.
                "MultipleOf" => new ConstraintModel(
                    ConstraintKind.MultipleOf,
                    Divisor: Bound(arguments, "divisor", "1")),
                "Pattern" => PatternConstraint(arguments, call),
                "AllowedValues" => AllowedValuesConstraint(arguments, call),
                _ => null,
            };

        private ConstraintModel? PatternConstraint(
            IReadOnlyDictionary<string, ExpressionSyntax> arguments, InvocationExpressionSyntax call) {

            if (!arguments.TryGetValue("pattern", out var accessor)) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
                return null;
            }

            // The accessor is a method group, so the emitted form is that method invoked. No inline
            // pattern can reach here at all, which is why VM0017's policy has nothing to say about a
            // rules class - the AOT-clean spelling is the only one this surface offers.
            return _model.GetSymbolInfo(accessor).Symbol is IMethodSymbol regex
                ? new ConstraintModel(ConstraintKind.Pattern,
                    RegexAccessor: $"{regex.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{regex.Name}()")
                : null;
        }

        private ConstraintModel AllowedValuesConstraint(
            IReadOnlyDictionary<string, ExpressionSyntax> arguments, InvocationExpressionSyntax call) {

            var values = new List<string>();

            foreach (var argument in call.ArgumentList.Arguments) {
                if (argument.Expression == (arguments.TryGetValue("value", out var selector) ? selector : null)) {
                    continue;
                }

                if (_model.GetConstantValue(argument.Expression) is { HasValue: true }) {
                    values.Add(argument.Expression.ToString());
                }
            }

            return new ConstraintModel(
                ConstraintKind.AllowedValues,
                Values: new EquatableArray<string>(System.Collections.Immutable.ImmutableArray.CreateRange(values)));
        }

        private void ReadEnsure(InvocationExpressionSyntax call, IReadOnlyDictionary<string, ExpressionSyntax> arguments) {
            if (!arguments.TryGetValue("predicate", out var predicate)) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
                return;
            }

            if (!IsSelfContained(predicate)) {
                return;
            }

            var text = predicate.ToString();
            var anchor = RuleText.AnchorOfPredicate(text) is { } name ? PropertyNamed(name) : null;

            // The anchor has to resolve to a property, even when field: renames the error. A rule is
            // emitted in its anchored property's chain so that both engines agree on ordering (§4.2),
            // and there is nowhere to put one that belongs to no property. field: renames; it does
            // not detach.
            if (anchor is null) {
                _owner.Report(ValidationDiagnostics.EnsureHasNoField, call, _rulesClass.Name);
                return;
            }

            var field = ExplicitField(arguments) ?? _owner._fieldNamer(anchor.Name);
            var lifted = $"Rule{_liftedRules++}";
            _predicates.Add(new LiftedPredicate(lifted, predicate, LiftBody(predicate)));

            var accessor = $"global::{Namespace()}{_rulesClass.Name}_Rules.{lifted}";

            AddRule(new DeclaredRule(
                anchor,
                field,
                new ConstraintModel(
                    ConstraintKind.Predicate,
                    Code: Literal(arguments, "code"),
                    Message: Literal(arguments, "message")
                        ?? RuleText.RenderPredicate(text, _owner._fieldNamer),
                    PredicateAccessor: accessor,
                    // Carried on the constraint, not the property: several rules can anchor to one
                    // property and each name a different field, so a per-property name would let
                    // the first one silently win.
                    Field: ExplicitField(arguments),
                    Severity: SeverityOf(arguments)),
                Nesting.None));
        }

        /// <summary>
        /// Adds a rule carrying whatever conditional blocks are open around it.
        /// </summary>
        private void AddRule(DeclaredRule rule) {
            if (_open.Count > 0) {
                // Nested blocks conjoin rather than replace, with no depth limit worth imposing.
                rule = Conditioned(rule, string.Join(" && ", _open));
            }

            _rules.Add(rule);
        }

        /// <summary>
        /// Conditions every rule the current statement declared - the one-argument
        /// <c>.When()</c> / <c>.Unless()</c> form.
        /// </summary>
        /// <remarks>
        /// Scope is the statement, which is already the unit this reader walks. That is what removes
        /// the need for FluentValidation's <c>ApplyConditionTo</c>: there is no retroactive default
        /// to opt out of, because nothing reaches past the semicolon.
        /// </remarks>
        private void StampStatement(
            InvocationExpressionSyntax call,
            IReadOnlyDictionary<string, ExpressionSyntax> arguments,
            int statementStart,
            bool negated) {

            if (!arguments.TryGetValue("condition", out var predicate)) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
                return;
            }

            if (!IsLambda(predicate, call) || !IsSelfContained(predicate)) {
                return;
            }

            ReportIfConstant(predicate, call, negated);

            if (statementStart >= _rules.Count) {
                _owner.Report(
                    ValidationDiagnostics.ConditionAppliesToNoRules, call, negated ? "Unless" : "When");
                return;
            }

            var condition = Lift(predicate, negated);

            for (var i = statementStart; i < _rules.Count; i++) {
                _rules[i] = Conditioned(_rules[i], condition);
            }
        }

        /// <summary>
        /// Reads a <c>When(condition, () =&gt; …)</c> block, with the condition open over its body.
        /// </summary>
        private void ReadBlock(
            InvocationExpressionSyntax call,
            IReadOnlyDictionary<string, ExpressionSyntax> arguments,
            bool negated) {

            if (!arguments.TryGetValue("condition", out var predicate) ||
                !arguments.TryGetValue("rules", out var body)) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
                return;
            }

            if (!IsLambda(predicate, call) || !IsSelfContained(predicate)) {
                return;
            }

            ReportIfConstant(predicate, call, negated);

            var lifted = LiftCall(predicate);

            _lastBlockCall = lifted;
            _lastBlockNegated = negated;

            ReadBlockBody(call, body, negated ? $"!({lifted})" : lifted, negated ? "Unless" : "When");
        }

        /// <summary>
        /// Reads the <c>Otherwise</c> half, reusing the block's own lifted method negated rather
        /// than lifting a second one for the same lambda.
        /// </summary>
        private void ReadOtherwise(
            InvocationExpressionSyntax call, IReadOnlyDictionary<string, ExpressionSyntax> arguments) {

            if (_lastBlockCall is not { } lifted || !arguments.TryGetValue("rules", out var body)) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
                return;
            }

            ReadBlockBody(call, body, _lastBlockNegated ? lifted : $"!({lifted})", "Otherwise");
        }

        private void ReadBlockBody(
            InvocationExpressionSyntax call, ExpressionSyntax body, string condition, string what) {

            var start = _rules.Count;

            _open.Add(condition);

            try {
                switch (body) {
                    case ParenthesizedLambdaExpressionSyntax { Block: { } block }:
                        foreach (var statement in block.Statements) {
                            Read(statement);
                        }

                        break;

                    // A block that says one thing does not have to open braces to say it, for the
                    // same reason an expression-bodied Describe does not.
                    case ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } expression }:
                        ReadExpression(expression);
                        break;

                    default:
                        _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
                        break;
                }
            } finally {
                _open.RemoveAt(_open.Count - 1);
            }

            if (_rules.Count == start) {
                _owner.Report(ValidationDiagnostics.EmptyConditionalBlock, call, what, _rulesClass.Name);
            }
        }

        /// <summary>
        /// Reports a condition whose body the compiler can fold to a constant.
        /// </summary>
        /// <remarks>
        /// The one check here no runtime library could offer: a described engine holds a delegate
        /// and cannot know what it returns without calling it, where the generator has the
        /// expression in hand. Roslyn does the folding, so <c>x =&gt; 1 &gt; 2</c> is caught along
        /// with the literal.
        /// </remarks>
        private void ReportIfConstant(ExpressionSyntax predicate, InvocationExpressionSyntax call, bool negated) {
            var body = predicate switch {
                SimpleLambdaExpressionSyntax { ExpressionBody: { } expression } => expression,
                ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } expression } => expression,
                _ => null,
            };

            if (body is null || _model.GetConstantValue(body) is not { HasValue: true, Value: bool folded }) {
                return;
            }

            var holds = negated ? !folded : folded;

            _owner.Report(
                ValidationDiagnostics.ConstantCondition, call,
                holds ? "true" : "false",
                holds
                    ? "the guard is noise - the rules it covers always apply"
                    : "the rules it covers can never fire");
        }

        /// <summary>
        /// Whether the condition was written as a lambda, which is the only form that can be lifted.
        /// </summary>
        /// <remarks>
        /// A method group reaches the emitter with no body to copy, and the lifted method would come
        /// out as <c>=&gt; true</c> - a condition that silently holds always, in generated code
        /// nobody reads. Reported rather than emitted: this is exactly the class of quiet wrong
        /// answer the no-emit-after-diagnostic rule exists to prevent.
        /// </remarks>
        private bool IsLambda(ExpressionSyntax predicate, InvocationExpressionSyntax call) {
            if (predicate is SimpleLambdaExpressionSyntax { ExpressionBody: not null } or
                ParenthesizedLambdaExpressionSyntax { ExpressionBody: not null }) {
                return true;
            }

            _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);

            return false;
        }

        /// <summary>
        /// Lifts a condition lambda into a static method and returns the call to it.
        /// </summary>
        /// <summary>
        /// The predicate's body, rewritten so that it compiles where it is going to be written.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A predicate is lifted into <c>{RulesClass}_Rules</c> so it keeps its declaring file's
        /// using directives. The cost is that a name which resolved inside the rules class does not
        /// resolve there: <c>x =&gt; x.Count &lt;= Max</c> becomes CS0103, whatever <c>Max</c>'s
        /// accessibility, because the lifted method is simply not in that scope.
        /// </para>
        /// <para>
        /// So a bare reference to one of the rules class's own members is qualified. That reads the
        /// real member rather than a copy of it, which is what keeps the two engines agreeing: the
        /// described engine runs the original lambda, and it must see the same value.
        /// </para>
        /// <para>
        /// A <c>private</c> member cannot be reached even qualified. A constant is carried across by
        /// value - C# already bakes a const at every use site, so both engines see the same number
        /// either way - and anything else is VM0078.
        /// </para>
        /// </remarks>
        private string LiftBody(ExpressionSyntax lambda) {
            var body = lambda switch {
                SimpleLambdaExpressionSyntax { ExpressionBody: { } expression } => expression,
                ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } expression } => expression,
                _ => null,
            };

            if (body is null) {
                return "true";
            }

            var qualified = _rulesClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var edits = new List<(int Start, int Length, string Text)>();

            foreach (var identifier in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()) {
                // The right-hand side of a member access is already anchored by whatever precedes
                // it; only a bare name has lost its scope.
                if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier) {
                    continue;
                }

                if (_model.GetSymbolInfo(identifier).Symbol is not { IsStatic: true } symbol ||
                    symbol.ContainingType is not { } declaring ||
                    !DeclaredByTheRulesClass(declaring)) {
                    continue;
                }

                var start = identifier.Span.Start - body.Span.Start;

                if (_model.Compilation.IsSymbolAccessibleWithin(symbol, _rulesClass.ContainingAssembly)) {
                    edits.Add((start, identifier.Span.Length, $"{qualified}.{identifier.Identifier.Text}"));
                    continue;
                }

                if (ConstantText(identifier) is { } literal) {
                    edits.Add((start, identifier.Span.Length, literal));
                    continue;
                }

                _owner.Report(
                    ValidationDiagnostics.PredicateReferencesPrivateMember, identifier,
                    $"{_rulesClass.Name}.{identifier.Identifier.Text}");
            }

            var text = body.ToString();

            // Right to left, so an earlier edit does not move a later one's offsets.
            foreach (var (start, length, replacement) in edits.OrderByDescending(edit => edit.Start)) {
                text = text.Remove(start, length).Insert(start, replacement);
            }

            return text;
        }

        private bool DeclaredByTheRulesClass(INamedTypeSymbol declaring) {
            for (INamedTypeSymbol? current = _rulesClass; current is not null; current = current.BaseType) {
                if (SymbolEqualityComparer.Default.Equals(current, declaring)) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A constant rendered as a literal that reads back as the same value <i>and</i> the same
        /// type, or null when it cannot be.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every C# constant type can be written back exactly; what has to be got right is the
        /// suffix and the round-trip format. <c>SymbolDisplay.FormatPrimitive</c> alone is not
        /// enough - it renders <c>1.5m</c> as <c>1.5</c>, which is a <c>double</c> literal and so a
        /// different type, and it renders an enum as a bare number.
        /// </para>
        /// <para>
        /// Floating point uses <c>G17</c>/<c>G9</c> rather than the default: shortest-round-trip
        /// formatting only became the default in .NET Core 3.0, and this assembly is netstandard2.0
        /// and may be loaded into a .NET Framework host. Both formats round-trip everywhere.
        /// </para>
        /// <para>
        /// There is no fidelity question about doing this at all: C# bakes a constant into every use
        /// site already, so the lifted copy and the original are the same value by the language's
        /// own rules rather than by our arithmetic.
        /// </para>
        /// </remarks>
        private string? ConstantText(ExpressionSyntax identifier) {
            var constant = _model.GetConstantValue(identifier);

            if (!constant.HasValue) {
                return null;
            }

            if (constant.Value is not { } value) {
                return "null";
            }

            // An enum constant arrives as its underlying integral value, so the type is what makes
            // it read back as itself. A cast rather than a member name, because a value need not
            // correspond to any declared member - a [Flags] combination is an ordinary constant.
            if (_model.GetTypeInfo(identifier).Type is { TypeKind: TypeKind.Enum } enumType) {
                return _model.Compilation.IsSymbolAccessibleWithin(enumType, _rulesClass.ContainingAssembly)
                    && Primitive(value) is { } underlying
                        ? $"({enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){underlying}"
                        : null;
            }

            return Primitive(value);
        }

        private static string? Primitive(object value) => value switch {
            bool or string or char => SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false),
            sbyte or byte or short or ushort or int =>
                SymbolDisplay.FormatPrimitive(value, quoteStrings: false, useHexadecimalNumbers: false),
            uint number => $"{number.ToString(CultureInfo.InvariantCulture)}U",
            long number => $"{number.ToString(CultureInfo.InvariantCulture)}L",
            ulong number => $"{number.ToString(CultureInfo.InvariantCulture)}UL",
            float number => Floating(
                number, float.IsNaN(number), float.IsPositiveInfinity(number), float.IsNegativeInfinity(number),
                "float", number.ToString("G9", CultureInfo.InvariantCulture), "F"),
            double number => Floating(
                number, double.IsNaN(number), double.IsPositiveInfinity(number), double.IsNegativeInfinity(number),
                "double", number.ToString("G17", CultureInfo.InvariantCulture), "D"),

            // ToString round-trips a decimal exactly, scale included - 1.50m stays 1.50m rather
            // than collapsing to 1.5m, which is the same value but a different representation.
            decimal number => $"{number.ToString(CultureInfo.InvariantCulture)}m",
            _ => null,
        };

        /// <summary>
        /// A floating-point literal, or the named member for the three values that have no literal.
        /// </summary>
        private static string Floating(
            object value, bool nan, bool positiveInfinity, bool negativeInfinity,
            string type, string formatted, string suffix) {

            _ = value;

            if (nan) {
                return $"{type}.NaN";
            }

            if (positiveInfinity) {
                return $"{type}.PositiveInfinity";
            }

            if (negativeInfinity) {
                return $"{type}.NegativeInfinity";
            }

            // The suffix is not decoration: G17 renders 10.0 as "10", which without it is an int.
            return formatted + suffix;
        }

        private string LiftCall(ExpressionSyntax predicate) {
            var lifted = $"Cond{_liftedConditions++}";
            _predicates.Add(new LiftedPredicate(lifted, predicate, LiftBody(predicate)));

            return $"global::{Namespace()}{_rulesClass.Name}_Rules.{lifted}(value)";
        }

        private string Lift(ExpressionSyntax predicate, bool negated) {
            var call = LiftCall(predicate);

            return negated ? $"!({call})" : call;
        }

        /// <summary>
        /// Adds <paramref name="condition"/> to whatever the rule already carries.
        /// </summary>
        /// <remarks>
        /// Conjoined rather than replaced, so a chained <c>.When()</c> written inside a <c>When</c>
        /// block means both. The emitter hoists each distinct call once, so repeating one across
        /// several rules costs one evaluation, not several.
        /// </remarks>
        private static DeclaredRule Conditioned(DeclaredRule rule, string condition) => rule with {
            Constraint = rule.Constraint is { } constraint
                ? constraint with { Condition = Conjoin(constraint.Condition, condition) }
                : null,
            Condition = Conjoin(rule.Condition, condition),
        };

        private static string Conjoin(string? existing, string added) =>
            existing is null ? added : $"{existing} && {added}";

        private void ReadApply(InvocationExpressionSyntax call, IReadOnlyDictionary<string, ExpressionSyntax> arguments) {
            if (!arguments.TryGetValue("rule", out var rule) ||
                _model.GetSymbolInfo(rule).Symbol is not IMethodSymbol method) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, call, _rulesClass.Name);
                return;
            }

            _applied.Add(
                $"{method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{method.Name}");
        }

        /// <summary>
        /// Checks that a predicate reads only its own parameter and static or constant state.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rule that makes the two engines the same thing: the generator lifts the predicate into
        /// a static method, and the runtime holds it as a delegate. A delegate can close over the
        /// rules class instance and a static method cannot, so anything captured compiles on one path
        /// and not on the other.
        /// </para>
        /// <para>
        /// Caught here rather than left to the compiler because the compiler would catch it in the
        /// <i>lifted</i> file - an error about a name that does not exist, in generated code, pointing
        /// at a line nobody wrote. Plan §7.5 is explicit that this is the worst place for an error to
        /// surface.
        /// </para>
        /// </remarks>
        private bool IsSelfContained(ExpressionSyntax predicate) {
            var lambdaParameters = predicate is LambdaExpressionSyntax lambda
                ? _model.GetSymbolInfo(lambda).Symbol as IMethodSymbol
                : null;

            foreach (var node in predicate.DescendantNodesAndSelf()) {
                if (node is ThisExpressionSyntax or BaseExpressionSyntax) {
                    _owner.Report(ValidationDiagnostics.PredicateCapturesState, node, _rulesClass.Name);
                    return false;
                }

                if (node is not IdentifierNameSyntax identifier) {
                    continue;
                }

                var symbol = _model.GetSymbolInfo(identifier).Symbol;

                var captured = symbol switch {
                    ILocalSymbol => true,
                    IParameterSymbol parameter =>
                        lambdaParameters is null ||
                        !lambdaParameters.Parameters.Any(own => SymbolEqualityComparer.Default.Equals(own, parameter)),
                    IFieldSymbol { IsStatic: false } field =>
                        SymbolEqualityComparer.Default.Equals(field.ContainingType, _rulesClass),
                    IPropertySymbol { IsStatic: false } property =>
                        SymbolEqualityComparer.Default.Equals(property.ContainingType, _rulesClass),
                    IMethodSymbol { IsStatic: false } method =>
                        SymbolEqualityComparer.Default.Equals(method.ContainingType, _rulesClass),
                    _ => false,
                };

                if (captured) {
                    _owner.Report(ValidationDiagnostics.PredicateCapturesState, identifier, _rulesClass.Name);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Resolves the property a selector reads, rejecting anything that is not a plain path.
        /// </summary>
        private IPropertySymbol? PropertyOf(ExpressionSyntax selector) {
            if (selector is not LambdaExpressionSyntax lambda || lambda.Body is not ExpressionSyntax body) {
                return null;
            }

            while (body is MemberAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax inner }) {
                body = inner;
            }

            return body is MemberAccessExpressionSyntax member &&
                   _model.GetSymbolInfo(member).Symbol is IPropertySymbol property
                ? property
                : null;
        }

        private IPropertySymbol? PropertyNamed(string name) =>
            _target.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();

        private string? ExplicitField(IReadOnlyDictionary<string, ExpressionSyntax> arguments) =>
            Literal(arguments, "field");

        /// <summary>
        /// The severity member named by a <c>severity:</c> argument, or null for the default.
        /// </summary>
        /// <remarks>
        /// Read as the enum's underlying constant rather than as source text, so
        /// <c>ValidationSeverity.Warning</c>, an alias and a cast of the literal all resolve the
        /// same. Anything that is not a member of the enum is left as null rather than guessed at.
        /// </remarks>
        private string? SeverityOf(IReadOnlyDictionary<string, ExpressionSyntax> arguments) {
            if (!arguments.TryGetValue("severity", out var expression)) {
                return null;
            }

            return _model.GetConstantValue(expression) is { HasValue: true, Value: int value }
                ? value switch { 1 => "Warning", 2 => "Info", _ => null }
                : null;
        }

        private string? Literal(IReadOnlyDictionary<string, ExpressionSyntax> arguments, string parameter) =>
            arguments.TryGetValue(parameter, out var expression) &&
            _model.GetConstantValue(expression) is { HasValue: true, Value: string text }
                ? text
                : null;

        private string Bound(
            IReadOnlyDictionary<string, ExpressionSyntax> arguments, string parameter, string fallback) =>
            arguments.TryGetValue(parameter, out var expression) ? expression.ToString() : fallback;

        /// <summary>A bound that was not passed, as null rather than as a stand-in value.</summary>
        private string? OptionalBound(
            IReadOnlyDictionary<string, ExpressionSyntax> arguments, string parameter) =>
            arguments.TryGetValue(parameter, out var expression) && expression.ToString() != "null"
                ? expression.ToString()
                : null;

        /// <summary>
        /// Maps a call's arguments onto parameter names, so the reader never depends on position and
        /// a caller may pass <c>field:</c> or <c>code:</c> wherever they like.
        /// </summary>
        private static Dictionary<string, ExpressionSyntax> Arguments(
            InvocationExpressionSyntax call, IMethodSymbol method) {

            var mapped = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
            var position = 0;

            // An extension method invoked in reduced form has its receiver as parameter 0 of the
            // unreduced symbol but not in the argument list, so the reduced form is what lines up.
            var parameters = (method.ReducedFrom is null ? method : method).Parameters;

            foreach (var argument in call.ArgumentList.Arguments) {
                if (argument.NameColon is { } named) {
                    mapped[named.Name.Identifier.Text] = argument.Expression;
                    continue;
                }

                if (position < parameters.Length) {
                    mapped[parameters[position].Name] = argument.Expression;
                }

                position++;
            }

            return mapped;
        }

        private string Namespace() =>
            _rulesClass.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : _rulesClass.ContainingNamespace.ToDisplayString() + ".";

        public RulesDeclaration Finish() => new(
            _target,
            _rulesClass,
            _rules,
            _applied,
            _predicates,
            _parameter);
    }
}

/// <summary>Whether a rule descends, and into what.</summary>
public enum Nesting {
    None,
    Object,
    Elements,
}

/// <summary>One rule read out of a Describe body, still carrying its Roslyn symbols.</summary>
/// <param name="Condition">
/// Guards this rule, when a <c>When</c>/<c>Unless</c> covers it. Carried on the rule as well as on
/// its constraint because a nesting rule has no constraint to carry it - <c>rules.Nested(x =&gt;
/// x.Auto).When(…)</c> guards the descent itself.
/// </param>
public sealed record DeclaredRule(
    IPropertySymbol? Property,
    string Field,
    ConstraintModel? Constraint,
    Nesting Nesting,
    string? Condition = null);

/// <summary>A predicate to be lifted into a static method the validator can call.</summary>
/// <param name="MethodName">The lifted method's name.</param>
/// <param name="Lambda">The declaration, which still supplies the parameter name.</param>
/// <param name="Body">
/// The body as it should be written into the lifted class: bare references to the rules class's own
/// members qualified, and private constants replaced by their value. Resolved in the front end
/// because that is where the semantic model is.
/// </param>
public sealed record LiftedPredicate(string MethodName, ExpressionSyntax Lambda, string Body);

/// <summary>Everything one rules class declared, before it is merged with the target's attributes.</summary>
public sealed record RulesDeclaration(
    INamedTypeSymbol Target,
    INamedTypeSymbol RulesClass,
    IReadOnlyList<DeclaredRule> Rules,
    IReadOnlyList<string> AppliedRules,
    IReadOnlyList<LiftedPredicate> Predicates,
    string ParameterName);
