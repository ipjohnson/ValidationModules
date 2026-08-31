using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ValidationModules.Rules;
using ValidationModules.SourceGenerator.Impl.Emitters;
using ValidationModules.SourceGenerator.Impl.Models;

namespace ValidationModules.SourceGenerator.Impl.FrontEnds;

/// <summary>
/// Transcribes an <c>IValidationRulesFor&lt;T&gt;.Describe</c> body into the region method the
/// generated validator calls.
/// </summary>
/// <remarks>
/// <para>
/// The body is C# that is read, never run. Vocabulary calls are recognized islands expanded into
/// check-and-report code; every other statement - locals, LINQ, arithmetic, <c>if</c>/<c>else</c> -
/// is transcribed and runs at validation time inside the region method. Positional transcription:
/// the region mirrors the body statement for statement, and semantics are C# semantics.
/// </para>
/// <para>
/// <b>Two invariants, everything else relaxes.</b> The builder flows only where this reader can
/// follow (VM0087), and transcribed code must compile at the emission site (VM0088). The region is
/// emitted into a companion file carrying the rules class's own using directives - the
/// <c>PredicateEmitter</c> move, extended - so what has to be rewritten is small: <c>nameof</c>
/// through the subject parameter becomes the wire path, bare references to the rules class's own
/// statics are qualified, <c>rules.Context</c> becomes the live context, and a bare <c>return</c>
/// becomes <c>return Continue</c>.
/// </para>
/// <para>
/// <b>Fragments are expanded into companion methods, not textually inlined.</b> A fragment's body
/// resolves against its own file's using directives, which the caller's companion does not have -
/// the exact hazard the predicate lifting existed to avoid. Each fragment (per concrete target for
/// a generic one) becomes a method in a container carrying the fragment file's usings, called in
/// place; ordering, the shared collector and per-concrete-type field names are unchanged by the
/// difference.
/// </para>
/// </remarks>
public sealed class RulesFrontEnd {
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly Func<string, string> _fieldNamer;

    /// <summary>Whether a type is the target of a rules class somewhere in this compilation -
    /// supplied by the caller, which sees every candidate, so a facet whose rules are declared
    /// externally is not accused of having none.</summary>
    private readonly Func<INamedTypeSymbol, bool>? _rulesTarget;

    /// <summary>Fragment methods accumulated across every rules class in the pass, keyed by
    /// (fragment, concrete target) so two callers share one method.</summary>
    private readonly Dictionary<string, FragmentMethod> _fragments = new(StringComparer.Ordinal);

    private readonly List<FragmentContainer> _containers = new();

    public RulesFrontEnd(
        Func<string, string> fieldNamer,
        Func<INamedTypeSymbol, bool>? rulesTarget = null,
        string? codeNamespace = null) {
        _fieldNamer = fieldNamer;
        _rulesTarget = rulesTarget;
        _codeNamespace = codeNamespace;
    }

    /// <summary>The assembly's code namespace, applied to what an Ensure authors or derives.</summary>
    private readonly string? _codeNamespace;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// The fragment containers every processed rules class asked for, one per declaring type,
    /// carrying that type's file usings. Emitted once per pass, after every candidate is read.
    /// </summary>
    public IReadOnlyList<FragmentContainer> FragmentContainers => _containers;

    /// <summary>
    /// Reads every target <paramref name="rulesClass"/> declares rules for - one region per
    /// implemented <c>IValidationRulesFor&lt;T&gt;</c>, so one class can describe several types,
    /// each still getting its own validator. Empty when the candidate is not a rules class.
    /// </summary>
    /// <remarks>
    /// Each interface is paired with its <c>Describe</c> through
    /// <see cref="ITypeSymbol.FindImplementationForInterfaceMember"/> rather than a name lookup.
    /// That is what lands overloaded <c>Describe</c>s on their own targets - and what makes an
    /// explicitly implemented one visible at all, its metadata name not being "Describe". A body
    /// that fails transcription reports diagnostics and drops only its own region - nothing is
    /// emitted after an error, so a broken declaration cannot become a validator that checks less
    /// than it says.
    /// </remarks>
    public IReadOnlyList<RulesDeclaration> Build(INamedTypeSymbol rulesClass, Compilation compilation) {
        List<RulesDeclaration>? declarations = null;

        // Regions merge into one companion class per rules class, so the cached-facet fields of
        // every region must stay distinct; the seed carries the count across writers.
        var fieldSeed = 0;

        foreach (var contract in rulesClass.AllInterfaces) {
            if (contract.ConstructedFrom.ToDisplayString() != KnownTypes.ValidationRulesForInterface ||
                contract.TypeArguments.Length != 1 ||
                contract.TypeArguments[0] is not INamedTypeSymbol target) {
                continue;
            }

            if (contract.GetMembers("Describe").FirstOrDefault() is not IMethodSymbol declared ||
                rulesClass.FindImplementationForInterfaceMember(declared) is not IMethodSymbol describe ||
                describe.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                    is not MethodDeclarationSyntax syntax) {
                continue;
            }

            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            var before = _diagnostics.Count;
            var writer = new RegionWriter(
                this, compilation, model, target, rulesClass,
                describe.Parameters[0], describe.Parameters[1],
                fieldSeed: fieldSeed);

            if (syntax.Body is { } block) {
                writer.ReadBlock(block.Statements, depth: 0);
            } else if (syntax.ExpressionBody is { } arrow) {
                // An expression-bodied Describe is one statement's worth of rules. The expression
                // is read where it stands - a synthesized statement node belongs to no syntax tree
                // and the first GetSymbolInfo against one throws, taking the whole generator's
                // output with it.
                writer.ReadExpressionStatement(arrow.Expression, depth: 0, report: arrow.Expression);
            }

            if (FailedSince(before)) {
                continue;
            }

            fieldSeed += writer.Fields.Count;

            (declarations ??= new List<RulesDeclaration>()).Add(new RulesDeclaration(
                target,
                rulesClass,
                describe.Parameters[1].Name,
                writer.Lines,
                writer.Dependencies,
                writer.AppliedRules,
                writer.Fields));
        }

        return declarations ?? (IReadOnlyList<RulesDeclaration>)Array.Empty<RulesDeclaration>();
    }

    private void Report(DiagnosticDescriptor descriptor, SyntaxNode node, params object?[] args) =>
        _diagnostics.Add(Diagnostic.Create(descriptor, node.GetLocation(), args));

    /// <summary>
    /// Whether anything reported since <paramref name="before"/> leaves the body unusable, which
    /// is what makes the caller drop it rather than emit a validator built from a partial read.
    /// </summary>
    /// <remarks>
    /// Severity rather than a count, because not every diagnostic from a body is a refusal.
    /// VM0092 states the code a rule derived and the rule is emitted regardless; counting it would
    /// silently drop the whole rules class for saying something true about it.
    /// </remarks>
    private bool FailedSince(int before) {
        for (var index = before; index < _diagnostics.Count; index++) {
            if (_diagnostics[index].Severity == DiagnosticSeverity.Error) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the wire name of one property: <c>[JsonPropertyName]</c> first, then
    /// <c>[Display(Name)]</c>, then the naming policy - the same ladder the attribute front end
    /// applies, so a rule written in a body and one written as an attribute name the field alike.
    /// </summary>
    internal string WireNameOf(IPropertySymbol property) {
        foreach (var attribute in property.GetAttributes()) {
            var name = attribute.AttributeClass?.ToDisplayString();

            if (name == KnownTypes.JsonPropertyName &&
                attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is string jsonName) {
                return jsonName;
            }

            if (name == KnownTypes.DisplayAttribute) {
                foreach (var named in attribute.NamedArguments) {
                    if (named.Key == "Name" && named.Value.Value is string displayName) {
                        return displayName;
                    }
                }
            }
        }

        return _fieldNamer(property.Name);
    }

    /// <summary>
    /// Registers one fragment instantiation, transcribing its body on first use, and returns the
    /// call target. Two rules classes calling the same fragment for the same target share one
    /// method.
    /// </summary>
    private FragmentMethod? FragmentFor(
        IMethodSymbol constructed,
        INamedTypeSymbol target,
        Compilation compilation,
        SyntaxNode site,
        List<IMethodSymbol> expanding) {

        var definition = constructed.OriginalDefinition;
        var key = $"{definition.ToDisplayString()}|{(constructed.IsGenericMethod ? target.ToDisplayString() : string.Empty)}";

        // The stack check comes before the registry: a fragment registers itself before its body
        // is read so two callers share one method, and a cycle would otherwise hit that early
        // registration and emit mutually recursive methods that run forever at validation time.
        if (expanding.Any(open => SymbolEqualityComparer.Default.Equals(open, definition))) {
            Report(ValidationDiagnostics.FragmentCallCycle, site,
                string.Join(" -> ", expanding.Select(m => m.Name).Concat(new[] { definition.Name })));
            return null;
        }

        if (_fragments.TryGetValue(key, out var existing)) {
            return existing;
        }

        if (definition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
            is not MethodDeclarationSyntax syntax || syntax.Body is null && syntax.ExpressionBody is null) {
            Report(ValidationDiagnostics.FragmentIsCompiledIl, site,
                $"{definition.ContainingType.Name}.{definition.Name}");
            return null;
        }

        // The fragment's own parameter roles, resolved on the constructed symbol so a generic
        // fragment's subject parameter is already typed as the concrete target.
        IParameterSymbol? builder = null;
        IParameterSymbol? subject = null;
        var extras = new List<IParameterSymbol>();

        foreach (var parameter in constructed.Parameters) {
            if (parameter.Type is INamedTypeSymbol named &&
                named.ConstructedFrom.ToDisplayString() == KnownTypes.ValidationRulesBuilder) {
                builder = parameter;
            } else if (SymbolEqualityComparer.Default.Equals(parameter.Type, target)) {
                subject ??= parameter;
            } else {
                extras.Add(parameter);
            }
        }

        if (builder is null) {
            return null;
        }

        var model = compilation.GetSemanticModel(syntax.SyntaxTree);
        var name = constructed.IsGenericMethod ? $"{definition.Name}_{target.Name}" : definition.Name;
        var container = ContainerFor(definition.ContainingType);

        // Distinct fragments can collide on the composed name - overloads, or two targets sharing
        // a simple name. A numeric suffix keeps the method callable; the container's readability
        // survives because the collision is rare.
        while (container.Methods.Any(m => m.Name == name)) {
            name += "_";
        }

        var method = new FragmentMethod(
            name,
            definition,
            target,
            subject,
            builder.Name,
            extras);

        _fragments[key] = method;
        container.Methods.Add(method);

        var before = _diagnostics.Count;
        var writer = new RegionWriter(
            this, compilation, model, target, definition.ContainingType,
            // The fragment method reuses the fragment's own parameter names, so its body transcribes
            // with no identifier rewriting - the same property the region method has.
            builder: definition.Parameters[IndexOf(definition, builder)],
            subject: subject is null ? null : definition.Parameters[IndexOf(definition, subject)],
            expanding: expanding.Concat(new[] { definition }).ToList(),
            insideFragment: true,
            fieldPrefix: $"_{name}Facet");

        if (syntax.Body is { } block) {
            writer.ReadBlock(block.Statements, depth: 0);
        } else if (syntax.ExpressionBody is { } arrow) {
            writer.ReadExpressionStatement(arrow.Expression, depth: 0, report: arrow.Expression);
        }

        method.BodyLines.AddRange(writer.Lines);
        method.Fields.AddRange(writer.Fields);

        return FailedSince(before) ? null : method;
    }

    private static int IndexOf(IMethodSymbol definition, IParameterSymbol constructedParameter) {
        for (var i = 0; i < definition.Parameters.Length; i++) {
            if (definition.Parameters[i].Ordinal == constructedParameter.Ordinal) {
                return i;
            }
        }

        return constructedParameter.Ordinal;
    }

    private FragmentContainer ContainerFor(INamedTypeSymbol declaringType) {
        foreach (var container in _containers) {
            if (SymbolEqualityComparer.Default.Equals(container.DeclaringType, declaringType)) {
                return container;
            }
        }

        var ns = declaringType.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : declaringType.ContainingNamespace.ToDisplayString();

        var created = new FragmentContainer(ns, $"{declaringType.Name}_Fragments", declaringType);

        _containers.Add(created);

        return created;
    }

    /// <summary>
    /// Walks one body - a Describe or a fragment - producing the region's statement lines.
    /// </summary>
    private sealed class RegionWriter {
        private const string Flow = "global::ValidationModules.ValidationFlow";
        private const string Codes = "global::ValidationModules.ValidationCodes";
        private const string SeverityEnum = "global::ValidationModules.ValidationSeverity";
        private const string ContextExtensions = "global::ValidationModules.ValidationContextExtensions";

        private readonly RulesFrontEnd _owner;
        private readonly Compilation _compilation;
        private readonly SemanticModel _model;
        private readonly INamedTypeSymbol _target;
        private readonly INamedTypeSymbol _declaringClass;
        private readonly IParameterSymbol _builder;
        private readonly IParameterSymbol? _subject;
        private readonly List<IMethodSymbol> _expanding;
        private readonly bool _insideFragment;

        private readonly List<string> _lines = new();
        private readonly List<RegionDependency> _dependencies = new();
        private readonly List<string> _applied = new();
        private readonly List<CompanionField> _fields = new();
        private readonly string _fieldPrefix;
        private readonly int _fieldSeed;

        /// <summary>One counter for every generated local, so expansions cannot collide with each
        /// other whatever the author named things.</summary>
        private int _locals;

        private readonly HashSet<string> _missingLocals = new(StringComparer.Ordinal);

        /// <summary>
        /// The local holding one chain's failed-Require result, named after the property the way
        /// the attribute region names it, with a counter only when a name repeats.
        /// </summary>
        private string MissingLocal(string propertyName) {
            var name = $"missing{propertyName}";

            while (!_missingLocals.Add(name)) {
                name = $"missing{propertyName}{_locals++}";
            }

            return name;
        }

        public RegionWriter(
            RulesFrontEnd owner,
            Compilation compilation,
            SemanticModel model,
            INamedTypeSymbol target,
            INamedTypeSymbol declaringClass,
            IParameterSymbol builder,
            IParameterSymbol? subject,
            List<IMethodSymbol>? expanding = null,
            bool insideFragment = false,
            string fieldPrefix = "_facet",
            int fieldSeed = 0) {

            _fieldPrefix = fieldPrefix;
            _fieldSeed = fieldSeed;
            _owner = owner;
            _compilation = compilation;
            _model = model;
            _target = target;
            _declaringClass = declaringClass;
            _builder = builder;
            _subject = subject;
            _expanding = expanding ?? new List<IMethodSymbol>();
            _insideFragment = insideFragment;
        }

        public IReadOnlyList<string> Lines => _lines;

        public IReadOnlyList<RegionDependency> Dependencies => _dependencies;

        public IReadOnlyList<string> AppliedRules => _applied;

        /// <summary>The lazily-built facet validators this region caches, emitted as fields on the
        /// companion class.</summary>
        public IReadOnlyList<CompanionField> Fields => _fields;

        private string CompanionField(string typeQualified) {
            var field = new CompanionField(typeQualified, $"{_fieldPrefix}{_fieldSeed + _fields.Count}");

            _fields.Add(field);

            return field.Name;
        }

        /// <summary>
        /// Whether anything in this compilation declares rules for a facet: the attribute on the
        /// facet itself, constraint attributes on its properties, or a rules class targeting it.
        /// An As over a facet with none would be a silent no-op, which is VM0091 instead.
        /// </summary>
        private bool FacetHasRules(INamedTypeSymbol facet) {
            if (facet.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == KnownTypes.GenerateValidatorAttribute)) {
                return true;
            }

            if (_owner._rulesTarget?.Invoke(facet) == true) {
                return true;
            }

            foreach (var property in facet.GetMembers().OfType<IPropertySymbol>()) {
                foreach (var attribute in property.GetAttributes()) {
                    var ns = attribute.AttributeClass?.ContainingNamespace?.ToDisplayString();

                    if (ns == KnownTypes.ConstraintsNamespace || ns == KnownTypes.DataAnnotationsNamespace) {
                        return true;
                    }
                }
            }

            return false;
        }

        public void ReadBlock(
            IEnumerable<StatementSyntax> statements, int depth, bool inLoop = false, bool inSwitch = false) {
            foreach (var statement in statements) {
                ReadStatement(statement, depth, inLoop, inSwitch);
            }
        }

        private void ReadStatement(StatementSyntax statement, int depth, bool inLoop, bool inSwitch = false) {
            switch (statement) {
                case ExpressionStatementSyntax { Expression: { } expression }:
                    ReadExpressionStatement(expression, depth, statement, inLoop);
                    return;

                case LocalDeclarationStatementSyntax declaration:
                    if (declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) {
                        _owner.Report(ValidationDiagnostics.NotTranscribable, statement,
                            _declaringClass.Name, "a using declaration");
                        return;
                    }

                    Transcribe(declaration, depth);
                    return;

                case IfStatementSyntax conditional:
                    ReadIf(conditional, depth, inLoop, inSwitch);
                    return;

                case SwitchStatementSyntax dispatch:
                    ReadSwitch(dispatch, depth, inLoop);
                    return;

                case ForStatementSyntax loop:
                    ReadLoop(loop, loop.Statement, $"for ({Rewrite(loop.Declaration?.ToString() ?? loop.Initializers.ToString())}; {RewriteOptional(loop.Condition)}; {Rewrite(loop.Incrementors.ToString())})", depth);
                    return;

                case ForEachStatementSyntax each:
                    ReadLoop(each, each.Statement,
                        $"foreach ({each.Type} {each.Identifier.Text} in {Rewrite(each.Expression)})", depth);
                    return;

                case WhileStatementSyntax spin:
                    ReadLoop(spin, spin.Statement, $"while ({Rewrite(spin.Condition)})", depth);
                    return;

                case DoStatementSyntax done:
                    Line(depth, "do {");
                    ReadEmbedded(done.Statement, depth + 1, inLoop: true);
                    Line(depth, $"}} while ({Rewrite(done.Condition)});");
                    return;

                case ReturnStatementSyntax { Expression: null }:
                    // The region is a method, so an early return ends this rules class's checks and
                    // nothing else. Continue rather than Stop: the author is done, not failing.
                    Line(depth, $"return {Flow}.Continue;");
                    return;

                case ReturnStatementSyntax:
                    _owner.Report(ValidationDiagnostics.NotTranscribable, statement,
                        _declaringClass.Name, "a return with a value - Describe returns nothing");
                    return;

                case BlockSyntax nested:
                    Line(depth, "{");
                    ReadBlock(nested.Statements, depth + 1, inLoop, inSwitch);
                    Line(depth, "}");
                    return;

                case LocalFunctionStatementSyntax function:
                    GuardIslandsInside(function, "a local function");
                    Transcribe(function, depth);
                    return;

                case BreakStatementSyntax when inLoop || inSwitch:
                case ContinueStatementSyntax when inLoop:
                    Transcribe(statement, depth);
                    return;

                case ThrowStatementSyntax:
                    Transcribe(statement, depth);
                    return;

                case EmptyStatementSyntax:
                    return;

                default:
                    // goto, unsafe, lock, try, fixed, yield, checked blocks - the v1-rejected
                    // exotica, admitted later if a real case appears.
                    _owner.Report(ValidationDiagnostics.NotTranscribable, statement,
                        _declaringClass.Name, $"a {statement.Kind()}");
                    return;
            }
        }

        public void ReadExpressionStatement(
            ExpressionSyntax expression, int depth, SyntaxNode report, bool inLoop = false) {

            if (RootsAtBuilder(expression)) {
                // rules.Context.… roots at the builder but is transcription, not an island: the
                // reporter tier is legal anywhere, loops included. Its flow-typed result lands in
                // the auto-wrap below.
                if (ReachesThroughContext(expression)) {
                    var reported = _model.GetTypeInfo(expression).Type;

                    if (reported?.ToDisplayString() == "ValidationModules.ValidationFlow") {
                        Line(depth, $"if (({Rewrite(expression)}).ShouldStop) {{");
                        Line(depth + 1, $"return {Flow}.Stop;");
                        Line(depth, "}");
                    } else {
                        Line(depth, $"{Rewrite(expression)};");
                    }

                    return;
                }

                if (inLoop) {
                    _owner.Report(ValidationDiagnostics.IslandInUnreadableScope, report, _declaringClass.Name);
                    return;
                }

                ReadIsland(expression, depth, report);
                return;
            }

            if (IsFragmentCall(expression, out var fragmentCall, out var method)) {
                if (inLoop) {
                    _owner.Report(ValidationDiagnostics.IslandInUnreadableScope, report, _declaringClass.Name);
                    return;
                }

                ReadFragmentCall(fragmentCall!, method!, depth);
                return;
            }

            // Mutating the subject from a validation body is the detectable half of the purity
            // line; the rest is convention.
            if (expression is AssignmentExpressionSyntax { Left: { } lhs } && Roots(lhs, _subject)) {
                _owner.Report(ValidationDiagnostics.NotTranscribable, report,
                    _declaringClass.Name, "an assignment to the subject - validation does not mutate its value");
                return;
            }

            // Type-driven auto-flow-wrap: any expression-statement whose type is ValidationFlow is
            // checked and propagated. No method list to maintain - it covers every Report helper,
            // future ones, and user helpers returning a flow. Assigning the flow opts out.
            var type = _model.GetTypeInfo(expression).Type;

            if (type?.ToDisplayString() == "ValidationModules.ValidationFlow") {
                Line(depth, $"if (({Rewrite(expression)}).ShouldStop) {{");
                Line(depth + 1, $"return {Flow}.Stop;");
                Line(depth, "}");
                return;
            }

            Line(depth, $"{Rewrite(expression)};");
        }

        private void ReadIf(IfStatementSyntax conditional, int depth, bool inLoop, bool inSwitch = false) {
            Line(depth, $"if ({Rewrite(conditional.Condition)}) {{");
            ReadEmbedded(conditional.Statement, depth + 1, inLoop, inSwitch);

            var alternative = conditional.Else;

            while (alternative is not null) {
                if (alternative.Statement is IfStatementSyntax chained) {
                    Line(depth, $"}} else if ({Rewrite(chained.Condition)}) {{");
                    ReadEmbedded(chained.Statement, depth + 1, inLoop, inSwitch);
                    alternative = chained.Else;
                } else {
                    Line(depth, "} else {");
                    ReadEmbedded(alternative.Statement, depth + 1, inLoop, inSwitch);
                    alternative = null;
                }
            }

            Line(depth, "}");
        }

        private void ReadSwitch(SwitchStatementSyntax dispatch, int depth, bool inLoop) {
            Line(depth, $"switch ({Rewrite(dispatch.Expression)}) {{");

            foreach (var section in dispatch.Sections) {
                foreach (var label in section.Labels) {
                    Line(depth + 1, Rewrite(label).TrimEnd());
                }

                ReadBlock(section.Statements, depth + 2, inLoop, inSwitch: true);
            }

            Line(depth, "}");
        }

        private void ReadLoop(StatementSyntax loop, StatementSyntax body, string header, int depth) {
            _ = loop;
            Line(depth, $"{header} {{");
            ReadEmbedded(body, depth + 1, inLoop: true);
            Line(depth, "}");
        }

        private void ReadEmbedded(StatementSyntax statement, int depth, bool inLoop, bool inSwitch = false) {
            if (statement is BlockSyntax block) {
                ReadBlock(block.Statements, depth, inLoop, inSwitch);
            } else {
                ReadStatement(statement, depth, inLoop, inSwitch);
            }
        }

        // ---- islands ---------------------------------------------------------------------------

        /// <summary>Whether the expression is an invocation chain hanging off the builder parameter.</summary>
        private bool RootsAtBuilder(ExpressionSyntax expression) {
            var current = expression;

            while (true) {
                switch (current) {
                    case InvocationExpressionSyntax invocation:
                        current = invocation.Expression;
                        continue;

                    case MemberAccessExpressionSyntax member:
                        current = member.Expression;
                        continue;

                    case IdentifierNameSyntax identifier:
                        return SymbolEqualityComparer.Default.Equals(
                            _model.GetSymbolInfo(identifier).Symbol, _builder);

                    default:
                        return false;
                }
            }
        }

        private bool Roots(ExpressionSyntax expression, ISymbol? root) {
            if (root is null) {
                return false;
            }

            var current = expression;

            while (current is MemberAccessExpressionSyntax member) {
                current = member.Expression;
            }

            return current is IdentifierNameSyntax identifier &&
                SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(identifier).Symbol, root);
        }

        private void ReadIsland(ExpressionSyntax expression, int depth, SyntaxNode report) {
            var chain = new List<InvocationExpressionSyntax>();
            var current = expression;

            while (true) {
                if (current is InvocationExpressionSyntax invocation) {
                    chain.Add(invocation);
                    current = invocation.Expression;
                } else if (current is MemberAccessExpressionSyntax member) {
                    current = member.Expression;
                } else {
                    break;
                }
            }

            chain.Reverse();

            var expansion = new IslandExpansion(this, depth);

            foreach (var call in chain) {
                if (!expansion.ReadCall(call, report)) {
                    return;
                }
            }

            expansion.Emit();
        }

        private bool ReachesThroughContext(ExpressionSyntax expression) {
            for (var current = expression; ;) {
                switch (current) {
                    case InvocationExpressionSyntax invocation:
                        current = invocation.Expression;
                        continue;

                    case MemberAccessExpressionSyntax member:
                        if (member.Name.Identifier.Text == "Context" &&
                            member.Expression is IdentifierNameSyntax root &&
                            SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(root).Symbol, _builder)) {
                            return true;
                        }

                        current = member.Expression;
                        continue;

                    default:
                        return false;
                }
            }
        }

        // ---- fragments -------------------------------------------------------------------------

        private bool IsFragmentCall(
            ExpressionSyntax expression, out InvocationExpressionSyntax? call, out IMethodSymbol? method) {

            call = null;
            method = null;

            if (expression is not InvocationExpressionSyntax invocation ||
                _model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol candidate) {
                return false;
            }

            var resolved = candidate.ReducedFrom is { } reduced
                ? reduced.Construct(candidate.TypeArguments.ToArray())
                : candidate;

            if (!resolved.IsStatic || !resolved.ReturnsVoid) {
                return false;
            }

            // Receives the builder, in any position - the reduced extension receiver included.
            var receivesBuilder =
                (candidate.ReducedFrom is not null && invocation.Expression is MemberAccessExpressionSyntax { Expression: { } receiver } &&
                    Roots(receiver, _builder)) ||
                invocation.ArgumentList.Arguments.Any(argument =>
                    argument.Expression is IdentifierNameSyntax name &&
                    SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(name).Symbol, _builder));

            if (!receivesBuilder) {
                return false;
            }

            if (!resolved.Parameters.Any(parameter =>
                    parameter.Type is INamedTypeSymbol named &&
                    named.ConstructedFrom.ToDisplayString() == KnownTypes.ValidationRulesBuilder)) {
                return false;
            }

            call = invocation;
            method = resolved;

            return true;
        }

        private void ReadFragmentCall(InvocationExpressionSyntax call, IMethodSymbol method, int depth) {
            var fragment = _owner.FragmentFor(method, _target, _compilation, call, _expanding);

            if (fragment is null) {
                return;
            }

            // The subject argument must be the subject parameter - a facet of a child is Nested's
            // territory, where the path pushes.
            var arguments = MapArguments(
                call, method,
                reducedForm: _model.GetSymbolInfo(call).Symbol is IMethodSymbol { ReducedFrom: not null });

            if (fragment.Subject is { } subjectParameter) {
                if (!arguments.TryGetValue(subjectParameter.Name, out var subjectArgument) ||
                    !(subjectArgument is IdentifierNameSyntax name &&
                        SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(name).Symbol, _subject))) {
                    _owner.Report(ValidationDiagnostics.RulesFlowNotFollowable, call,
                        $"the fragment's {_target.Name} parameter must be passed the Describe subject");
                    return;
                }
            }

            var rendered = new List<string> { "ref ctx" };

            if (fragment.Subject is not null) {
                rendered.Add(_subject!.Name);
            }

            foreach (var extra in fragment.ExtraParameters) {
                if (arguments.TryGetValue(extra.Name, out var argument)) {
                    rendered.Add(Rewrite(argument));
                } else if (extra.HasExplicitDefaultValue) {
                    rendered.Add(FormatDefault(extra));
                } else {
                    rendered.Add("default");
                }
            }

            var ns = fragment.Definition.ContainingType.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : fragment.Definition.ContainingType.ContainingNamespace.ToDisplayString() + ".";

            Line(depth,
                $"if (global::{ns}{fragment.Definition.ContainingType.Name}_Fragments.{fragment.Name}({string.Join(", ", rendered)}).ShouldStop) {{");
            Line(depth + 1, $"return {Flow}.Stop;");
            Line(depth, "}");
        }

        private static string FormatDefault(IParameterSymbol parameter) =>
            parameter.ExplicitDefaultValue is null
                ? parameter.Type.IsReferenceType ? "null" : "default"
                : SymbolDisplay.FormatPrimitive(parameter.ExplicitDefaultValue, quoteStrings: true, useHexadecimalNumbers: false)
                    ?? "default";

        /// <summary>
        /// Maps a call's arguments onto parameter names, so nothing downstream depends on position
        /// and a caller may pass <c>field:</c> or <c>max:</c> wherever they like.
        /// </summary>
        /// <param name="parameters">
        /// The unreduced parameter owner. A reduced extension call carries its receiver outside
        /// the argument list, so the first parameter is skipped to keep positions lined up.
        /// </param>
        private Dictionary<string, ExpressionSyntax> MapArguments(
            InvocationExpressionSyntax call, IMethodSymbol parameters, bool reducedForm) {

            var mapped = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
            var position = reducedForm ? 1 : 0;

            foreach (var argument in call.ArgumentList.Arguments) {
                if (argument.NameColon is { } named) {
                    mapped[named.Name.Identifier.Text] = argument.Expression;
                    continue;
                }

                if (position < parameters.Parameters.Length) {
                    mapped[parameters.Parameters[position].Name] = argument.Expression;
                }

                position++;
            }

            return mapped;
        }

        // ---- transcription ---------------------------------------------------------------------

        private void GuardIslandsInside(SyntaxNode scope, string what) {
            foreach (var node in scope.DescendantNodes()) {
                if (node is IdentifierNameSyntax identifier &&
                    SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(identifier).Symbol, _builder)) {
                    _owner.Report(ValidationDiagnostics.IslandInUnreadableScope, identifier, _declaringClass.Name);
                    return;
                }
            }

            _ = what;
        }

        private void Transcribe(StatementSyntax statement, int depth) {
            foreach (var line in Rewrite(statement).Split('\n')) {
                Line(depth, line.TrimEnd('\r'));
            }
        }

        private string RewriteOptional(ExpressionSyntax? expression) =>
            expression is null ? string.Empty : Rewrite(expression);

        private string Rewrite(SyntaxNode node) {
            GuardBuilderInside(node);
            CheckAccessibility(node);

            var rewriter = new TranscriptionRewriter(this);
            var rewritten = rewriter.Visit(node);

            return rewritten.NormalizeWhitespace("    ", "\n").ToFullString();
        }

        private string Rewrite(string text) => text;

        /// <summary>
        /// Invariant 1: inside transcribed code the builder may appear only under
        /// <c>rules.Context</c>. Everywhere else it is a flow the reader cannot follow - stored,
        /// captured, returned, or passed somewhere unreadable - and would transcribe into a call on
        /// the inert surface that validates nothing.
        /// </summary>
        private void GuardBuilderInside(SyntaxNode node) {
            foreach (var identifier in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()) {
                if (!SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(identifier).Symbol, _builder)) {
                    continue;
                }

                if (identifier.Parent is MemberAccessExpressionSyntax { Name.Identifier.Text: "Context" } access &&
                    access.Expression == identifier) {
                    continue;
                }

                _owner.Report(ValidationDiagnostics.RulesFlowNotFollowable, identifier,
                    "store it, capture it, return it, or pass it to anything the generator cannot read");
            }
        }

        /// <summary>
        /// Invariant 2: everything transcribed must compile in the companion file. The companion is
        /// internal to the same assembly, so what breaks is <c>private</c>/<c>protected</c> members
        /// of the rules class - caught here, with "make it internal", instead of surfacing inside
        /// generated code.
        /// </summary>
        private void CheckAccessibility(SyntaxNode node) {
            foreach (var identifier in node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()) {
                var symbol = _model.GetSymbolInfo(identifier).Symbol;

                if (symbol is null or ILocalSymbol or IParameterSymbol or IRangeVariableSymbol
                    or IDiscardSymbol or ILabelSymbol) {
                    continue;
                }

                if (symbol is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction }) {
                    continue;
                }

                // A private constant is carried across by value instead - C# bakes a const at
                // every use site already, so the copy and the original are the same value by the
                // language's own rules. Everything else private is the diagnostic.
                if (symbol is IFieldSymbol { HasConstantValue: true } bakeable &&
                    ConstantText(identifier, bakeable) is not null) {
                    continue;
                }

                if (!_compilation.IsSymbolAccessibleWithin(symbol, _declaringClass.ContainingAssembly)) {
                    _owner.Report(ValidationDiagnostics.MemberNotReachableFromRegion, identifier,
                        symbol.Name, _declaringClass.Name);
                    continue;
                }

                // A generic fragment's members bind through the constraint interface, but the
                // emitted method's subject is the concrete type - so a member the target
                // implements explicitly is not reachable by name there, and would fail as CS1061
                // inside generated code.
                if (_subject?.Type is ITypeParameterSymbol &&
                    symbol.ContainingType is { TypeKind: TypeKind.Interface } &&
                    _target.FindImplementationForInterfaceMember(symbol) is { } implementation &&
                    Explicitly(implementation)) {
                    _owner.Report(ValidationDiagnostics.MemberNotReachableFromRegion, identifier,
                        $"{_target.Name}.{symbol.Name} (implemented explicitly)", _declaringClass.Name);
                }
            }
        }

        private static bool Explicitly(ISymbol implementation) => implementation switch {
            IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 } => true,
            IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 } => true,
            _ => false,
        };

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
        /// </remarks>
        private string? ConstantText(SyntaxNode identifier, IFieldSymbol field) {
            if (field.ConstantValue is not { } value) {
                return "null";
            }

            // An enum constant arrives as its underlying integral value, so the type is what makes
            // it read back as itself. A cast rather than a member name, because a value need not
            // correspond to any declared member - a [Flags] combination is an ordinary constant.
            if (_model.GetTypeInfo(identifier).Type is { TypeKind: TypeKind.Enum } enumType) {
                return _compilation.IsSymbolAccessibleWithin(enumType, _declaringClass.ContainingAssembly)
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
            uint number => $"{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}U",
            long number => $"{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}L",
            ulong number => $"{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}UL",
            float number => Floating(
                float.IsNaN(number), float.IsPositiveInfinity(number), float.IsNegativeInfinity(number),
                "float", number.ToString("G9", System.Globalization.CultureInfo.InvariantCulture), "F"),
            double number => Floating(
                double.IsNaN(number), double.IsPositiveInfinity(number), double.IsNegativeInfinity(number),
                "double", number.ToString("G17", System.Globalization.CultureInfo.InvariantCulture), "D"),

            // ToString round-trips a decimal exactly, scale included - 1.50m stays 1.50m rather
            // than collapsing to 1.5m, which is the same value but a different representation.
            decimal number => $"{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}m",
            _ => null,
        };

        /// <summary>
        /// A floating-point literal, or the named member for the three values that have no literal.
        /// The suffix is not decoration: G17 renders 10.0 as "10", which without it is an int.
        /// </summary>
        private static string Floating(
            bool nan, bool positiveInfinity, bool negativeInfinity,
            string type, string formatted, string suffix) {

            if (nan) {
                return $"{type}.NaN";
            }

            if (positiveInfinity) {
                return $"{type}.PositiveInfinity";
            }

            if (negativeInfinity) {
                return $"{type}.NegativeInfinity";
            }

            return formatted + suffix;
        }

        private void Line(int depth, string text) {
            if (text.Length == 0) {
                _lines.Add(string.Empty);
                return;
            }

            _lines.Add(new string(' ', depth * 4) + text);
        }

        /// <summary>
        /// The receiver of a trailing <c>Nullable&lt;T&gt;.Value</c> unwrap, or null when the
        /// expression is not one. The unwrap is never needed - every rule parameter is already
        /// nullable - and it is never harmless: it skews literal-type inference and puts
        /// <c>.value</c> on the wire path. See VM0093.
        /// </summary>
        private ExpressionSyntax? NullableUnwrapReceiver(ExpressionSyntax expression) =>
            expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Value" } member &&
            _model.GetSymbolInfo(member).Symbol is IPropertySymbol {
                ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
            }
                ? member.Expression
                : null;

        /// <summary>
        /// The member path a value argument reads off the subject, or null when it is not one.
        /// Conditional access is the nested-path spelling and reads through.
        /// </summary>
        private List<IPropertySymbol>? PathOf(ExpressionSyntax expression) {
            var segments = new List<IPropertySymbol>();
            var current = expression;

            while (true) {
                switch (current) {
                    case MemberAccessExpressionSyntax member:
                        if (_model.GetSymbolInfo(member).Symbol is not IPropertySymbol property) {
                            return null;
                        }

                        segments.Insert(0, property);
                        current = member.Expression;
                        continue;

                    case ConditionalAccessExpressionSyntax conditional: {
                        // x.Home?.PostalCode arrives as ConditionalAccess(x.Home, .PostalCode...).
                        var tail = CollectBindings(conditional.WhenNotNull);

                        if (tail is null) {
                            return null;
                        }

                        segments.InsertRange(0, tail);
                        current = conditional.Expression;
                        continue;
                    }

                    case IdentifierNameSyntax identifier:
                        return _subject is not null &&
                            SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(identifier).Symbol, _subject)
                            ? segments
                            : null;

                    default:
                        return null;
                }
            }
        }

        private List<IPropertySymbol>? CollectBindings(ExpressionSyntax whenNotNull) {
            var segments = new List<IPropertySymbol>();
            var current = whenNotNull;

            while (true) {
                switch (current) {
                    case MemberBindingExpressionSyntax binding:
                        if (_model.GetSymbolInfo(binding).Symbol is not IPropertySymbol bound) {
                            return null;
                        }

                        segments.Insert(0, bound);
                        return segments;

                    case MemberAccessExpressionSyntax member:
                        if (_model.GetSymbolInfo(member).Symbol is not IPropertySymbol property) {
                            return null;
                        }

                        segments.Insert(0, property);
                        current = member.Expression;
                        continue;

                    case ConditionalAccessExpressionSyntax nested: {
                        var tail = CollectBindings(nested.WhenNotNull);

                        if (tail is null) {
                            return null;
                        }

                        segments.InsertRange(0, tail);
                        current = nested.Expression;
                        continue;
                    }

                    default:
                        return null;
                }
            }
        }

        private string WirePathOf(List<IPropertySymbol> segments) =>
            string.Join(".", segments.Select(WireNameOf));

        /// <summary>
        /// Whether every segment of an island's value path can be named on the concrete target.
        /// Inside a generic fragment a member binds through the constraint interface, and one the
        /// target implements explicitly is not reachable by name in the emitted method - reported
        /// here instead of failing as CS1061 inside generated code.
        /// </summary>
        private bool PathIsReachable(List<IPropertySymbol> path, SyntaxNode site) {
            if (_subject?.Type is not ITypeParameterSymbol) {
                return true;
            }

            foreach (var segment in path) {
                if (segment.ContainingType is { TypeKind: TypeKind.Interface } &&
                    _target.FindImplementationForInterfaceMember(segment) is { } implementation &&
                    Explicitly(implementation)) {
                    _owner.Report(ValidationDiagnostics.MemberNotReachableFromRegion, site,
                        $"{_target.Name}.{segment.Name} (implemented explicitly)", _declaringClass.Name);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The wire name of one path segment, resolved against the concrete target. Inside a
        /// generic fragment a member binds through the constraint interface; the name the wire
        /// sees is the implementing property's - <c>[JsonPropertyName]</c> on the implementer
        /// wins, which is the point of stamping fragments out per concrete type.
        /// </summary>
        private string WireNameOf(IPropertySymbol property) {
            if (property.ContainingType is { TypeKind: TypeKind.Interface } &&
                !SymbolEqualityComparer.Default.Equals(property.ContainingType, _target) &&
                _target.FindImplementationForInterfaceMember(property) is IPropertySymbol implementer) {
                return _owner.WireNameOf(implementer);
            }

            return _owner.WireNameOf(property);
        }

        // ---- the island expansion --------------------------------------------------------------

        /// <summary>
        /// One chained statement's constraints, gathered then emitted: a failed Require suppresses
        /// the rest of its own chain through the shared <c>missing</c> local, exactly as the
        /// attribute region's else-if does.
        /// </summary>
        private sealed class IslandExpansion {
            private readonly RegionWriter _writer;
            private readonly int _depth;

            private ExpressionSyntax? _value;
            private string? _access;
            private ValidatedPropertyModel? _facts;
            private string? _field;
            private ConstraintModel? _required;
            private readonly List<ConstraintModel> _constraints = new();
            private readonly List<(bool Elements, ExpressionSyntax Value, string? Field)> _descents = new();

            public IslandExpansion(RegionWriter writer, int depth) {
                _writer = writer;
                _depth = depth;
            }

            public bool ReadCall(InvocationExpressionSyntax call, SyntaxNode report) {
                if (_writer._model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) {
                    // Beside the generic VM0070, name the frequent cause: a .Value unwrap on an
                    // argument. The arguments still bind on their own, so the unwrap is visible
                    // even though the invocation is not.
                    var unwrapReported = false;

                    foreach (var argument in call.ArgumentList.Arguments) {
                        if (_writer.NullableUnwrapReceiver(argument.Expression) is { } unwrapped &&
                            _writer.PathOf(unwrapped) is not null) {
                            _writer._owner.Report(ValidationDiagnostics.NullableValueUnwrapped,
                                argument.Expression, unwrapped.ToString());
                            unwrapReported = true;
                        }
                    }

                    // Require's object? catch-all binds the non-nullable spelling, so VM0090
                    // normally arrives through the bound path. This covers what still cannot
                    // bind - RequireAllowingEmpty is string-only, and exotic value shapes exist -
                    // so the answer is VM0090 there too, and alone: the unresolvable call is
                    // downstream of the same mistake. Not when the argument was a .Value unwrap -
                    // there the fix is dropping the unwrap, which VM0093 above already said.
                    if (!unwrapReported &&
                        call.Expression is MemberAccessExpressionSyntax {
                            Name.Identifier.Text: "Require" or "RequireAllowingEmpty",
                        } &&
                        call.ArgumentList.Arguments.Count > 0 &&
                        call.ArgumentList.Arguments[0].NameColon is null &&
                        call.ArgumentList.Arguments[0].Expression is { } required &&
                        _writer._model.GetTypeInfo(required).Type is { IsValueType: true } requiredType &&
                        requiredType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T &&
                        _writer.PathOf(required) is not null) {
                        _writer._owner.Report(
                            ValidationDiagnostics.RequireCannotFail, call, required.ToString());
                        return false;
                    }

                    _writer._owner.Report(ValidationDiagnostics.NotTranscribable, call,
                        _writer._declaringClass.Name, "an unresolvable call on the builder");
                    return false;
                }

                var name = method.Name;
                var arguments = _writer.MapArguments(
                    call, method.ReducedFrom ?? method, reducedForm: method.ReducedFrom is not null);

                if (name == "Apply") {
                    return ReadApply(call, arguments);
                }

                if (name == "As") {
                    return ReadFacet(call, method, arguments);
                }

                // The entry call carries the value; chained calls inherit its anchor.
                if (arguments.TryGetValue("value", out var value)) {
                    // A trailing .Value on a nullable member is corrected - the rule reads the
                    // member itself, so the path and the guard are the proven nullable shape -
                    // and reported, so the source stops disagreeing with what is generated.
                    if (_writer.NullableUnwrapReceiver(value) is { } unwrapped &&
                        _writer.PathOf(unwrapped) is not null) {
                        _writer._owner.Report(ValidationDiagnostics.NullableValueUnwrapped,
                            value, unwrapped.ToString());
                        value = unwrapped;
                    }

                    _value = value;

                    var path = _writer.PathOf(value);
                    var explicitField = FieldLiteral(arguments);

                    if (path is null && explicitField is null && name is not "Ensure") {
                        _writer._owner.Report(ValidationDiagnostics.SelectorNotAPath, call,
                            _writer._declaringClass.Name);
                        return false;
                    }

                    if (path is not null && !_writer.PathIsReachable(path, value)) {
                        return false;
                    }

                    _access = value.ToString();
                    _field = explicitField ?? (path is null ? null : _writer.WirePathOf(path));
                    _facts = FactsFor(value, path);
                }

                switch (name) {
                    case "For":
                        return true;

                    case "Require":
                    case "RequireAllowingEmpty":
                        if (_facts is { IsString: false, IsReferenceType: false, IsNullableValueType: false }) {
                            _writer._owner.Report(ValidationDiagnostics.RequireCannotFail, call,
                                _access ?? "the value");
                            return false;
                        }

                        _required = new ConstraintModel(
                            ConstraintKind.Required,
                            AllowEmptyStrings: name == "RequireAllowingEmpty",
                            Field: FieldLiteral(arguments));
                        return true;

                    case "Ensure":
                        return ReadEnsure(call, arguments);

                    case "Nested":
                        return ReadDescent(call, arguments, elements: false);

                    case "Each":
                        return ReadDescent(call, arguments, elements: true);

                    default: {
                        var constraint = ConstraintFor(name, arguments, call);

                        if (constraint is null) {
                            _writer._owner.Report(ValidationDiagnostics.NotTranscribable, report,
                                _writer._declaringClass.Name, $"a call to '{name}' the reader does not know");
                            return false;
                        }

                        _constraints.Add(constraint with { Field = FieldLiteral(arguments) });
                        return true;
                    }
                }
            }

            /// <summary>
            /// <c>rules.As&lt;TFacet&gt;(x)</c>: validate the subject as one of its facets. One
            /// spelling, two bindings - a facet generated in this compilation binds statically
            /// through a lazily-built validator cached on the companion; a facet from a referenced
            /// assembly resolves the closed <c>IValidatorFor&lt;TFacet&gt;</c> through the pass's
            /// services, and a missing registration throws naming the module to compose. The path
            /// does not push; suppression shares the collector as everywhere.
            /// </summary>
            private bool ReadFacet(
                InvocationExpressionSyntax call,
                IMethodSymbol method,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments) {

                if (!arguments.TryGetValue("value", out var subjectArgument) ||
                    subjectArgument is not IdentifierNameSyntax name ||
                    !SymbolEqualityComparer.Default.Equals(
                        _writer._model.GetSymbolInfo(name).Symbol, _writer._subject)) {
                    _writer._owner.Report(ValidationDiagnostics.RulesFlowNotFollowable, call,
                        "validate a facet of anything but the subject itself - a facet of a child is Nested's territory");
                    return false;
                }

                if (method.TypeArguments.Length != 1 ||
                    method.TypeArguments[0] is not INamedTypeSymbol facet) {
                    return false;
                }

                var subject = _writer._subject!.Name;
                var facetQualified = facet.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                if (SymbolEqualityComparer.Default.Equals(
                        facet.ContainingAssembly, _writer._compilation.Assembly)) {
                    if (!_writer.FacetHasRules(facet)) {
                        _writer._owner.Report(ValidationDiagnostics.FacetDeclaresNoRules, call, facet.Name);
                        return false;
                    }

                    var ns = facet.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : facet.ContainingNamespace.ToDisplayString() + ".";
                    var validator = $"global::{ns}{facet.Name}Validator";
                    var field = _writer.CompanionField(validator);

                    _writer.Line(_depth,
                        $"if (({field} ??= new {validator}()).Validate(ref ctx, {subject}).ShouldStop) {{");
                    _writer.Line(_depth + 1, $"return {Flow}.Stop;");
                    _writer.Line(_depth, "}");

                    return true;
                }

                // Statically closed: the facet type is written in source, so the service type is
                // closed at build time - no scanning, no naming protocol, no MakeGenericType. The
                // exception message can name the module because the generator knows the facet's
                // assembly and the Add{Assembly}Validators convention.
                var service = $"global::ValidationModules.IValidatorFor<{facetQualified}>";
                var local = $"facet{_writer._locals++}";
                var assembly = facet.ContainingAssembly.Name;
                var module = $"Add{ModuleIdentifier(assembly)}Validators";
                var message = SymbolDisplay.FormatLiteral(
                    $"No IValidatorFor<{facet.Name}> is registered. Compose the validators from " +
                    $"assembly '{assembly}' ({module}()).", quote: true);

                _writer.Line(_depth,
                    $"var {local} = ({service}?)ctx.Services?.GetService(typeof({service})) ?? " +
                    $"throw new global::System.InvalidOperationException({message});");
                _writer.Line(_depth, $"if ({local}.Validate(ref ctx, {subject}).ShouldStop) {{");
                _writer.Line(_depth + 1, $"return {Flow}.Stop;");
                _writer.Line(_depth, "}");

                return true;
            }

            /// <summary>The Add{X}Validators identifier for an assembly name, mirroring the
            /// registration emitter: namespace-sanitized, then through the shared
            /// <see cref="RegistrationNaming"/> so the message names the method that exists.</summary>
            private static string ModuleIdentifier(string assemblyName) {
                var builder = new System.Text.StringBuilder(assemblyName.Length);

                foreach (var part in assemblyName.Split('.')) {
                    if (part.Length == 0) {
                        continue;
                    }

                    if (builder.Length > 0) {
                        builder.Append('.');
                    }

                    if (!char.IsLetter(part[0]) && part[0] != '_') {
                        builder.Append('_');
                    }

                    foreach (var character in part) {
                        builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
                    }
                }

                return builder.Length == 0 ? "Generated" : RegistrationNaming.Identifier(builder.ToString());
            }

            private bool ReadDescent(
                InvocationExpressionSyntax call,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                bool elements) {

                if (_writer._insideFragment) {
                    _writer._owner.Report(ValidationDiagnostics.NotTranscribable, call,
                        _writer._declaringClass.Name,
                        "a descent inside a fragment - declare Nested and Each in the rules class body");
                    return false;
                }

                var value = arguments.TryGetValue("value", out var argument) ? argument : _value;

                if (value is null) {
                    return false;
                }

                _descents.Add((elements, value, FieldLiteral(arguments)));

                return true;
            }

            private bool ReadApply(
                InvocationExpressionSyntax call, IReadOnlyDictionary<string, ExpressionSyntax> arguments) {

                if (_depth > 0 || _writer._insideFragment) {
                    _writer._owner.Report(ValidationDiagnostics.NotTranscribable, call,
                        _writer._declaringClass.Name,
                        "Apply anywhere but the top of a Describe body - applied rules run last, unconditionally");
                    return false;
                }

                if (!arguments.TryGetValue("rule", out var rule) ||
                    _writer._model.GetSymbolInfo(rule).Symbol is not IMethodSymbol method) {
                    _writer._owner.Report(ValidationDiagnostics.NotTranscribable, call,
                        _writer._declaringClass.Name, "an Apply whose argument is not a method group");
                    return false;
                }

                _writer._applied.Add(
                    $"{method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{method.Name}");

                return true;
            }

            private bool ReadEnsure(
                InvocationExpressionSyntax call, IReadOnlyDictionary<string, ExpressionSyntax> arguments) {

                if (!arguments.TryGetValue("condition", out var condition)) {
                    return false;
                }

                var subject = _writer._subject?.Name ?? "x";
                var text = condition.ToString();
                var anchorName = RuleText.AnchorOfPredicate($"{subject} => {text}");
                var explicitField = FieldLiteral(arguments);

                var anchor = anchorName is null
                    ? null
                    : _writer._target.GetMembers(anchorName).OfType<IPropertySymbol>().FirstOrDefault();

                if (anchor is null && explicitField is null) {
                    _writer._owner.Report(ValidationDiagnostics.EnsureHasNoField, call,
                        _writer._declaringClass.Name);
                    return false;
                }

                var field = explicitField ?? _writer._owner.WireNameOf(anchor!);
                var explicitMessage = Literal(arguments, "message");
                var message = explicitMessage
                    ?? RuleText.RenderPredicate($"{subject} => {text}", _writer._owner._fieldNamer);

                // Derived from the condition rather than from `message`, so an author rewording
                // their own text does not move the wire code. The rule is the condition.
                var owner = _writer._owner;
                var derived = CodeNaming.Apply(
                    owner._codeNamespace, RuleText.CodeOfPredicate($"{subject} => {text}", owner._fieldNamer));
                var authored = CodeNaming.Apply(owner._codeNamespace, Literal(arguments, "code"));
                var code = authored is not null
                    ? Quote(authored)
                    : derived is null ? $"{Codes}.Predicate" : Quote(derived);

                // A derived code is the one part of a rules class that cannot be read off the
                // source, so it is stated at the site that owns it.
                if (authored is null && derived is not null) {
                    owner.Report(ValidationDiagnostics.EnsureCodeDerived, call, derived, message);
                }
                var severity = SeverityOf(arguments) is { } member ? $", {SeverityEnum}.{member}" : string.Empty;

                // Never null-guarded: the condition may read fields other than its anchor, so null
                // there is the author's, same as the attribute-region predicate. An explicit
                // message: is the author's text and reports as authored, so no language pack
                // replaces it; the derived wording belongs to the library and stays replaceable.
                var report = explicitMessage is null ? "Report" : "ReportAuthored";

                _writer.Line(_depth,
                    $"if (!({_writer.Rewrite(condition)}) && ctx.{report}({Quote(field)}, {code}, {Quote(message)}{severity}).ShouldStop) {{");
                _writer.Line(_depth + 1, $"return {Flow}.Stop;");
                _writer.Line(_depth, "}");

                return true;
            }

            public void Emit() {
                string? missing = null;

                if (_required is { } required && _access is { } access && _facts is { } facts) {
                    var test = ValidatorEmitter.RequiredTest(access, facts, required);
                    var field = Quote(required.Field ?? _field!);

                    if (_constraints.Count > 0 || _descents.Count > 0) {
                        missing = _writer.MissingLocal(facts.PropertyName);
                        _writer.Line(_depth, $"var {missing} = {test};");
                        test = missing;
                    }

                    _writer.Line(_depth,
                        $"if ({test} && {ContextExtensions}.ReportRequired(ctx, {field}).ShouldStop) {{");
                    _writer.Line(_depth + 1, $"return {Flow}.Stop;");
                    _writer.Line(_depth, "}");
                }

                foreach (var constraint in _constraints) {
                    if (_access is not { } anchored || _facts is not { } anchorFacts) {
                        continue;
                    }

                    var test = ValidatorEmitter.TestFor(
                        anchored, anchorFacts, constraint, new List<(string, ConstraintModel)>(),
                        new List<(string, ConstraintModel)>());

                    if (test is null) {
                        continue;
                    }

                    var reported = Quote(constraint.Field ?? _field!);
                    var report = ValidatorEmitter.ReportFor(reported, constraint, anchorFacts);

                    // The same conjunct shape the attribute region emits: the test is bracketed
                    // once anything precedes it, so a top-level || cannot silently widen the rule.
                    var condition = missing is null || constraint.Kind == ConstraintKind.Predicate
                        ? test
                        : $"!{missing} && ({test})";

                    _writer.Line(_depth, $"if ({ValidatorEmitter.Conjoin(condition, report)}) {{");
                    _writer.Line(_depth + 1, $"return {Flow}.Stop;");
                    _writer.Line(_depth, "}");
                }

                foreach (var (elements, value, field) in _descents) {
                    EmitDescent(elements, value, field, missing);
                }
            }

            private void EmitDescent(bool elements, ExpressionSyntax value, string? explicitField, string? missing) {
                var path = _writer.PathOf(value);

                if (path is null || path.Count != 1) {
                    // A descent pushes the property's own name as a path segment, so it needs a
                    // single-segment path; a facet of a child is its own Nested's territory.
                    _writer._owner.Report(ValidationDiagnostics.SelectorNotAPath, value,
                        _writer._declaringClass.Name);
                    return;
                }

                var property = path[0];
                var field = explicitField ?? _writer._owner.WireNameOf(property);
                var dependency = _writer.DependencyFor(property, elements, value);

                if (dependency is null) {
                    return;
                }

                var n = _writer._locals++;
                var access = value.ToString();
                var guard = missing is null ? string.Empty : $"!{missing} && ";

                if (elements) {
                    var items = $"items{n}";
                    var index = $"i{n}";

                    _writer.Line(_depth, $"if ({guard}{access} is {{ }} {items}) {{");
                    _writer.Line(_depth + 1, $"for (var {index} = 0; {index} < {items}.{dependency.CountAccessor}; {index}++) {{");
                    _writer.Line(_depth + 2, $"var element{n} = {items}[{index}];");
                    _writer.Line(_depth + 2, $"if (element{n} is not null) {{");
                    _writer.Line(_depth + 3, $"var elementCtx{n} = ctx.PushIndex({Quote(field)}, {index});");
                    _writer.Line(_depth + 3, $"for (var vi{n} = 0; vi{n} < {dependency.ParameterName}.Length; vi{n}++) {{");
                    _writer.Line(_depth + 4, $"if ({dependency.ParameterName}[vi{n}].Validate(ref elementCtx{n}, element{n}).ShouldStop) {{");
                    _writer.Line(_depth + 5, $"return {Flow}.Stop;");
                    _writer.Line(_depth + 4, "}");
                    _writer.Line(_depth + 3, "}");
                    _writer.Line(_depth + 2, "}");
                    _writer.Line(_depth + 1, "}");
                    _writer.Line(_depth, "}");
                } else {
                    _writer.Line(_depth, $"if ({guard}{access} is {{ }} nested{n}) {{");
                    _writer.Line(_depth + 1, $"var ctx{n} = ctx.Push({Quote(field)});");
                    _writer.Line(_depth + 1, $"for (var vi{n} = 0; vi{n} < {dependency.ParameterName}.Length; vi{n}++) {{");
                    _writer.Line(_depth + 2, $"if ({dependency.ParameterName}[vi{n}].Validate(ref ctx{n}, nested{n}).ShouldStop) {{");
                    _writer.Line(_depth + 3, $"return {Flow}.Stop;");
                    _writer.Line(_depth + 2, "}");
                    _writer.Line(_depth + 1, "}");
                    _writer.Line(_depth, "}");
                }
            }

            private ValidatedPropertyModel FactsFor(ExpressionSyntax value, List<IPropertySymbol>? path) {
                var type = _writer._model.GetTypeInfo(value).Type;
                var name = path is { Count: > 0 } ? path[path.Count - 1].Name : "Value";

                return new ValidatedPropertyModel(
                    name,
                    _field ?? name,
                    type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "global::System.Object",
                    PropertyShape.Scalar,
                    null,
                    null,
                    type?.IsReferenceType ?? true,
                    type?.SpecialType == SpecialType.System_String,
                    type is not null && TypeFacts.IsNullableValueType(type),
                    type is not null && TypeFacts.IsIndexable(type),
                    type is null ? "Count" : TypeFacts.CountAccessor(type),
                    false,
                    default);
            }

            private ConstraintModel? ConstraintFor(
                string name, IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                InvocationExpressionSyntax call) =>
                name switch {
                    "Length" => new ConstraintModel(
                        ConstraintKind.StringLength,
                        Min: Bound(arguments, "min", "0"),
                        Max: Bound(arguments, "max", int.MaxValue.ToString())),
                    "Count" => new ConstraintModel(
                        ConstraintKind.ItemCount,
                        Min: Bound(arguments, "min", "0"),
                        Max: Bound(arguments, "max", int.MaxValue.ToString())),
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
                    "MultipleOf" => new ConstraintModel(
                        ConstraintKind.MultipleOf,
                        Divisor: Bound(arguments, "divisor", "1"),
                        DecimalDomain: DivisorIsFloating(arguments)),
                    "Pattern" => PatternConstraint(arguments, call),
                    "AllowedValues" => AllowedValuesConstraint(arguments, call),
                    _ => null,
                };

            private bool DivisorIsFloating(IReadOnlyDictionary<string, ExpressionSyntax> arguments) =>
                arguments.TryGetValue("divisor", out var divisor) &&
                _writer._model.GetTypeInfo(divisor).Type?.SpecialType
                    is SpecialType.System_Double or SpecialType.System_Single;

            private ConstraintModel? PatternConstraint(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments, InvocationExpressionSyntax call) {

                if (!arguments.TryGetValue("pattern", out var accessor)) {
                    return null;
                }

                // The accessor is a method group for a [GeneratedRegex] partial method, so the
                // emitted form is that method invoked. No inline pattern can reach here at all.
                return _writer._model.GetSymbolInfo(accessor).Symbol is IMethodSymbol regex
                    ? new ConstraintModel(ConstraintKind.Pattern,
                        RegexAccessor: $"{regex.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{regex.Name}()")
                    : null;
            }

            private ConstraintModel AllowedValuesConstraint(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments, InvocationExpressionSyntax call) {

                var values = new List<string>();
                var displays = new List<string>();

                if (arguments.TryGetValue("allowed", out var allowed)) {
                    var elements = allowed switch {
                        CollectionExpressionSyntax collection => collection.Elements
                            .OfType<ExpressionElementSyntax>()
                            .Select(element => element.Expression),
                        ArrayCreationExpressionSyntax { Initializer: { } initializer } =>
                            initializer.Expressions.AsEnumerable(),
                        ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitly } =>
                            implicitly.Expressions.AsEnumerable(),
                        _ => Enumerable.Empty<ExpressionSyntax>(),
                    };

                    foreach (var element in elements) {
                        if (_writer._model.GetConstantValue(element) is { HasValue: true }) {
                            values.Add(_writer.Rewrite(element));
                            displays.Add(DisplayOf(element));
                        }
                    }
                }

                _ = call;

                return new ConstraintModel(
                    ConstraintKind.AllowedValues,
                    Values: new EquatableArray<string>(
                        System.Collections.Immutable.ImmutableArray.CreateRange(values)),
                    ValueDisplays: new EquatableArray<string>(
                        System.Collections.Immutable.ImmutableArray.CreateRange(displays)));
            }

            private string DisplayOf(ExpressionSyntax element) {
                var text = element.ToString();

                if (text.Length >= 2 && text[0] == '"') {
                    return text.Substring(1, text.Length - 2);
                }

                var dot = text.LastIndexOf('.');

                return dot >= 0 ? text.Substring(dot + 1) : text;
            }

            private string? Literal(IReadOnlyDictionary<string, ExpressionSyntax> arguments, string parameter) =>
                arguments.TryGetValue(parameter, out var expression) &&
                _writer._model.GetConstantValue(expression) is { HasValue: true, Value: string text }
                    ? text
                    : null;

            /// <summary>
            /// The <c>field:</c> argument. <c>nameof</c> through the subject names a member, and a
            /// member's wire name is the field namer's business - transcribed code already rewrites
            /// the same spelling (see <c>VisitInvocationExpression</c>), so without this one
            /// property could reach a client under two keys: <c>AccountNumber</c> from a
            /// <c>field:</c> and <c>accountNumber</c> from everything else. Any other constant
            /// stays exactly as written, because an explicit string is the author choosing the
            /// wire name.
            /// </summary>
            private string? FieldLiteral(IReadOnlyDictionary<string, ExpressionSyntax> arguments) {
                if (arguments.TryGetValue("field", out var expression) &&
                    expression is InvocationExpressionSyntax invocation &&
                    invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" } &&
                    invocation.ArgumentList.Arguments.Count == 1 &&
                    _writer.PathOf(invocation.ArgumentList.Arguments[0].Expression) is { Count: > 0 } path) {
                    return _writer.WirePathOf(path);
                }

                return Literal(arguments, "field");
            }

            private string? SeverityOf(IReadOnlyDictionary<string, ExpressionSyntax> arguments) {
                if (!arguments.TryGetValue("severity", out var expression)) {
                    return null;
                }

                return _writer._model.GetConstantValue(expression) is { HasValue: true, Value: int value }
                    ? value switch { 1 => "Warning", 2 => "Info", _ => null }
                    : null;
            }

            /// <summary>
            /// A bound as it lands in the check text - rewritten, not raw, so a bare reference to
            /// the rules class's own const qualifies or bakes exactly as it does anywhere else in
            /// the body.
            /// </summary>
            private string Bound(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments, string parameter, string fallback) =>
                arguments.TryGetValue(parameter, out var expression)
                    ? _writer.Rewrite(expression)
                    : fallback;

            private string? OptionalBound(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments, string parameter) =>
                arguments.TryGetValue(parameter, out var expression) && expression.ToString() != "null"
                    ? _writer.Rewrite(expression)
                    : null;

            private static string Quote(string text) => SymbolDisplay.FormatLiteral(text, quote: true);
        }

        private RegionDependency? DependencyFor(
            IPropertySymbol property, bool elements, ExpressionSyntax site) {

            foreach (var existing in _dependencies) {
                if (SymbolEqualityComparer.Default.Equals(existing.Property, property) &&
                    existing.Elements == elements) {
                    return existing;
                }
            }

            var elementType = elements
                ? TypeFacts.ElementTypeOf(property.Type)
                : Unwrap(property.Type);

            if (elementType is not INamedTypeSymbol named) {
                _owner.Report(ValidationDiagnostics.SelectorNotAPath, site, _declaringClass.Name);
                return null;
            }

            var camel = property.Name.Length == 0 || char.IsLower(property.Name[0])
                ? property.Name
                : char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);

            var dependency = new RegionDependency(
                property,
                elements,
                $"{camel}Validators",
                $"{property.Name}Validators",
                named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TypeFacts.CountAccessor(property.Type));

            _dependencies.Add(dependency);

            return dependency;
        }

        private static ITypeSymbol Unwrap(ITypeSymbol type) =>
            type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                ? nullable.TypeArguments[0]
                : type;

        /// <summary>
        /// The rewrites transcription needs, applied to original nodes so the semantic model still
        /// answers for them: <c>nameof</c> through the subject becomes the wire path,
        /// <c>rules.Context</c> becomes the live context, and a bare reference to the rules class's
        /// own statics is qualified - the companion is a different class, so the name has lost its
        /// scope (the lifted-predicate precedent).
        /// </summary>
        private sealed class TranscriptionRewriter : CSharpSyntaxRewriter {
            private readonly RegionWriter _writer;

            public TranscriptionRewriter(RegionWriter writer) => _writer = writer;

            public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
                if (node.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" } &&
                    node.ArgumentList.Arguments.Count == 1 &&
                    _writer.PathOf(node.ArgumentList.Arguments[0].Expression) is { Count: > 0 } path) {
                    return SyntaxFactory.ParseExpression(
                        SymbolDisplay.FormatLiteral(_writer.WirePathOf(path), quote: true));
                }

                return base.VisitInvocationExpression(node);
            }

            public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node) {
                if (node.Name.Identifier.Text == "Context" &&
                    node.Expression is IdentifierNameSyntax root &&
                    SymbolEqualityComparer.Default.Equals(
                        _writer._model.GetSymbolInfo(root).Symbol, _writer._builder)) {
                    return SyntaxFactory.IdentifierName("ctx");
                }

                return base.VisitMemberAccessExpression(node);
            }

            public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) {
                // The right-hand side of a member access is anchored by whatever precedes it; only
                // a bare name has lost its scope.
                if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node) {
                    return base.VisitIdentifierName(node);
                }

                if (_writer._model.GetSymbolInfo(node).Symbol is not { IsStatic: true } symbol ||
                    symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol) ||
                    symbol.ContainingType is not { } declaring ||
                    !DeclaredByTheClass(declaring)) {
                    return base.VisitIdentifierName(node);
                }

                // A private constant cannot be qualified, and does not need to be: C# bakes a
                // const at every use site, so the value is written back as a literal of its own
                // exact type. The accessibility walk already let it through on the same test.
                if (symbol is IFieldSymbol { HasConstantValue: true } constant &&
                    !_writer._compilation.IsSymbolAccessibleWithin(
                        constant, _writer._declaringClass.ContainingAssembly) &&
                    _writer.ConstantText(node, constant) is { } literal) {
                    return SyntaxFactory.ParseExpression(literal);
                }

                return SyntaxFactory.ParseExpression(
                    $"{_writer._declaringClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{node.Identifier.Text}");
            }

            private bool DeclaredByTheClass(INamedTypeSymbol declaring) {
                for (INamedTypeSymbol? current = _writer._declaringClass; current is not null; current = current.BaseType) {
                    if (SymbolEqualityComparer.Default.Equals(current, declaring)) {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}

/// <summary>Whether a rule descends, and into what. Retained for the model merge.</summary>
public enum Nesting {
    None,
    Object,
    Elements,
}

/// <summary>
/// A nested or element descent a region declares, which the validator must supply a validator
/// array for: the region method takes it as a parameter, the validator passes its own injected
/// set - so a separately registered validator for the nested type composes in a region exactly as
/// it does on an attribute descent.
/// </summary>
public sealed record RegionDependency(
    IPropertySymbol Property,
    bool Elements,
    string ParameterName,
    string AccessorName,
    string ElementQualifiedType,
    string CountAccessor);

/// <summary>Everything one rules class transcribed to, before the model merge.</summary>
public sealed record RulesDeclaration(
    INamedTypeSymbol Target,
    INamedTypeSymbol RulesClass,
    string SubjectParameterName,
    IReadOnlyList<string> BodyLines,
    IReadOnlyList<RegionDependency> Dependencies,
    IReadOnlyList<string> AppliedRules,
    IReadOnlyList<CompanionField> Fields);

/// <summary>A lazily-built facet validator a region caches, emitted as a nullable static field on
/// the companion class. The race on first use is benign - two threads build equivalent validators
/// and one wins, the same reasoning the validator's own nested arrays rely on.</summary>
public sealed record CompanionField(string TypeQualified, string Name);

/// <summary>
/// One fragment method: a static, void, same-compilation method that received the builder,
/// transcribed once per concrete target and emitted into its declaring type's container.
/// </summary>
public sealed class FragmentMethod {
    public FragmentMethod(
        string name,
        IMethodSymbol definition,
        INamedTypeSymbol target,
        IParameterSymbol? subject,
        string builderParameterName,
        IReadOnlyList<IParameterSymbol> extraParameters) {

        Name = name;
        Definition = definition;
        Target = target;
        Subject = subject;
        BuilderParameterName = builderParameterName;
        ExtraParameters = extraParameters;
    }

    public string Name { get; }

    public IMethodSymbol Definition { get; }

    /// <summary>The concrete type this instantiation validates - members resolve against it, so
    /// <c>[JsonPropertyName]</c> on an implementing property wins for field naming.</summary>
    public INamedTypeSymbol Target { get; }

    /// <summary>The parameter typed as the target, or null for a fragment that only computes and
    /// reports with explicit field names.</summary>
    public IParameterSymbol? Subject { get; }

    public string BuilderParameterName { get; }

    public IReadOnlyList<IParameterSymbol> ExtraParameters { get; }

    public List<string> BodyLines { get; } = new();

    public List<CompanionField> Fields { get; } = new();
}

/// <summary>
/// One region-declared descent as the attribute front end merges it: enough to make the validator
/// grow the injected-validator machinery for the property, with the walk itself owned by the
/// region. Constraints never travel this way any more - they expand in the region's own text.
/// </summary>
public sealed record DeclaredRule(
    IPropertySymbol? Property,
    string? Field,
    Models.ConstraintModel? Constraint,
    Nesting Nesting,
    string? Condition = null);

/// <summary>The fragment methods of one declaring type, emitted with that type's file usings.</summary>
public sealed class FragmentContainer {
    public FragmentContainer(string ns, string name, INamedTypeSymbol declaringType) {
        Namespace = ns;
        Name = name;
        DeclaringType = declaringType;
    }

    public string Namespace { get; }

    public string Name { get; }

    public INamedTypeSymbol DeclaringType { get; }

    public List<FragmentMethod> Methods { get; } = new();
}
