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

        foreach (var statement in StatementsOf(syntax)) {
            reader.Read(statement);
        }

        return reader.Finish();
    }

    private static IEnumerable<StatementSyntax> StatementsOf(MethodDeclarationSyntax syntax) {
        if (syntax.Body is { } block) {
            return block.Statements;
        }

        // An expression-bodied Describe is one rule, which is a shape worth accepting: a rules class
        // that says one thing should not have to open a block to say it.
        return syntax.ExpressionBody is { } arrow
            ? new StatementSyntax[] { SyntaxFactory.ExpressionStatement(arrow.Expression) }
            : Array.Empty<StatementSyntax>();
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
            if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }) {
                _owner.Report(ValidationDiagnostics.NotARuleDeclaration, statement, _rulesClass.Name);
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

            foreach (var call in chain) {
                anchor = ReadCall(call, anchor);
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
        private IPropertySymbol? ReadCall(InvocationExpressionSyntax call, IPropertySymbol? inherited) {
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
                _rules.Add(new DeclaredRule(anchor, field, constraint, Nesting.None));
            } else if (name is "Nested" or "Each") {
                _rules.Add(new DeclaredRule(anchor, field, null,
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
            var lifted = $"Rule{_predicates.Count}";
            _predicates.Add(new LiftedPredicate(lifted, predicate));

            var accessor = $"global::{Namespace()}{_rulesClass.Name}_Rules.{lifted}";

            _rules.Add(new DeclaredRule(
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
public sealed record DeclaredRule(
    IPropertySymbol? Property,
    string Field,
    ConstraintModel? Constraint,
    Nesting Nesting);

/// <summary>A predicate to be lifted into a static method the validator can call.</summary>
public sealed record LiftedPredicate(string MethodName, ExpressionSyntax Lambda);

/// <summary>Everything one rules class declared, before it is merged with the target's attributes.</summary>
public sealed record RulesDeclaration(
    INamedTypeSymbol Target,
    INamedTypeSymbol RulesClass,
    IReadOnlyList<DeclaredRule> Rules,
    IReadOnlyList<string> AppliedRules,
    IReadOnlyList<LiftedPredicate> Predicates,
    string ParameterName);
