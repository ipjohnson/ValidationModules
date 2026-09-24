using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
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
/// follow (VM3002), and transcribed code must compile at the emission site (VM3004). The region is
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
public sealed class RulesFrontEnd
{
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
        string? codeNamespace = null,
        bool compileDataAnnotations = true
    )
    {
        _fieldNamer = fieldNamer;
        _rulesTarget = rulesTarget;
        _codeNamespace = codeNamespace;
        _compileDataAnnotations = compileDataAnnotations;
    }

    /// <summary>The assembly's code namespace, applied to what an Ensure authors or derives.</summary>
    private readonly string? _codeNamespace;

    /// <summary>
    /// Whether the DataAnnotations vocabulary produces rules, which decides whether a type carrying
    /// only DataAnnotations attributes has a validator a descent can call.
    /// </summary>
    private readonly bool _compileDataAnnotations;

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
    public IReadOnlyList<RulesDeclaration> Build(
        INamedTypeSymbol rulesClass,
        Compilation compilation
    )
    {
        List<RulesDeclaration>? declarations = null;

        // Regions merge into one companion class per rules class, so the cached-facet fields and
        // the hoisted message infos of every region must stay distinct; the seeds carry the counts
        // across writers.
        var fieldSeed = 0;
        var infoSeed = 0;

        foreach (var contract in rulesClass.AllInterfaces)
        {
            if (
                contract.ConstructedFrom.ToDisplayString() != KnownTypes.ValidationRulesForInterface
                || contract.TypeArguments.Length != 1
                || contract.TypeArguments[0] is not INamedTypeSymbol target
            )
            {
                continue;
            }

            if (
                contract.GetMembers("Describe").FirstOrDefault() is not IMethodSymbol declared
                || rulesClass.FindImplementationForInterfaceMember(declared)
                    is not IMethodSymbol describe
                || describe.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                    is not MethodDeclarationSyntax syntax
            )
            {
                continue;
            }

            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            var before = _diagnostics.Count;
            var writer = new RegionWriter(
                this,
                compilation,
                model,
                target,
                rulesClass,
                describe.Parameters[0],
                describe.Parameters[1],
                fieldSeed: fieldSeed,
                infoSeed: infoSeed
            );

            if (syntax.Body is { } block)
            {
                writer.ReadBlock(block.Statements, depth: 0);
            }
            else if (syntax.ExpressionBody is { } arrow)
            {
                // An expression-bodied Describe is one statement's worth of rules. The expression
                // is read where it stands - a synthesized statement node belongs to no syntax tree
                // and the first GetSymbolInfo against one throws, taking the whole generator's
                // output with it.
                writer.ReadExpressionStatement(
                    arrow.Expression,
                    depth: 0,
                    report: arrow.Expression
                );
            }

            if (FailedSince(before))
            {
                continue;
            }

            fieldSeed += writer.Fields.Count;
            infoSeed += writer.MessageInfos.Count;

            (declarations ??= new List<RulesDeclaration>()).Add(
                new RulesDeclaration(
                    target,
                    rulesClass,
                    describe.Parameters[1].Name,
                    writer.Body,
                    writer.Dependencies,
                    writer.AppliedRules,
                    writer.Fields,
                    writer.MessageInfos,
                    writer.Facets
                )
            );
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
    /// VM3103 states the code a rule derived and the rule is emitted regardless; counting it would
    /// silently drop the whole rules class for saying something true about it.
    /// </remarks>
    private bool FailedSince(int before)
    {
        for (var index = before; index < _diagnostics.Count; index++)
        {
            if (_diagnostics[index].Severity == DiagnosticSeverity.Error)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the wire name of one property: <c>[JsonPropertyName]</c>, otherwise the naming
    /// policy. The attribute front end applies the same ladder, so a rule written in a body and one
    /// written as an attribute name the field alike. <c>[Display(Name)]</c> labels messages and
    /// never names the field.
    /// </summary>
    internal string WireNameOf(IPropertySymbol property) =>
        AttributeFrontEnd.JsonNameOf(property) ?? _fieldNamer(property.Name);

    /// <summary>
    /// Registers one fragment instantiation, transcribing its body on first use, and returns the
    /// call target. Two rules classes calling the same fragment for the same target share one
    /// method.
    /// </summary>
    /// <remarks>
    /// An instantiation is keyed by every type argument, not only the target. A fragment with a
    /// second type parameter can be closed over one target twice, with a different argument for
    /// the second each time, and its extra parameters then have different types.
    /// </remarks>
    private FragmentMethod? FragmentFor(
        IMethodSymbol constructed,
        INamedTypeSymbol target,
        Compilation compilation,
        SyntaxNode site,
        List<IMethodSymbol> expanding
    )
    {
        var definition = constructed.OriginalDefinition;
        var key =
            $"{definition.ToDisplayString()}|{string.Join(",", constructed.TypeArguments.Select(static argument => argument.ToDisplayString()))}";

        // The stack check comes before the registry: a fragment registers itself before its body
        // is read so two callers share one method, and a cycle would otherwise hit that early
        // registration and emit mutually recursive methods that run forever at validation time.
        if (expanding.Any(open => SymbolEqualityComparer.Default.Equals(open, definition)))
        {
            Report(
                ValidationDiagnostics.FragmentCallCycle,
                site,
                string.Join(" -> ", expanding.Select(m => m.Name).Concat(new[] { definition.Name }))
            );
            return null;
        }

        if (_fragments.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (
            definition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                is not MethodDeclarationSyntax syntax
            || syntax.Body is null && syntax.ExpressionBody is null
        )
        {
            Report(
                ValidationDiagnostics.FragmentIsCompiledIl,
                site,
                $"{definition.ContainingType.Name}.{definition.Name}"
            );
            return null;
        }

        // Checked before the method is registered, so a type the container cannot name never
        // reaches a generated file.
        for (var i = 0; i < definition.TypeParameters.Length; i++)
        {
            if (Unnameable(constructed.TypeArguments[i], compilation) is { } reason)
            {
                Report(
                    ValidationDiagnostics.FragmentTypeArgumentNotNameable,
                    site,
                    $"{definition.ContainingType.Name}.{definition.Name}",
                    definition.TypeParameters[i].Name,
                    constructed.TypeArguments[i].ToDisplayString(),
                    reason
                );
                return null;
            }
        }

        // The fragment's own parameter roles, resolved on the constructed symbol so a generic
        // fragment's subject parameter is already typed as the concrete target.
        IParameterSymbol? builder = null;
        IParameterSymbol? subject = null;
        var extras = new List<IParameterSymbol>();

        foreach (var parameter in constructed.Parameters)
        {
            if (
                parameter.Type is INamedTypeSymbol named
                && named.ConstructedFrom.ToDisplayString() == KnownTypes.ValidationRulesBuilder
            )
            {
                builder = parameter;
            }
            else if (SymbolEqualityComparer.Default.Equals(parameter.Type, target))
            {
                subject ??= parameter;
            }
            else
            {
                extras.Add(parameter);
            }
        }

        if (builder is null)
        {
            return null;
        }

        var model = compilation.GetSemanticModel(syntax.SyntaxTree);
        var name = constructed.IsGenericMethod
            ? $"{definition.Name}_{target.Name}"
            : definition.Name;
        var container = ContainerFor(definition.ContainingType);

        // Distinct fragments can collide on the composed name - overloads, or two targets sharing
        // a simple name. A numeric suffix keeps the method callable; the container's readability
        // survives because the collision is rare.
        while (container.Methods.Any(m => m.Name == name))
        {
            name += "_";
        }

        var method = new FragmentMethod(name, definition, target, subject, builder.Name, extras);

        _fragments[key] = method;
        container.Methods.Add(method);

        var before = _diagnostics.Count;
        var writer = new RegionWriter(
            this,
            compilation,
            model,
            target,
            definition.ContainingType,
            // The fragment method reuses the fragment's own parameter names, so its body transcribes
            // with no identifier rewriting - the same property the region method has.
            builder: definition.Parameters[IndexOf(definition, builder)],
            subject: subject is null ? null : definition.Parameters[IndexOf(definition, subject)],
            expanding: expanding.Concat(new[] { definition }).ToList(),
            insideFragment: true,
            fieldPrefix: $"_{name}Facet",
            infoPrefix: $"_{name}Message",
            typeArguments: TypeArgumentsOf(definition, constructed)
        );

        if (syntax.Body is { } block)
        {
            writer.ReadBlock(block.Statements, depth: 0);
        }
        else if (syntax.ExpressionBody is { } arrow)
        {
            writer.ReadExpressionStatement(arrow.Expression, depth: 0, report: arrow.Expression);
        }

        method.Body.AddRange(writer.Body);
        method.Fields.AddRange(writer.Fields);
        method.MessageInfos.AddRange(writer.MessageInfos);
        method.Facets.AddRange(writer.Facets);

        return FailedSince(before) ? null : method;
    }

    /// <summary>
    /// Why a fragment container cannot name a type, or null when it can. An anonymous type has no
    /// name, and a private or protected type is out of the container's reach.
    /// </summary>
    private static string? Unnameable(ITypeSymbol type, Compilation compilation) =>
        type switch
        {
            { TypeKind: TypeKind.Error } => null,
            { IsAnonymousType: true } => ValidationDiagnostics.AnonymousTypeArgumentTail,
            IArrayTypeSymbol array => Unnameable(array.ElementType, compilation),
            INamedTypeSymbol named
                when !compilation.IsSymbolAccessibleWithin(
                    named.OriginalDefinition,
                    compilation.Assembly
                ) => ValidationDiagnostics.InaccessibleTypeArgumentTail(
                named.OriginalDefinition.ToDisplayString()
            ),
            INamedTypeSymbol named => (
                named.ContainingType is { } outer ? Unnameable(outer, compilation) : null
            )
                ?? named
                    .TypeArguments.Select(argument => Unnameable(argument, compilation))
                    .FirstOrDefault(reason => reason is not null),
            _ => null,
        };

    /// <summary>
    /// The concrete type each of a fragment's type parameters stands for in one instantiation.
    /// </summary>
    private static Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeArgumentsOf(
        IMethodSymbol definition,
        IMethodSymbol constructed
    )
    {
        var arguments = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(
            SymbolEqualityComparer.Default
        );

        for (var i = 0; i < definition.TypeParameters.Length; i++)
        {
            arguments[definition.TypeParameters[i]] = constructed.TypeArguments[i];
        }

        return arguments;
    }

    private static int IndexOf(IMethodSymbol definition, IParameterSymbol constructedParameter)
    {
        for (var i = 0; i < definition.Parameters.Length; i++)
        {
            if (definition.Parameters[i].Ordinal == constructedParameter.Ordinal)
            {
                return i;
            }
        }

        return constructedParameter.Ordinal;
    }

    private FragmentContainer ContainerFor(INamedTypeSymbol declaringType)
    {
        foreach (var container in _containers)
        {
            if (SymbolEqualityComparer.Default.Equals(container.DeclaringType, declaringType))
            {
                return container;
            }
        }

        var ns = declaringType.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : declaringType.ContainingNamespace.ToDisplayString();

        var created = new FragmentContainer(
            ns,
            GeneratedNames.FragmentContainer(declaringType),
            declaringType
        );

        _containers.Add(created);

        return created;
    }

    /// <summary>
    /// Walks one body - a Describe or a fragment - producing the region's statements.
    /// </summary>
    private sealed class RegionWriter
    {
        private const string Flow = "global::ValidationModules.ValidationFlow";
        private const string Codes = "global::ValidationModules.ValidationCodes";
        private const string SeverityEnum = "global::ValidationModules.ValidationSeverity";

        private readonly RulesFrontEnd _owner;
        private readonly Compilation _compilation;
        private readonly SemanticModel _model;
        private readonly INamedTypeSymbol _target;
        private readonly INamedTypeSymbol _declaringClass;
        private readonly IParameterSymbol _builder;
        private readonly IParameterSymbol? _subject;
        private readonly List<IMethodSymbol> _expanding;
        private readonly bool _insideFragment;

        /// <summary>
        /// The concrete type each of the enclosing fragment's type parameters stands for in this
        /// instantiation. Empty outside a generic fragment.
        /// </summary>
        private readonly IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> _typeArguments;

        private readonly List<RegionStatement> _body = new();

        /// <summary>The blocks opened and not yet closed, innermost on top.</summary>
        private readonly Stack<RegionBlock> _open = new();

        private readonly List<RegionDependency> _dependencies = new();
        private readonly List<string> _applied = new();
        private readonly List<CompanionField> _fields = new();
        private readonly List<INamedTypeSymbol> _facets = new();
        private readonly string _fieldPrefix;
        private readonly int _fieldSeed;

        /// <summary>
        /// The message infos this region hoists onto its companion, for the rules on a property
        /// with a <c>[Display(Name)]</c> label.
        /// </summary>
        private readonly ValidatorEmitter.MessageInfoPool _infos;

        /// <summary>One counter for every generated local, so expansions cannot collide with each
        /// other whatever the author named things.</summary>
        private int _locals;

        private readonly HashSet<string> _missingLocals = new(StringComparer.Ordinal);

        /// <summary>
        /// The local holding one chain's failed-Require result, named after the property the way
        /// the attribute region names it, with a counter only when a name repeats.
        /// </summary>
        private string MissingLocal(string propertyName)
        {
            var name = $"missing{propertyName}";

            while (!_missingLocals.Add(name))
            {
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
            int fieldSeed = 0,
            string infoPrefix = "_message",
            int infoSeed = 0,
            IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>? typeArguments = null
        )
        {
            _fieldPrefix = fieldPrefix;
            _fieldSeed = fieldSeed;
            _infos = new ValidatorEmitter.MessageInfoPool(infoPrefix, infoSeed);
            _owner = owner;
            _compilation = compilation;
            _model = model;
            _target = target;
            _declaringClass = declaringClass;
            _builder = builder;
            _subject = subject;
            _expanding = expanding ?? new List<IMethodSymbol>();
            _insideFragment = insideFragment;
            _typeArguments =
                typeArguments
                ?? new Dictionary<ITypeParameterSymbol, ITypeSymbol>(
                    SymbolEqualityComparer.Default
                );
        }

        public IReadOnlyList<RegionStatement> Body => _body;

        public IReadOnlyList<RegionDependency> Dependencies => _dependencies;

        public IReadOnlyList<string> AppliedRules => _applied;

        /// <summary>
        /// The facets this body validates the subject through with <c>As</c>, its fragments' as
        /// well. The subject's validator leaves their attribute declarations to the facet's own
        /// validator, which the <c>As</c> call runs.
        /// </summary>
        public IReadOnlyList<INamedTypeSymbol> Facets => _facets;

        private void AddFacet(INamedTypeSymbol facet)
        {
            if (!_facets.Contains(facet, SymbolEqualityComparer.Default))
            {
                _facets.Add(facet);
            }
        }

        /// <summary>The lazily-built facet validators this region caches, emitted as fields on the
        /// companion class.</summary>
        public IReadOnlyList<CompanionField> Fields => _fields;

        /// <summary>The message infos this region hoists, emitted as static fields on the
        /// companion class.</summary>
        public IReadOnlyList<(string Field, string Initializer)> MessageInfos => _infos.Fields;

        private string CompanionField(string typeQualified)
        {
            var field = new CompanionField(
                typeQualified,
                $"{_fieldPrefix}{_fieldSeed + _fields.Count}"
            );

            _fields.Add(field);

            return field.Name;
        }

        /// <summary>
        /// Whether anything in this compilation declares rules for a facet: the attribute on the
        /// facet itself, constraint attributes on its properties, or a rules class targeting it.
        /// An As over a facet with none would be a silent no-op, which is VM3105 instead.
        /// </summary>
        private bool FacetHasRules(INamedTypeSymbol facet)
        {
            if (
                facet
                    .GetAttributes()
                    .Any(attribute =>
                        attribute.AttributeClass?.ToDisplayString()
                        == KnownTypes.GenerateValidatorAttribute
                    )
            )
            {
                return true;
            }

            if (_owner._rulesTarget?.Invoke(facet) == true)
            {
                return true;
            }

            foreach (var property in facet.GetMembers().OfType<IPropertySymbol>())
            {
                foreach (var attribute in property.GetAttributes())
                {
                    var ns = attribute.AttributeClass?.ContainingNamespace?.ToDisplayString();

                    if (
                        ns == KnownTypes.ConstraintsNamespace
                        || ns == KnownTypes.DataAnnotationsNamespace
                    )
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a declaration of <paramref name="property"/> that the attribute front end reads
        /// carries <c>[ValidateNested]</c>: the target's own, or one the member walk merges in from
        /// an interface or an overridden base. The attribute region then already descends into it.
        /// </summary>
        private bool CarriesValidateNested(IPropertySymbol property)
        {
            foreach (
                var member in MemberWalk.PropertiesOf(
                    _target,
                    _compilation,
                    declaration =>
                        DescentTargets.CarriesConstraints(
                            declaration,
                            _owner._compileDataAnnotations
                        )
                )
            )
            {
                if (member.Property.Name == property.Name)
                {
                    return member.Sources.Any(DescentTargets.HasValidateNested);
                }
            }

            return false;
        }

        public void ReadBlock(
            IEnumerable<StatementSyntax> statements,
            int depth,
            bool inLoop = false,
            bool inSwitch = false
        )
        {
            foreach (var statement in statements)
            {
                ReadStatement(statement, depth, inLoop, inSwitch);
            }
        }

        private void ReadStatement(
            StatementSyntax statement,
            int depth,
            bool inLoop,
            bool inSwitch = false
        )
        {
            switch (statement)
            {
                case ExpressionStatementSyntax { Expression: { } expression }:
                    ReadExpressionStatement(expression, depth, statement, inLoop);
                    return;

                case LocalDeclarationStatementSyntax declaration:
                    if (declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                    {
                        _owner.Report(
                            ValidationDiagnostics.NotTranscribable,
                            statement,
                            _declaringClass.Name,
                            "a using declaration"
                        );
                        return;
                    }

                    Transcribe(declaration);
                    return;

                case IfStatementSyntax conditional:
                    ReadIf(conditional, depth, inLoop, inSwitch);
                    return;

                case SwitchStatementSyntax dispatch:
                    ReadSwitch(dispatch, depth, inLoop);
                    return;

                case ForStatementSyntax loop:
                    ReadLoop(
                        loop,
                        loop.Statement,
                        $"for ({(loop.Declaration is { } declared ? Rewrite(declared) : string.Join(", ", loop.Initializers.Select(Rewrite)))}; {RewriteOptional(loop.Condition)}; {string.Join(", ", loop.Incrementors.Select(Rewrite))})",
                        depth
                    );
                    return;

                case ForEachStatementSyntax each:
                    ReadLoop(
                        each,
                        each.Statement,
                        $"foreach ({Rewrite(each.Type)} {each.Identifier.Text} in {Rewrite(each.Expression)})",
                        depth
                    );
                    return;

                case WhileStatementSyntax spin:
                    ReadLoop(spin, spin.Statement, $"while ({Rewrite(spin.Condition)})", depth);
                    return;

                case DoStatementSyntax done:
                    Open("do");
                    ReadEmbedded(done.Statement, depth + 1, inLoop: true);
                    Close(footer: $"while ({Rewrite(done.Condition)});");
                    return;

                case ReturnStatementSyntax { Expression: null }:
                    // The region is a method, so an early return ends this rules class's checks and
                    // nothing else. Continue rather than Stop: the author is done, not failing.
                    Statement($"return {Flow}.Continue;");
                    return;

                case ReturnStatementSyntax:
                    _owner.Report(
                        ValidationDiagnostics.NotTranscribable,
                        statement,
                        _declaringClass.Name,
                        "a return with a value - Describe returns nothing"
                    );
                    return;

                case BlockSyntax nested:
                    Open(null);
                    ReadBlock(nested.Statements, depth + 1, inLoop, inSwitch);
                    Close();
                    return;

                case LocalFunctionStatementSyntax function:
                    GuardIslandsInside(function, "a local function");
                    Transcribe(function);
                    return;

                case BreakStatementSyntax when inLoop || inSwitch:
                case ContinueStatementSyntax when inLoop:
                    Transcribe(statement);
                    return;

                case ThrowStatementSyntax:
                    Transcribe(statement);
                    return;

                case EmptyStatementSyntax:
                    return;

                default:
                    // goto, unsafe, lock, try, fixed, yield, checked blocks - the v1-rejected
                    // exotica, admitted later if a real case appears.
                    _owner.Report(
                        ValidationDiagnostics.NotTranscribable,
                        statement,
                        _declaringClass.Name,
                        $"a {statement.Kind()}"
                    );
                    return;
            }
        }

        public void ReadExpressionStatement(
            ExpressionSyntax expression,
            int depth,
            SyntaxNode report,
            bool inLoop = false
        )
        {
            if (RootsAtBuilder(expression))
            {
                // rules.Context.… roots at the builder but is transcription, not an island: the
                // reporter tier is legal anywhere, loops included. Its flow-typed result lands in
                // the auto-wrap below.
                if (ReachesThroughContext(expression))
                {
                    var reported = _model.GetTypeInfo(expression).Type;

                    if (reported?.ToDisplayString() == "ValidationModules.ValidationFlow")
                    {
                        StopIf($"({Rewrite(expression)}).ShouldStop");
                    }
                    else
                    {
                        Statement($"{Rewrite(expression)};");
                    }

                    return;
                }

                if (inLoop)
                {
                    _owner.Report(
                        ValidationDiagnostics.IslandInUnreadableScope,
                        report,
                        _declaringClass.Name,
                        ValidationDiagnostics.IslandScopeTail
                    );
                    return;
                }

                ReadIsland(expression, depth, report);
                return;
            }

            if (IsFragmentCall(expression, out var fragmentCall, out var method))
            {
                if (inLoop)
                {
                    _owner.Report(
                        ValidationDiagnostics.IslandInUnreadableScope,
                        report,
                        _declaringClass.Name,
                        ValidationDiagnostics.IslandScopeTail
                    );
                    return;
                }

                ReadFragmentCall(fragmentCall!, method!);
                return;
            }

            // Mutating the subject from a validation body is the detectable half of the purity
            // line; the rest is convention.
            if (expression is AssignmentExpressionSyntax { Left: { } lhs } && Roots(lhs, _subject))
            {
                _owner.Report(
                    ValidationDiagnostics.NotTranscribable,
                    report,
                    _declaringClass.Name,
                    "an assignment to the subject - validation does not mutate its value"
                );
                return;
            }

            // Type-driven auto-flow-wrap: any expression-statement whose type is ValidationFlow is
            // checked and propagated. No method list to maintain - it covers every Report helper,
            // future ones, and user helpers returning a flow. Assigning the flow opts out.
            var type = _model.GetTypeInfo(expression).Type;

            if (type?.ToDisplayString() == "ValidationModules.ValidationFlow")
            {
                StopIf($"({Rewrite(expression)}).ShouldStop");
                return;
            }

            Statement($"{Rewrite(expression)};");
        }

        private void ReadIf(
            IfStatementSyntax conditional,
            int depth,
            bool inLoop,
            bool inSwitch = false
        )
        {
            Open($"if ({Rewrite(conditional.Condition)})");
            ReadEmbedded(conditional.Statement, depth + 1, inLoop, inSwitch);
            Close();

            var alternative = conditional.Else;

            while (alternative is not null)
            {
                if (alternative.Statement is IfStatementSyntax chained)
                {
                    Open($"else if ({Rewrite(chained.Condition)})");
                    ReadEmbedded(chained.Statement, depth + 1, inLoop, inSwitch);
                    Close();
                    alternative = chained.Else;
                }
                else
                {
                    Open("else");
                    ReadEmbedded(alternative.Statement, depth + 1, inLoop, inSwitch);
                    Close();
                    alternative = null;
                }
            }
        }

        private void ReadSwitch(SwitchStatementSyntax dispatch, int depth, bool inLoop)
        {
            Open($"switch ({Rewrite(dispatch.Expression)})");

            foreach (var section in dispatch.Sections)
            {
                Open(
                    string.Join("\n", section.Labels.Select(label => Rewrite(label).TrimEnd())),
                    braced: false
                );
                ReadBlock(section.Statements, depth + 2, inLoop, inSwitch: true);
                Close();
            }

            Close();
        }

        private void ReadLoop(StatementSyntax loop, StatementSyntax body, string header, int depth)
        {
            _ = loop;
            Open(header);
            ReadEmbedded(body, depth + 1, inLoop: true);
            Close();
        }

        private void ReadEmbedded(
            StatementSyntax statement,
            int depth,
            bool inLoop,
            bool inSwitch = false
        )
        {
            if (statement is BlockSyntax block)
            {
                ReadBlock(block.Statements, depth, inLoop, inSwitch);
            }
            else
            {
                ReadStatement(statement, depth, inLoop, inSwitch);
            }
        }

        // ---- islands ---------------------------------------------------------------------------

        /// <summary>
        /// The target's property called <paramref name="name"/>: declared on the target, on a base
        /// type, or on an interface the target extends.
        /// </summary>
        /// <remarks>
        /// <c>GetMembers</c> answers for declared members only, and a condition may read a property
        /// the subject inherits. The base chain comes first, so a class's own declaration wins over
        /// an interface's.
        /// </remarks>
        private IPropertySymbol? PropertyNamed(string name)
        {
            for (INamedTypeSymbol? type = _target; type is not null; type = type.BaseType)
            {
                if (
                    type.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault() is { } declared
                )
                {
                    return declared;
                }
            }

            foreach (var contract in _target.AllInterfaces)
            {
                if (
                    contract.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault() is
                    { } declared
                )
                {
                    return declared;
                }
            }

            return null;
        }

        /// <summary>Whether the expression is an invocation chain hanging off the builder parameter.</summary>
        private bool RootsAtBuilder(ExpressionSyntax expression)
        {
            var current = expression;

            while (true)
            {
                switch (current)
                {
                    case InvocationExpressionSyntax invocation:
                        current = invocation.Expression;
                        continue;

                    case MemberAccessExpressionSyntax member:
                        current = member.Expression;
                        continue;

                    case IdentifierNameSyntax identifier:
                        return SymbolEqualityComparer.Default.Equals(
                            _model.GetSymbolInfo(identifier).Symbol,
                            _builder
                        );

                    default:
                        return false;
                }
            }
        }

        private bool Roots(ExpressionSyntax expression, ISymbol? root)
        {
            if (root is null)
            {
                return false;
            }

            var current = expression;

            while (current is MemberAccessExpressionSyntax member)
            {
                current = member.Expression;
            }

            return current is IdentifierNameSyntax identifier
                && SymbolEqualityComparer.Default.Equals(
                    _model.GetSymbolInfo(identifier).Symbol,
                    root
                );
        }

        private void ReadIsland(ExpressionSyntax expression, int depth, SyntaxNode report)
        {
            var chain = new List<InvocationExpressionSyntax>();
            var current = expression;

            while (true)
            {
                if (current is InvocationExpressionSyntax invocation)
                {
                    chain.Add(invocation);
                    current = invocation.Expression;
                }
                else if (current is MemberAccessExpressionSyntax member)
                {
                    current = member.Expression;
                }
                else
                {
                    break;
                }
            }

            chain.Reverse();

            var expansion = new IslandExpansion(this, depth);

            foreach (var call in chain)
            {
                if (!expansion.ReadCall(call, report))
                {
                    return;
                }
            }

            expansion.Emit();
        }

        private bool ReachesThroughContext(ExpressionSyntax expression)
        {
            for (var current = expression; ; )
            {
                switch (current)
                {
                    case InvocationExpressionSyntax invocation:
                        current = invocation.Expression;
                        continue;

                    case MemberAccessExpressionSyntax member:
                        if (
                            member.Name.Identifier.Text == "Context"
                            && member.Expression is IdentifierNameSyntax root
                            && SymbolEqualityComparer.Default.Equals(
                                _model.GetSymbolInfo(root).Symbol,
                                _builder
                            )
                        )
                        {
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
            ExpressionSyntax expression,
            out InvocationExpressionSyntax? call,
            out IMethodSymbol? method
        )
        {
            call = null;
            method = null;

            if (
                expression is not InvocationExpressionSyntax invocation
                || _model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol candidate
            )
            {
                return false;
            }

            var resolved = Instantiated(
                candidate.ReducedFrom is { } reduced
                    ? reduced.Construct(candidate.TypeArguments.ToArray())
                    : candidate
            );

            if (!resolved.IsStatic || !resolved.ReturnsVoid)
            {
                return false;
            }

            // Receives the builder, in any position - the reduced extension receiver included.
            var receivesBuilder =
                (
                    candidate.ReducedFrom is not null
                    && invocation.Expression
                        is MemberAccessExpressionSyntax { Expression: { } receiver }
                    && Roots(receiver, _builder)
                )
                || invocation.ArgumentList.Arguments.Any(argument =>
                    argument.Expression is IdentifierNameSyntax name
                    && SymbolEqualityComparer.Default.Equals(
                        _model.GetSymbolInfo(name).Symbol,
                        _builder
                    )
                );

            if (!receivesBuilder)
            {
                return false;
            }

            if (
                !resolved.Parameters.Any(parameter =>
                    parameter.Type is INamedTypeSymbol named
                    && named.ConstructedFrom.ToDisplayString() == KnownTypes.ValidationRulesBuilder
                )
            )
            {
                return false;
            }

            call = invocation;
            method = resolved;

            return true;
        }

        /// <summary>
        /// A called method with this instantiation's concrete types put in for the enclosing
        /// fragment's type parameters.
        /// </summary>
        /// <remarks>
        /// Inside a generic fragment, a call to another generic fragment binds over the enclosing
        /// fragment's own type parameters, because they are all its body can name. Expanded as it
        /// stands, the inner fragment's subject is a type parameter rather than the target, so it
        /// is not recognised as the subject, its rules report VM3007, and its method is emitted
        /// with a parameter of type <c>T</c>. Closed over the concrete types the enclosing
        /// fragment was closed over, it is expanded exactly as if the rules class had called it.
        /// </remarks>
        private IMethodSymbol Instantiated(IMethodSymbol method)
        {
            if (_typeArguments.Count == 0 || !method.IsGenericMethod)
            {
                return method;
            }

            var arguments = new ITypeSymbol[method.TypeArguments.Length];
            var changed = false;

            for (var i = 0; i < arguments.Length; i++)
            {
                arguments[i] = Substituted(method.TypeArguments[i]);
                changed |= !SymbolEqualityComparer.Default.Equals(
                    arguments[i],
                    method.TypeArguments[i]
                );
            }

            return changed ? method.OriginalDefinition.Construct(arguments) : method;
        }

        private ITypeSymbol Substituted(ITypeSymbol type) =>
            type switch
            {
                ITypeParameterSymbol parameter
                    when _typeArguments.TryGetValue(parameter, out var concrete) => concrete,
                IArrayTypeSymbol array => _compilation.CreateArrayTypeSymbol(
                    Substituted(array.ElementType),
                    array.Rank
                ),
                INamedTypeSymbol { IsGenericType: true } named => named.ConstructedFrom.Construct(
                    named.TypeArguments.Select(Substituted).ToArray()
                ),
                _ => type,
            };

        /// <summary>
        /// The enclosing fragment's type parameter a name refers to, with the type it stands for
        /// in this expansion, or null when the name refers to anything else.
        /// </summary>
        private (ITypeParameterSymbol Parameter, ITypeSymbol Concrete)? TypeArgumentOf(
            IdentifierNameSyntax name
        ) =>
            _typeArguments.Count > 0
            && !name.IsVar
            && _model.GetSymbolInfo(name).Symbol is ITypeParameterSymbol parameter
            && _typeArguments.TryGetValue(parameter, out var concrete)
                ? (parameter, concrete)
                : null;

        private void ReadFragmentCall(InvocationExpressionSyntax call, IMethodSymbol method)
        {
            var fragment = _owner.FragmentFor(method, _target, _compilation, call, _expanding);

            if (fragment is null)
            {
                return;
            }

            foreach (var facet in fragment.Facets)
            {
                AddFacet(facet);
            }

            // The subject argument must be the subject parameter - a facet of a child is Nested's
            // territory, where the path pushes.
            var arguments = MapArguments(
                call,
                method,
                reducedForm: _model.GetSymbolInfo(call).Symbol
                    is IMethodSymbol { ReducedFrom: not null }
            );

            if (fragment.Subject is { } subjectParameter)
            {
                if (
                    !arguments.TryGetValue(subjectParameter.Name, out var subjectArgument)
                    || !(
                        subjectArgument is IdentifierNameSyntax name
                        && SymbolEqualityComparer.Default.Equals(
                            _model.GetSymbolInfo(name).Symbol,
                            _subject
                        )
                    )
                )
                {
                    _owner.Report(
                        ValidationDiagnostics.RulesFlowNotFollowable,
                        call,
                        $"the fragment's {_target.Name} parameter must be passed the Describe subject"
                    );
                    return;
                }
            }

            var rendered = new List<string> { "ref ctx" };

            if (fragment.Subject is not null)
            {
                rendered.Add(_subject!.Name);
            }

            foreach (var extra in fragment.ExtraParameters)
            {
                if (arguments.TryGetValue(extra.Name, out var argument))
                {
                    rendered.Add(Rewrite(argument));
                }
                else if (extra.HasExplicitDefaultValue)
                {
                    rendered.Add(FormatDefault(extra));
                }
                else
                {
                    rendered.Add("default");
                }
            }

            var ns = fragment.Definition.ContainingType.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : fragment.Definition.ContainingType.ContainingNamespace.ToDisplayString() + ".";

            StopIf(
                $"global::{ns}{GeneratedNames.FragmentContainer(fragment.Definition.ContainingType)}.{fragment.Name}({string.Join(", ", rendered)}).ShouldStop"
            );
        }

        private static string FormatDefault(IParameterSymbol parameter) =>
            parameter.ExplicitDefaultValue is null
                ? parameter.Type.IsReferenceType
                    ? "null"
                    : "default"
                : SymbolDisplay.FormatPrimitive(
                    parameter.ExplicitDefaultValue,
                    quoteStrings: true,
                    useHexadecimalNumbers: false
                ) ?? "default";

        /// <summary>
        /// Maps a call's arguments onto parameter names, so nothing downstream depends on position
        /// and a caller may pass <c>field:</c> or <c>max:</c> wherever they like.
        /// </summary>
        /// <param name="parameters">
        /// The unreduced parameter owner. A reduced extension call carries its receiver outside
        /// the argument list, so the first parameter is skipped to keep positions lined up.
        /// </param>
        private Dictionary<string, ExpressionSyntax> MapArguments(
            InvocationExpressionSyntax call,
            IMethodSymbol parameters,
            bool reducedForm
        )
        {
            var mapped = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
            var position = reducedForm ? 1 : 0;

            foreach (var argument in call.ArgumentList.Arguments)
            {
                if (argument.NameColon is { } named)
                {
                    mapped[named.Name.Identifier.Text] = argument.Expression;
                    continue;
                }

                if (position < parameters.Parameters.Length)
                {
                    mapped[parameters.Parameters[position].Name] = argument.Expression;
                }

                position++;
            }

            return mapped;
        }

        // ---- transcription ---------------------------------------------------------------------

        private void GuardIslandsInside(SyntaxNode scope, string what)
        {
            foreach (var node in scope.DescendantNodes())
            {
                if (
                    node is IdentifierNameSyntax identifier
                    && SymbolEqualityComparer.Default.Equals(
                        _model.GetSymbolInfo(identifier).Symbol,
                        _builder
                    )
                )
                {
                    _owner.Report(
                        ValidationDiagnostics.IslandInUnreadableScope,
                        identifier,
                        _declaringClass.Name,
                        IsContextAccess(identifier)
                            ? ValidationDiagnostics.ContextCaptureTail(what)
                            : ValidationDiagnostics.IslandScopeTail
                    );
                    return;
                }
            }
        }

        private void Transcribe(StatementSyntax statement) =>
            RegionSyntax.Add(Current, (StatementSyntax)Rewritten(statement));

        private string RewriteOptional(ExpressionSyntax? expression) =>
            expression is null ? string.Empty : Rewrite(expression);

        private string Rewrite(SyntaxNode node) =>
            Rewritten(node).NormalizeWhitespace("    ", "\n").ToFullString();

        private SyntaxNode Rewritten(SyntaxNode node)
        {
            GuardBuilderInside(node);
            CheckAccessibility(node);

            var rewriter = new TranscriptionRewriter(this);

            return rewriter.Visit(node);
        }

        /// <summary>
        /// Invariant 1: inside transcribed code the builder may appear only under
        /// <c>rules.Context</c>. Everywhere else it is a flow the reader cannot follow - stored,
        /// captured, returned, or passed somewhere unreadable - and would transcribe into a call on
        /// the inert surface that validates nothing.
        /// </summary>
        private void GuardBuilderInside(SyntaxNode node)
        {
            foreach (var identifier in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (
                    !SymbolEqualityComparer.Default.Equals(
                        _model.GetSymbolInfo(identifier).Symbol,
                        _builder
                    )
                )
                {
                    continue;
                }

                if (IsContextAccess(identifier))
                {
                    if (CapturingScope(identifier, node) is { } scope)
                    {
                        _owner.Report(
                            ValidationDiagnostics.IslandInUnreadableScope,
                            identifier.Parent!,
                            _declaringClass.Name,
                            ValidationDiagnostics.ContextCaptureTail(scope)
                        );
                    }

                    continue;
                }

                _owner.Report(
                    ValidationDiagnostics.RulesFlowNotFollowable,
                    identifier,
                    "store it, capture it, return it, or pass it to anything the generator cannot read"
                );
            }
        }

        /// <summary>Whether the builder identifier is the receiver of <c>rules.Context</c>.</summary>
        private static bool IsContextAccess(IdentifierNameSyntax identifier) =>
            identifier.Parent
                is MemberAccessExpressionSyntax { Name.Identifier.Text: "Context" } access
            && access.Expression == identifier;

        /// <summary>
        /// The scope between <paramref name="node"/> and the transcribed <paramref name="root"/>
        /// that would capture the context, as VM3003 names it, or null when there is none.
        /// </summary>
        /// <remarks>
        /// <c>rules.Context</c> becomes the region method's <c>ref</c> parameter, and C# does not
        /// let a lambda, an anonymous method, a local function or a query clause capture one. A
        /// local function statement that is itself the root has already been refused as a whole.
        /// </remarks>
        private static string? CapturingScope(SyntaxNode node, SyntaxNode root)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor == root)
                {
                    return null;
                }

                switch (ancestor)
                {
                    case AnonymousMethodExpressionSyntax:
                        return "an anonymous method";
                    case LambdaExpressionSyntax:
                        return "a lambda";
                    case LocalFunctionStatementSyntax:
                        return "a local function";
                    case QueryBodySyntax:
                        return "a query expression";
                }
            }

            return null;
        }

        /// <summary>
        /// Invariant 2: everything transcribed must compile in the companion file. The companion is
        /// internal to the same assembly, so what breaks is <c>private</c>/<c>protected</c> members
        /// of the rules class - caught here, with "make it internal", instead of surfacing inside
        /// generated code.
        /// </summary>
        private void CheckAccessibility(SyntaxNode node)
        {
            foreach (var identifier in node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            {
                var symbol = _model.GetSymbolInfo(identifier).Symbol;

                if (
                    symbol
                    is null
                        or ILocalSymbol
                        or IParameterSymbol
                        or IRangeVariableSymbol
                        or IDiscardSymbol
                        or ILabelSymbol
                )
                {
                    continue;
                }

                if (
                    symbol is IMethodSymbol
                    {
                        MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction
                    }
                )
                {
                    continue;
                }

                // A private constant is carried across by value instead - C# bakes a const at
                // every use site already, so the copy and the original are the same value by the
                // language's own rules. Everything else private is the diagnostic.
                if (
                    symbol is IFieldSymbol { HasConstantValue: true } bakeable
                    && ConstantText(identifier, bakeable) is not null
                )
                {
                    continue;
                }

                if (
                    !_compilation.IsSymbolAccessibleWithin(
                        symbol,
                        _declaringClass.ContainingAssembly
                    )
                )
                {
                    _owner.Report(
                        ValidationDiagnostics.MemberNotReachableFromRegion,
                        identifier,
                        symbol.Name,
                        _declaringClass.Name
                    );
                    continue;
                }

                // A generic fragment's members bind through the constraint interface, but the
                // emitted method's subject is the concrete type - so a member the target
                // implements explicitly is not reachable by name there, and would fail as CS1061
                // inside generated code.
                if (
                    _subject?.Type is ITypeParameterSymbol
                    && symbol.ContainingType is { TypeKind: TypeKind.Interface }
                    && _target.FindImplementationForInterfaceMember(symbol) is { } implementation
                    && Explicitly(implementation)
                )
                {
                    _owner.Report(
                        ValidationDiagnostics.MemberNotReachableFromRegion,
                        identifier,
                        $"{_target.Name}.{symbol.Name} (implemented explicitly)",
                        _declaringClass.Name
                    );
                }
            }
        }

        private static bool Explicitly(ISymbol implementation) =>
            implementation switch
            {
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
        private string? ConstantText(SyntaxNode identifier, IFieldSymbol field)
        {
            if (field.ConstantValue is not { } value)
            {
                return "null";
            }

            // An enum constant arrives as its underlying integral value, so the type is what makes
            // it read back as itself. A cast rather than a member name, because a value need not
            // correspond to any declared member - a [Flags] combination is an ordinary constant.
            if (_model.GetTypeInfo(identifier).Type is { TypeKind: TypeKind.Enum } enumType)
            {
                return
                    _compilation.IsSymbolAccessibleWithin(
                        enumType,
                        _declaringClass.ContainingAssembly
                    ) && Primitive(value) is { } underlying
                    ? $"({enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){underlying}"
                    : null;
            }

            return Primitive(value);
        }

        private static string? Primitive(object value) =>
            value switch
            {
                bool or string or char => SymbolDisplay.FormatPrimitive(
                    value,
                    quoteStrings: true,
                    useHexadecimalNumbers: false
                ),
                sbyte or byte or short or ushort or int => SymbolDisplay.FormatPrimitive(
                    value,
                    quoteStrings: false,
                    useHexadecimalNumbers: false
                ),
                uint number =>
                    $"{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}U",
                long number =>
                    $"{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}L",
                ulong number =>
                    $"{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}UL",
                float number => Floating(
                    float.IsNaN(number),
                    float.IsPositiveInfinity(number),
                    float.IsNegativeInfinity(number),
                    "float",
                    number.ToString("G9", System.Globalization.CultureInfo.InvariantCulture),
                    "F"
                ),
                double number => Floating(
                    double.IsNaN(number),
                    double.IsPositiveInfinity(number),
                    double.IsNegativeInfinity(number),
                    "double",
                    number.ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                    "D"
                ),

                // ToString round-trips a decimal exactly, scale included - 1.50m stays 1.50m rather
                // than collapsing to 1.5m, which is the same value but a different representation.
                decimal number =>
                    $"{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}m",
                _ => null,
            };

        /// <summary>
        /// A floating-point literal, or the named member for the three values that have no literal.
        /// The suffix is not decoration: G17 renders 10.0 as "10", which without it is an int.
        /// </summary>
        private static string Floating(
            bool nan,
            bool positiveInfinity,
            bool negativeInfinity,
            string type,
            string formatted,
            string suffix
        )
        {
            if (nan)
            {
                return $"{type}.NaN";
            }

            if (positiveInfinity)
            {
                return $"{type}.PositiveInfinity";
            }

            if (negativeInfinity)
            {
                return $"{type}.NegativeInfinity";
            }

            return formatted + suffix;
        }

        /// <summary>Where the next statement goes: the innermost open block, or the body.</summary>
        private List<RegionStatement> Current => _open.Count == 0 ? _body : _open.Peek().Body;

        private void Statement(string text) => Current.Add(new RegionCode(text));

        /// <summary>
        /// Adds a block under <paramref name="header"/>. The statements after it go into the block
        /// until <see cref="Close"/>.
        /// </summary>
        private void Open(string? header, bool braced = true)
        {
            var block = new RegionBlock(header, braced);

            Current.Add(block);
            _open.Push(block);
        }

        private void Close(string? footer = null) => _open.Pop().Footer = footer;

        /// <summary>The check every island and every flow-typed call ends in.</summary>
        private void StopIf(string condition)
        {
            Open($"if ({condition})");
            Statement($"return {Flow}.Stop;");
            Close();
        }

        /// <summary>
        /// The receiver of a trailing <c>Nullable&lt;T&gt;.Value</c> unwrap, or null when the
        /// expression is not one. The unwrap is never needed - every rule parameter is already
        /// nullable - and it is never harmless: it skews literal-type inference and puts
        /// <c>.value</c> on the wire path. See VM3104.
        /// </summary>
        private ExpressionSyntax? NullableUnwrapReceiver(ExpressionSyntax expression) =>
            expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Value" } member
            && _model.GetSymbolInfo(member).Symbol
                is IPropertySymbol
                {
                    ContainingType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                }
                ? member.Expression
                : null;

        /// <summary>
        /// The member path a value argument reads off the subject, or null when it is not one.
        /// Conditional access is the nested-path spelling and reads through.
        /// </summary>
        private List<IPropertySymbol>? PathOf(ExpressionSyntax expression)
        {
            var segments = new List<IPropertySymbol>();
            var current = expression;

            while (true)
            {
                switch (current)
                {
                    case MemberAccessExpressionSyntax member:
                        if (_model.GetSymbolInfo(member).Symbol is not IPropertySymbol property)
                        {
                            return null;
                        }

                        segments.Insert(0, property);
                        current = member.Expression;
                        continue;

                    case ConditionalAccessExpressionSyntax conditional:
                    {
                        // x.Home?.PostalCode arrives as ConditionalAccess(x.Home, .PostalCode...).
                        var tail = CollectBindings(conditional.WhenNotNull);

                        if (tail is null)
                        {
                            return null;
                        }

                        segments.InsertRange(0, tail);
                        current = conditional.Expression;
                        continue;
                    }

                    case IdentifierNameSyntax identifier:
                        return
                            _subject is not null
                            && SymbolEqualityComparer.Default.Equals(
                                _model.GetSymbolInfo(identifier).Symbol,
                                _subject
                            )
                            ? segments
                            : null;

                    default:
                        return null;
                }
            }
        }

        private List<IPropertySymbol>? CollectBindings(ExpressionSyntax whenNotNull)
        {
            var segments = new List<IPropertySymbol>();
            var current = whenNotNull;

            while (true)
            {
                switch (current)
                {
                    case MemberBindingExpressionSyntax binding:
                        if (_model.GetSymbolInfo(binding).Symbol is not IPropertySymbol bound)
                        {
                            return null;
                        }

                        segments.Insert(0, bound);
                        return segments;

                    case MemberAccessExpressionSyntax member:
                        if (_model.GetSymbolInfo(member).Symbol is not IPropertySymbol property)
                        {
                            return null;
                        }

                        segments.Insert(0, property);
                        current = member.Expression;
                        continue;

                    case ConditionalAccessExpressionSyntax nested:
                    {
                        var tail = CollectBindings(nested.WhenNotNull);

                        if (tail is null)
                        {
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
        /// Spells one member path an Ensure's message reads off the subject: each property of a
        /// model type under its field name, the way the error's own field is spelled. A member of
        /// a framework type such as <c>Length</c> or <c>Count</c>, a method, and whatever follows
        /// either stay as written. A first member that is not a property takes the policy.
        /// </summary>
        private IReadOnlyList<string> SpellMemberPath(IReadOnlyList<string> path)
        {
            var spelled = new List<string>(path.Count);
            INamedTypeSymbol? owner = _target;

            foreach (var identifier in path)
            {
                var property =
                    owner is not null && (spelled.Count == 0 || !IsFrameworkType(owner))
                        ? PropertyNamed(owner, identifier)
                        : null;

                if (property is not null)
                {
                    spelled.Add(WireNameOf(property));
                    owner = property.Type as INamedTypeSymbol;
                    continue;
                }

                spelled.Add(spelled.Count == 0 ? _owner._fieldNamer(identifier) : identifier);
                owner = null;
            }

            return spelled;
        }

        /// <summary>
        /// The instance property an identifier names on <paramref name="type"/>, its bases or its
        /// interfaces, or null.
        /// </summary>
        private static IPropertySymbol? PropertyNamed(INamedTypeSymbol type, string identifier)
        {
            var name =
                identifier.Length > 0 && identifier[0] == '@'
                    ? identifier.Substring(1)
                    : identifier;

            IPropertySymbol? Declared(ITypeSymbol candidate) =>
                candidate
                    .GetMembers(name)
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault(property => !property.IsStatic && !property.IsIndexer);

            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (Declared(current) is { } property)
                {
                    return property;
                }
            }

            foreach (var contract in type.AllInterfaces)
            {
                if (Declared(contract) is { } property)
                {
                    return property;
                }
            }

            return null;
        }

        /// <summary>Whether a type belongs to the <c>System</c> namespace or one inside it.</summary>
        private static bool IsFrameworkType(INamedTypeSymbol type)
        {
            var ns = type.ContainingNamespace;

            while (ns is { ContainingNamespace.IsGlobalNamespace: false })
            {
                ns = ns.ContainingNamespace;
            }

            return ns is { IsGlobalNamespace: false, Name: "System" };
        }

        /// <summary>
        /// Whether every segment of an island's value path can be named on the concrete target.
        /// Inside a generic fragment a member binds through the constraint interface, and one the
        /// target implements explicitly is not reachable by name in the emitted method - reported
        /// here instead of failing as CS1061 inside generated code.
        /// </summary>
        private bool PathIsReachable(List<IPropertySymbol> path, SyntaxNode site)
        {
            if (_subject?.Type is not ITypeParameterSymbol)
            {
                return true;
            }

            foreach (var segment in path)
            {
                if (
                    segment.ContainingType is { TypeKind: TypeKind.Interface }
                    && _target.FindImplementationForInterfaceMember(segment) is { } implementation
                    && Explicitly(implementation)
                )
                {
                    _owner.Report(
                        ValidationDiagnostics.MemberNotReachableFromRegion,
                        site,
                        $"{_target.Name}.{segment.Name} (implemented explicitly)",
                        _declaringClass.Name
                    );
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
        private string WireNameOf(IPropertySymbol property) =>
            _owner.WireNameOf(OnTarget(property));

        /// <summary>
        /// The <c>[Display(Name)]</c> label of one path segment, resolved against the concrete
        /// target for the reason <see cref="WireNameOf"/> is.
        /// </summary>
        private string? LabelOf(IPropertySymbol property) =>
            AttributeFrontEnd.DisplayLabelOf(OnTarget(property));

        /// <summary>
        /// The implementing property on the concrete target when <paramref name="property"/> binds
        /// through an interface the target implements, otherwise the property itself.
        /// </summary>
        private IPropertySymbol OnTarget(IPropertySymbol property) =>
            property.ContainingType is { TypeKind: TypeKind.Interface }
            && !SymbolEqualityComparer.Default.Equals(property.ContainingType, _target)
            && _target.FindImplementationForInterfaceMember(property) is IPropertySymbol implementer
                ? implementer
                : property;

        // ---- the island expansion --------------------------------------------------------------

        /// <summary>
        /// One chained statement's constraints, gathered then emitted: a failed Require suppresses
        /// the rest of its own chain through the shared <c>missing</c> local, exactly as the
        /// attribute region's else-if does.
        /// </summary>
        private sealed class IslandExpansion
        {
            private readonly RegionWriter _writer;
            private readonly int _depth;

            private ExpressionSyntax? _value;
            private string? _access;
            private ValidatedPropertyModel? _facts;
            private string? _field;
            private ConstraintModel? _required;
            private readonly List<ConstraintModel> _constraints = new();
            private readonly List<(
                bool Elements,
                ExpressionSyntax Value,
                string? Field,
                SyntaxNode Site
            )> _descents = new();

            /// <summary>
            /// Set by an <c>Each()</c> whose anchor is a collection of strings: everything chained
            /// after it constrains the elements, emitted as an indexed loop rather than a descent.
            /// </summary>
            private bool _perElement;
            private readonly List<ConstraintModel> _elementConstraints = new();

            public IslandExpansion(RegionWriter writer, int depth)
            {
                _writer = writer;
                _depth = depth;
            }

            public bool ReadCall(InvocationExpressionSyntax call, SyntaxNode report)
            {
                if (_writer._model.GetSymbolInfo(call).Symbol is not IMethodSymbol method)
                {
                    // Beside the generic VM3001, name the frequent cause: a .Value unwrap on an
                    // argument. The arguments still bind on their own, so the unwrap is visible
                    // even though the invocation is not.
                    var unwrapReported = false;

                    foreach (var argument in call.ArgumentList.Arguments)
                    {
                        if (
                            _writer.NullableUnwrapReceiver(argument.Expression) is { } unwrapped
                            && _writer.PathOf(unwrapped) is not null
                        )
                        {
                            _writer._owner.Report(
                                ValidationDiagnostics.NullableValueUnwrapped,
                                argument.Expression,
                                unwrapped.ToString()
                            );
                            unwrapReported = true;
                        }
                    }

                    // Require's object? catch-all binds the non-nullable spelling, so VM3101
                    // normally arrives through the bound path. This covers what still cannot
                    // bind - RequireAllowingEmpty is string-only, and exotic value shapes exist -
                    // so the answer is VM3101 there too, and alone: the unresolvable call is
                    // downstream of the same mistake. Not when the argument was a .Value unwrap -
                    // there the fix is dropping the unwrap, which VM3104 above already said.
                    if (
                        !unwrapReported
                        && call.Expression
                            is MemberAccessExpressionSyntax
                            {
                                Name.Identifier.Text: "Require" or "RequireAllowingEmpty",
                            }
                        && call.ArgumentList.Arguments.Count > 0
                        && call.ArgumentList.Arguments[0].NameColon is null
                        && call.ArgumentList.Arguments[0].Expression is { } required
                        && _writer._model.GetTypeInfo(required).Type
                            is { IsValueType: true } requiredType
                        && requiredType.OriginalDefinition.SpecialType
                            != SpecialType.System_Nullable_T
                        && _writer.PathOf(required) is not null
                    )
                    {
                        _writer._owner.Report(
                            ValidationDiagnostics.RequireCannotFail,
                            call,
                            required.ToString()
                        );
                        return false;
                    }

                    _writer._owner.Report(
                        ValidationDiagnostics.NotTranscribable,
                        call,
                        _writer._declaringClass.Name,
                        "an unresolvable call on the builder"
                    );
                    return false;
                }

                var name = method.Name;
                var arguments = _writer.MapArguments(
                    call,
                    method.ReducedFrom ?? method,
                    reducedForm: method.ReducedFrom is not null
                );

                if (name == "Apply")
                {
                    return ReadApply(call, arguments);
                }

                if (name == "As")
                {
                    return ReadFacet(call, method, arguments);
                }

                // The entry call carries the value; chained calls inherit its anchor.
                if (arguments.TryGetValue("value", out var value))
                {
                    // A trailing .Value on a nullable member is corrected - the rule reads the
                    // member itself, so the path and the guard are the proven nullable shape -
                    // and reported, so the source stops disagreeing with what is generated.
                    if (
                        _writer.NullableUnwrapReceiver(value) is { } unwrapped
                        && _writer.PathOf(unwrapped) is not null
                    )
                    {
                        _writer._owner.Report(
                            ValidationDiagnostics.NullableValueUnwrapped,
                            value,
                            unwrapped.ToString()
                        );
                        value = unwrapped;
                    }

                    _value = value;

                    var path = _writer.PathOf(value);
                    var explicitField = FieldLiteral(arguments);

                    if (path is null && explicitField is null && name is not "Ensure")
                    {
                        _writer._owner.Report(
                            ValidationDiagnostics.SelectorNotAPath,
                            call,
                            _writer._declaringClass.Name
                        );
                        return false;
                    }

                    if (path is not null && !_writer.PathIsReachable(path, value))
                    {
                        return false;
                    }

                    _access = value.ToString();
                    _field = explicitField ?? (path is null ? null : _writer.WirePathOf(path));
                    _facts = FactsFor(value, path, labelled: explicitField is null);
                }

                switch (name)
                {
                    case "For":
                        return true;

                    case "Require":
                    case "RequireAllowingEmpty":
                        if (_perElement)
                        {
                            // Null elements are skipped, like a nested walk's, so a per-element
                            // presence rule has nothing left to say that Length(1, …) does not.
                            _writer._owner.Report(
                                ValidationDiagnostics.NotTranscribable,
                                call,
                                _writer._declaringClass.Name,
                                "Require chained after element rules - null elements are "
                                    + "skipped, and Length(1, ...) already rejects the empty"
                            );
                            return false;
                        }

                        if (
                            _facts is
                            { IsString: false, IsReferenceType: false, IsNullableValueType: false }
                        )
                        {
                            _writer._owner.Report(
                                ValidationDiagnostics.RequireCannotFail,
                                call,
                                _access ?? "the value"
                            );
                            return false;
                        }

                        _required = new ConstraintModel(
                            ConstraintKind.Required,
                            AllowEmptyStrings: name == "RequireAllowingEmpty",
                            Field: FieldLiteral(arguments)
                        );
                        return true;

                    case "Ensure":
                        return ReadEnsure(call, arguments);

                    case "MultipleOf":
                        return ReadMultipleOf(call, method, arguments);

                    // One descent per chain. An Each over objects leaves the collection as the
                    // chain's anchor, so a descent chained after it would walk the elements a second
                    // time, or walk the collection as if it were one object.
                    case "Nested":
                        if (_perElement || DescendedIntoElements)
                        {
                            _writer._owner.Report(
                                ValidationDiagnostics.NotTranscribable,
                                call,
                                _writer._declaringClass.Name,
                                "a descent chained after element rules"
                            );
                            return false;
                        }

                        if (_descents.Count > 0)
                        {
                            _writer._owner.Report(
                                ValidationDiagnostics.NotTranscribable,
                                call,
                                _writer._declaringClass.Name,
                                "a descent chained after another descent"
                            );
                            return false;
                        }

                        return ReadDescent(call, arguments, elements: false);

                    case "Each":
                        if (_perElement || DescendedIntoElements)
                        {
                            _writer._owner.Report(
                                ValidationDiagnostics.NotTranscribable,
                                call,
                                _writer._declaringClass.Name,
                                "a second Each chained after element rules"
                            );
                            return false;
                        }

                        if (_descents.Count > 0)
                        {
                            _writer._owner.Report(
                                ValidationDiagnostics.NotTranscribable,
                                call,
                                _writer._declaringClass.Name,
                                "a descent chained after another descent"
                            );
                            return false;
                        }

                        // The string-element overloads return the element anchor rather than the
                        // collection's, and that return type is the reliable tell: everything
                        // chained after them constrains the elements, in an indexed loop. An
                        // object element descends into its own validator as before.
                        if (
                            method.ReturnType is INamedTypeSymbol { TypeArguments.Length: 2 } anchor
                            && anchor.TypeArguments[1].SpecialType == SpecialType.System_String
                        )
                        {
                            _perElement = true;
                            return true;
                        }

                        return ReadDescent(call, arguments, elements: true);

                    case "AllowedValues":
                        return ReadAllowedValues(call, arguments);

                    default:
                    {
                        var reported = _writer._owner._diagnostics.Count;
                        var constraint = ConstraintFor(name, arguments, call);

                        if (constraint is null)
                        {
                            // A reader that already said what is wrong with the call has said
                            // enough; a VM3001 beside it would only repeat that it was refused.
                            if (_writer._owner._diagnostics.Count == reported)
                            {
                                _writer._owner.Report(
                                    ValidationDiagnostics.NotTranscribable,
                                    report,
                                    _writer._declaringClass.Name,
                                    $"a call to '{name}' the reader does not know"
                                );
                            }

                            return false;
                        }

                        // An element rule carries no field override: its path is the collection's
                        // wire name, indexed per element at the emission site.
                        if (_perElement)
                        {
                            _elementConstraints.Add(constraint);
                            return true;
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
            /// assembly resolves every registered <c>IValidatorFor&lt;TFacet&gt;</c> through the
            /// pass's services and runs them in registration order, and none registered throws
            /// naming the module to compose. The path does not push; suppression shares the
            /// collector as everywhere.
            /// </summary>
            private bool ReadFacet(
                InvocationExpressionSyntax call,
                IMethodSymbol method,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments
            )
            {
                if (
                    !arguments.TryGetValue("value", out var subjectArgument)
                    || subjectArgument is not IdentifierNameSyntax name
                    || !SymbolEqualityComparer.Default.Equals(
                        _writer._model.GetSymbolInfo(name).Symbol,
                        _writer._subject
                    )
                )
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.RulesFlowNotFollowable,
                        call,
                        "validate a facet of anything but the subject itself - a facet of a child is Nested's territory"
                    );
                    return false;
                }

                // Substituted first, so a generic fragment's As<T> over its own subject parameter is
                // compared as the type it was expanded for.
                if (
                    method.TypeArguments.Length != 1
                    || _writer.Substituted(method.TypeArguments[0]) is not INamedTypeSymbol facet
                )
                {
                    return false;
                }

                if (SymbolEqualityComparer.Default.Equals(facet, _writer._target))
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.FacetIsTheSubjectType,
                        call,
                        facet.Name
                    );
                    return false;
                }

                var subject = _writer._subject!.Name;
                var facetQualified = facet.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                );

                // Recorded whichever way it binds: a facet from a referenced assembly resolves a
                // validator generated over there, which checks the same declarations.
                _writer.AddFacet(facet);

                if (
                    SymbolEqualityComparer.Default.Equals(
                        facet.ContainingAssembly,
                        _writer._compilation.Assembly
                    )
                )
                {
                    if (!_writer.FacetHasRules(facet))
                    {
                        _writer._owner.Report(
                            ValidationDiagnostics.FacetDeclaresNoRules,
                            call,
                            facet.Name
                        );
                        return false;
                    }

                    var ns = facet.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : facet.ContainingNamespace.ToDisplayString() + ".";
                    var validator = $"global::{ns}{GeneratedNames.Validator(facet)}";
                    var field = _writer.CompanionField(validator);

                    _writer.StopIf(
                        $"({field} ??= new {validator}()).Validate(ref ctx, {subject}).ShouldStop"
                    );

                    return true;
                }

                // Statically closed: the facet type is written in source, so the service type is
                // closed at build time - no scanning, no naming protocol, no MakeGenericType. The
                // exception message can name the module because the generator knows the facet's
                // assembly and the Add{Assembly}Validators convention.
                var service = $"global::ValidationModules.IValidatorFor<{facetQualified}>";
                var registered = $"global::System.Collections.Generic.IEnumerable<{service}>";
                var n = _writer._locals++;
                var local = $"facet{n}";
                var assembly = facet.ContainingAssembly.Name;
                var module = $"Add{ModuleIdentifier(assembly)}Validators";
                var message = SymbolDisplay.FormatLiteral(
                    $"No IValidatorFor<{facet.Name}> is registered. Compose the validators from "
                        + $"assembly '{assembly}' ({module}()).",
                    quote: true
                );

                // Every registration, in registration order, as ValidationRunner<T> composes them.
                // The array the container returns is used as it is rather than copied.
                _writer.Statement(
                    $"var {local}Registered = ctx.Services?.GetService(typeof({registered})) as "
                        + $"{registered} ?? global::System.Array.Empty<{service}>();"
                );
                _writer.Statement(
                    $"var {local} = {local}Registered as {service}[] ?? "
                        + $"global::System.Linq.Enumerable.ToArray({local}Registered);"
                );
                _writer.Open($"if ({local}.Length == 0)");
                _writer.Statement(
                    $"throw new global::System.InvalidOperationException({message});"
                );
                _writer.Close();
                _writer.Open($"for (var vi{n} = 0; vi{n} < {local}.Length; vi{n}++)");

                // An ordinary context rather than ctx: the container may hand back a hand-written
                // validator, whose nameof(...) fields the pass's namer is there to spell. A fresh
                // one per validator, as the runner gives each of its validators.
                _writer.Statement($"var {local}Context = ctx.WithResolvedFieldNames(false);");
                _writer.StopIf(
                    $"{local}[vi{n}].Validate(ref {local}Context, {subject}).ShouldStop"
                );
                _writer.Close();

                return true;
            }

            /// <summary>The Add{X}Validators identifier for an assembly name, mirroring the
            /// registration emitter: namespace-sanitized, then through the shared
            /// <see cref="RegistrationNaming"/> so the message names the method that exists.</summary>
            private static string ModuleIdentifier(string assemblyName)
            {
                var builder = new System.Text.StringBuilder(assemblyName.Length);

                foreach (var part in assemblyName.Split('.'))
                {
                    if (part.Length == 0)
                    {
                        continue;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append('.');
                    }

                    if (!char.IsLetter(part[0]) && part[0] != '_')
                    {
                        builder.Append('_');
                    }

                    foreach (var character in part)
                    {
                        builder.Append(
                            char.IsLetterOrDigit(character) || character == '_' ? character : '_'
                        );
                    }
                }

                return builder.Length == 0
                    ? "Generated"
                    : RegistrationNaming.Identifier(builder.ToString());
            }

            private bool ReadDescent(
                InvocationExpressionSyntax call,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                bool elements
            )
            {
                if (_writer._insideFragment)
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.NotTranscribable,
                        call,
                        _writer._declaringClass.Name,
                        "a descent inside a fragment - declare Nested and Each in the rules class body"
                    );
                    return false;
                }

                var value = arguments.TryGetValue("value", out var argument) ? argument : _value;

                if (value is null)
                {
                    return false;
                }

                // Nested reaches one object. A collection's elements are Each's descent, and a
                // dictionary's values are reached only by [ValidateNested], which walks them by key.
                if (!elements && _writer._model.GetTypeInfo(value).Type is { } type)
                {
                    var misuse =
                        TypeFacts.DictionaryTypesOf(type) is not null
                            ? $"Nested over the dictionary '{value}' ([ValidateNested] on the property validates each value)"
                        : TypeFacts.ElementTypeOf(type) is not null
                            ? $"Nested over the collection '{value}' (rules.Each({value}) validates each element)"
                        : null;

                    if (misuse is not null)
                    {
                        _writer._owner.Report(
                            ValidationDiagnostics.NotTranscribable,
                            call,
                            _writer._declaringClass.Name,
                            misuse
                        );
                        return false;
                    }
                }

                _descents.Add((elements, value, FieldLiteral(arguments), call));

                return true;
            }

            /// <summary>Whether an Each over objects already descended in this chain.</summary>
            private bool DescendedIntoElements => _descents.Any(descent => descent.Elements);

            private bool ReadApply(
                InvocationExpressionSyntax call,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments
            )
            {
                if (_depth > 0 || _writer._insideFragment)
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.NotTranscribable,
                        call,
                        _writer._declaringClass.Name,
                        "Apply anywhere but the top of a Describe body - applied rules run last, unconditionally"
                    );
                    return false;
                }

                if (
                    CalledMethod(
                        arguments.TryGetValue("rule", out var rule) ? rule : call,
                        "Apply",
                        "Move the rule into a static method that takes (ref ValidationContext, "
                            + $"{_writer._target.Name}) and returns ValidationFlow, and pass the "
                            + $"method by name: {_writer._builder.Name}.Apply(Check)"
                    )
                    is not { } method
                )
                {
                    return false;
                }

                _writer._applied.Add(
                    $"{method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{method.Name}"
                );

                return true;
            }

            private bool ReadEnsure(
                InvocationExpressionSyntax call,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments
            )
            {
                if (!arguments.TryGetValue("condition", out var condition))
                {
                    return false;
                }

                var subject = _writer._subject?.Name ?? "x";
                var text = condition.ToString();
                var anchorName = RuleText.AnchorOfPredicate($"{subject} => {text}");
                var explicitField = FieldLiteral(arguments);

                var anchor = anchorName is null ? null : _writer.PropertyNamed(anchorName);

                if (anchor is null && explicitField is null)
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.EnsureHasNoField,
                        call,
                        _writer._declaringClass.Name
                    );
                    return false;
                }

                var field = explicitField ?? _writer._owner.WireNameOf(anchor!);
                var explicitMessage = Literal(arguments, "message");

                // The message names members the way the error names its field, [JsonPropertyName]
                // included, so a client reads the keys it sent.
                var message =
                    explicitMessage
                    ?? RuleText.RenderPredicate($"{subject} => {text}", _writer.SpellMemberPath);

                // Derived from the condition rather than from `message`, so an author rewording
                // their own text does not move the wire code. The rule is the condition, spelled by
                // the policy, which is also what VM3103 quotes.
                var owner = _writer._owner;
                var spelledByPolicy = RuleText.RenderPredicate(
                    $"{subject} => {text}",
                    owner._fieldNamer
                );
                var derived = CodeNaming.Apply(
                    owner._codeNamespace,
                    RuleText.CodeOfPredicate($"{subject} => {text}", owner._fieldNamer)
                );
                var authored = CodeNaming.Apply(owner._codeNamespace, Literal(arguments, "code"));
                var code =
                    authored is not null ? Quote(authored)
                    : derived is null ? $"{Codes}.Predicate"
                    : Quote(derived);

                // A derived code is the one part of a rules class that cannot be read off the
                // source, so it is stated at the site that owns it.
                if (authored is null && derived is not null)
                {
                    owner.Report(
                        ValidationDiagnostics.EnsureCodeDerived,
                        call,
                        derived,
                        spelledByPolicy
                    );
                }
                var severity = SeverityOf(arguments) is { } member
                    ? $", {SeverityEnum}.{member}"
                    : string.Empty;

                // Never null-guarded: the condition may read fields other than its anchor, so null
                // there is the author's, same as the attribute-region predicate. An explicit
                // message: is the author's text and reports as authored, so no language pack
                // replaces it; the derived wording belongs to the library and stays replaceable.
                var report = explicitMessage is null ? "Report" : "ReportAuthored";

                _writer.StopIf(
                    $"!({_writer.Rewrite(condition)}) && ctx.{report}({Quote(field)}, {code}, {Quote(message)}{severity}).ShouldStop"
                );

                return true;
            }

            public void Emit()
            {
                string? missing = null;

                if (_required is { } required && _access is { } access && _facts is { } facts)
                {
                    var test = ValidatorEmitter.RequiredTest(access, facts, required);
                    var field = Quote(required.Field ?? _field!);

                    if (_constraints.Count > 0 || _descents.Count > 0)
                    {
                        missing = _writer.MissingLocal(facts.PropertyName);
                        _writer.Statement($"var {missing} = {test};");
                        test = missing;
                    }

                    var report = ValidatorEmitter.ReportFor(
                        field,
                        required,
                        facts,
                        infos: InfosFor(required, facts)
                    );

                    _writer.StopIf($"{test} && {report}.ShouldStop");
                }

                foreach (var constraint in _constraints)
                {
                    if (_access is not { } anchored || _facts is not { } anchorFacts)
                    {
                        continue;
                    }

                    var test = ValidatorEmitter.TestFor(
                        anchored,
                        anchorFacts,
                        constraint,
                        new List<(string, ConstraintModel)>(),
                        new List<(string, ConstraintModel)>()
                    );

                    if (test is null)
                    {
                        continue;
                    }

                    var reported = Quote(constraint.Field ?? _field!);
                    var report = ValidatorEmitter.ReportFor(
                        reported,
                        constraint,
                        anchorFacts,
                        infos: InfosFor(constraint, anchorFacts)
                    );

                    // The same conjunct shape the attribute region emits: the test is bracketed
                    // once anything precedes it, so a top-level || cannot silently widen the rule.
                    var condition =
                        missing is null || constraint.Kind == ConstraintKind.Predicate
                            ? test
                            : $"!{missing} && ({test})";

                    _writer.StopIf(ValidatorEmitter.Conjoin(condition, report));
                }

                if (
                    _perElement
                    && _elementConstraints.Count > 0
                    && _access is { } collection
                    && _facts is { } collectionFacts
                )
                {
                    EmitElementRules(collection, collectionFacts, missing);
                }

                foreach (var (elements, value, field, site) in _descents)
                {
                    EmitDescent(elements, value, field, missing, site);
                }
            }

            /// <summary>
            /// The rules chained after a string-element <c>Each()</c>, expanded into an indexed
            /// loop: <c>rules.Count(x.Steps, 1, 30).Each().Length(5, 500)</c> reports at
            /// <c>steps[0]</c> with the element's own constraint code.
            /// </summary>
            /// <remarks>
            /// The field is a computed interpolation rather than a pushed segment, because the
            /// element is the value - there is no member below it for a report to name, and the
            /// interpolation only ever renders on a failing element. Null elements skip through
            /// the guard <c>TestFor</c> already writes for a reference-typed access.
            /// </remarks>
            private void EmitElementRules(
                string access,
                ValidatedPropertyModel collection,
                string? missing
            )
            {
                var n = _writer._locals++;
                var items = $"items{n}";
                var index = $"i{n}";
                var element = $"element{n}";
                var guard = missing is null ? string.Empty : $"!{missing} && ";
                var field = _field!;

                var elementFacts = new ValidatedPropertyModel(
                    collection.PropertyName,
                    field,
                    "global::System.String",
                    PropertyShape.Scalar,
                    null,
                    null,
                    true,
                    true,
                    false,
                    false,
                    "Count",
                    false,
                    default
                );

                var present = ValidatorEmitter.PresentPattern(collection.MissingWhenDefault);

                _writer.Open($"if ({guard}{access} is {present} {items})");
                _writer.Open(
                    $"for (var {index} = 0; {index} < {items}.{collection.CountAccessor}; {index}++)"
                );
                _writer.Statement($"var {element} = {items}[{index}];");

                foreach (var constraint in _elementConstraints)
                {
                    var test = ValidatorEmitter.TestFor(
                        element,
                        elementFacts,
                        constraint,
                        new List<(string, ConstraintModel)>(),
                        new List<(string, ConstraintModel)>()
                    );

                    if (test is null)
                    {
                        continue;
                    }

                    var report = ValidatorEmitter.ReportFor(
                        $"$\"{field}[{{{index}}}]\"",
                        constraint,
                        elementFacts
                    );

                    _writer.StopIf(ValidatorEmitter.Conjoin(test, report));
                }

                _writer.Close();
                _writer.Close();
            }

            private void EmitDescent(
                bool elements,
                ExpressionSyntax value,
                string? explicitField,
                string? missing,
                SyntaxNode site
            )
            {
                var path = _writer.PathOf(value);

                if (path is null || path.Count != 1)
                {
                    // A descent pushes the property's own name as a path segment, so it needs a
                    // single-segment path; a facet of a child is its own Nested's territory.
                    _writer._owner.Report(
                        ValidationDiagnostics.SelectorNotAPath,
                        value,
                        _writer._declaringClass.Name
                    );
                    return;
                }

                var property = path[0];
                var construct = elements ? "rules.Each" : "rules.Nested";

                if (_writer.CarriesValidateNested(property))
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.RulesDescentRepeatsValidateNested,
                        site,
                        property.Name,
                        construct
                    );
                    return;
                }

                var field = explicitField ?? _writer._owner.WireNameOf(property);
                var dependency = _writer.DependencyFor(property, elements, value, site, construct);

                if (dependency is null)
                {
                    return;
                }

                // The walk below runs the validators for the declared type only, and a rules-class
                // descent has no Polymorphism to ask for more.
                if (
                    (elements ? TypeFacts.ElementTypeOf(property.Type) : Unwrap(property.Type))
                        is { } target
                    && AttributeFrontEnd.CanHaveSubtypes(target)
                )
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.RulesDescentIntoUnsealedType,
                        site,
                        target.Name,
                        property.Name,
                        construct,
                        ValidationDiagnostics.RulesDescentIntoUnsealedTypeFix(
                            target is { TypeKind: TypeKind.Class, IsAbstract: false },
                            target.Name,
                            property.Name,
                            construct
                        )
                    );
                }

                var n = _writer._locals++;
                var access = value.ToString();
                var guard = missing is null ? string.Empty : $"!{missing} && ";

                if (elements)
                {
                    var items = $"items{n}";
                    var index = $"i{n}";
                    var present = ValidatorEmitter.PresentPattern(
                        TypeFacts.IsMissingWhenDefault(property.Type)
                    );

                    _writer.Open($"if ({guard}{access} is {present} {items})");
                    _writer.Open(
                        $"for (var {index} = 0; {index} < {items}.{dependency.CountAccessor}; {index}++)"
                    );
                    _writer.Statement($"var element{n} = {items}[{index}];");
                    _writer.Open($"if (element{n} is not null)");
                    _writer.Statement(
                        $"var elementCtx{n} = ctx.PushIndex({Quote(field)}, {index});"
                    );
                    _writer.Open(
                        $"for (var vi{n} = 0; vi{n} < {dependency.ParameterName}.Length; vi{n}++)"
                    );
                    _writer.StopIf(
                        $"{dependency.ParameterName}[vi{n}].Validate(ref elementCtx{n}, element{n}).ShouldStop"
                    );
                    _writer.Close();
                    _writer.Close();
                    _writer.Close();
                    _writer.Close();
                }
                else
                {
                    _writer.Open($"if ({guard}{access} is {{ }} nested{n})");
                    _writer.Statement($"var ctx{n} = ctx.Push({Quote(field)});");
                    _writer.Open(
                        $"for (var vi{n} = 0; vi{n} < {dependency.ParameterName}.Length; vi{n}++)"
                    );
                    _writer.StopIf(
                        $"{dependency.ParameterName}[vi{n}].Validate(ref ctx{n}, nested{n}).ShouldStop"
                    );
                    _writer.Close();
                    _writer.Close();
                }
            }

            /// <param name="labelled">
            /// False when the chain renamed its field, which reports under a name the
            /// <c>[Display(Name)]</c> label was not written for.
            /// </param>
            private ValidatedPropertyModel FactsFor(
                ExpressionSyntax value,
                List<IPropertySymbol>? path,
                bool labelled
            )
            {
                var type = _writer._model.GetTypeInfo(value).Type;
                var name = path is { Count: > 0 } ? path[path.Count - 1].Name : "Value";

                return new ValidatedPropertyModel(
                    name,
                    _field ?? name,
                    type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        ?? "global::System.Object",
                    PropertyShape.Scalar,
                    null,
                    null,
                    type?.IsReferenceType ?? true,
                    type?.SpecialType == SpecialType.System_String,
                    type is not null && TypeFacts.IsNullableValueType(type),
                    type is not null && TypeFacts.IsIndexable(type),
                    type is null ? "Count" : TypeFacts.CountAccessor(type),
                    false,
                    default,
                    Label: labelled && path is { Count: > 0 }
                        ? _writer.LabelOf(path[path.Count - 1])
                        : null,
                    MissingWhenDefault: type is not null && TypeFacts.IsMissingWhenDefault(type)
                );
            }

            /// <summary>
            /// The region's pool when the report names a labelled property under its own field, so
            /// the label rides on a hoisted info; otherwise null, and the report takes the helpers.
            /// </summary>
            private ValidatorEmitter.MessageInfoPool? InfosFor(
                ConstraintModel constraint,
                ValidatedPropertyModel facts
            ) => constraint.Field is null && facts.Label is not null ? _writer._infos : null;

            private ConstraintModel? ConstraintFor(
                string name,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                InvocationExpressionSyntax call
            ) =>
                name switch
                {
                    "Length" => LengthOrCountConstraint(
                        ConstraintKind.StringLength,
                        arguments,
                        call
                    ),
                    "Count" => LengthOrCountConstraint(ConstraintKind.ItemCount, arguments, call),
                    "Range" => RangeConstraint(arguments, call),
                    "RangeAtLeast" => new ConstraintModel(
                        ConstraintKind.Range,
                        Min: OptionalBound(arguments, "min")
                    ),
                    "RangeAtMost" => new ConstraintModel(
                        ConstraintKind.Range,
                        Max: OptionalBound(arguments, "max")
                    ),
                    "Unique" => new ConstraintModel(ConstraintKind.UniqueItems),
                    "Pattern" => PatternConstraint(arguments, call),
                    _ => null,
                };

            /// <summary>
            /// <c>MultipleOf</c>, with its divisor in the denomination the check runs in - the one
            /// <c>[MultipleOf]</c> produces. The divisor's type in the overload the call bound to
            /// decides it: an integral or decimal divisor divides with <c>%</c>, and a double or
            /// float one's check takes a decimal divisor, so a constant goes through
            /// <see cref="MultipleOfReader"/> exactly as an attribute's does, and anything else is
            /// converted where the check reads it.
            /// </summary>
            private bool ReadMultipleOf(
                InvocationExpressionSyntax call,
                IMethodSymbol method,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments
            )
            {
                // The bound method's own parameters, because they carry the generic overloads'
                // type argument where the definition has only TValue.
                if (
                    !arguments.TryGetValue("divisor", out var divisor)
                    || method.Parameters.FirstOrDefault(parameter => parameter.Name == "divisor")
                        is not { } declared
                )
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.NotTranscribable,
                        call,
                        _writer._declaringClass.Name,
                        "a call to 'MultipleOf' the reader does not know"
                    );
                    return false;
                }

                var member = _facts?.PropertyName ?? _access ?? "the value";

                // The generic overloads take any INumber<T>, which includes types such as nint
                // that the check has no divisor form for.
                if (!MultipleOfReader.IsSupported(declared.Type))
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.MultipleOfOnUnsupportedType,
                        call,
                        member,
                        declared.Type.ToDisplayString()
                    );
                    return false;
                }

                var floating =
                    declared.Type.SpecialType
                    is SpecialType.System_Double
                        or SpecialType.System_Single;
                string rendered;
                var decimalDomain = floating;

                if (
                    _writer._model.GetConstantValue(divisor) is
                    { HasValue: true, Value: IFormattable constant }
                )
                {
                    var literal = constant.ToString(
                        constant is double or float ? "R" : null,
                        System.Globalization.CultureInfo.InvariantCulture
                    );

                    if (
                        !MultipleOfReader.TryResolve(
                            declared.Type,
                            literal,
                            out rendered,
                            out var value,
                            out decimalDomain
                        )
                    )
                    {
                        _writer._owner.Report(
                            ValidationDiagnostics.MultipleOfDivisorNotParseable,
                            divisor,
                            member,
                            declared.Type.ToDisplayString()
                        );
                        return false;
                    }

                    // The same check the attribute path makes. A zero reached the emitter as `% 0`,
                    // which is CS0020 inside generated code for an integral member.
                    if (value <= 0m)
                    {
                        _writer._owner.Report(
                            ValidationDiagnostics.MultipleOfDivisorNotPositive,
                            divisor,
                            member,
                            divisor.ToString()
                        );
                        return false;
                    }
                }
                else
                {
                    var read = _writer.Rewrite(divisor);

                    rendered = floating ? $"(decimal)({read})" : read;
                }

                _constraints.Add(
                    new ConstraintModel(
                        ConstraintKind.MultipleOf,
                        Divisor: rendered,
                        DecimalDomain: decimalDomain,
                        Field: FieldLiteral(arguments)
                    )
                );

                return true;
            }

            /// <summary>
            /// <c>Length</c> or <c>Count</c>, with the check <c>[StringLength]</c> and
            /// <c>[ItemCount]</c> get for inverted bounds.
            /// </summary>
            private ConstraintModel LengthOrCountConstraint(
                ConstraintKind kind,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                InvocationExpressionSyntax call
            )
            {
                ReportInvertedBounds(arguments, call);

                return new ConstraintModel(
                    kind,
                    Min: Bound(arguments, "min", "0"),
                    Max: Bound(arguments, "max", int.MaxValue.ToString())
                );
            }

            /// <summary><c>Range</c>, with the check <c>[Range]</c> gets for inverted bounds.</summary>
            private ConstraintModel RangeConstraint(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                InvocationExpressionSyntax call
            )
            {
                ReportInvertedBounds(arguments, call);

                return new ConstraintModel(
                    ConstraintKind.Range,
                    Min: OptionalBound(arguments, "min"),
                    Max: OptionalBound(arguments, "max")
                );
            }

            /// <summary>
            /// Reports a minimum above the maximum as VM1101. Only constant bounds are compared,
            /// because a bound computed at run time has no value here.
            /// </summary>
            private void ReportInvertedBounds(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                InvocationExpressionSyntax call
            )
            {
                if (
                    arguments.TryGetValue("min", out var min)
                    && arguments.TryGetValue("max", out var max)
                    && ConstantBound(min) is { } low
                    && ConstantBound(max) is { } high
                    && low > high
                )
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.MinExceedsMax,
                        call,
                        _facts?.PropertyName ?? _access ?? "the value",
                        ValidationDiagnostics.InvertedBounds,
                        ValidationDiagnostics.InvertedBoundsFix(min.ToString(), max.ToString())
                    );
                }
            }

            /// <summary>
            /// A numeric constant as a <c>decimal</c>, or null when the bound is not a constant or
            /// has no <c>decimal</c> form, as <c>double.NaN</c> has none.
            /// </summary>
            private decimal? ConstantBound(ExpressionSyntax bound)
            {
                var constant = _writer._model.GetConstantValue(bound);

                if (
                    !constant.HasValue
                    || constant.Value
                        is not (
                            sbyte
                            or byte
                            or short
                            or ushort
                            or int
                            or uint
                            or long
                            or ulong
                            or float
                            or double
                            or decimal
                        )
                )
                {
                    return null;
                }

                try
                {
                    return Convert.ToDecimal(
                        constant.Value,
                        System.Globalization.CultureInfo.InvariantCulture
                    );
                }
                catch (OverflowException)
                {
                    return null;
                }
            }

            private ConstraintModel? PatternConstraint(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                InvocationExpressionSyntax call
            )
            {
                if (!arguments.TryGetValue("pattern", out var accessor))
                {
                    return null;
                }

                // The replacement names a method after the anchored property, and keeps the
                // value argument where the call had one: the chained form has none.
                var suggested = $"{_facts?.PropertyName ?? "Value"}Pattern";
                var replacement = arguments.TryGetValue("value", out var value)
                    ? $"{_writer._builder.Name}.Pattern({value}, {suggested})"
                    : $".Pattern({suggested})";

                // The accessor is a [GeneratedRegex] method, so the emitted form is that method
                // invoked. No inline pattern can reach here at all.
                return
                    CalledMethod(
                        accessor,
                        "Pattern",
                        "Declare the expression as a [GeneratedRegex] method and pass the method "
                            + $"by name: {replacement}"
                    )
                        is { } regex
                    ? new ConstraintModel(
                        ConstraintKind.Pattern,
                        RegexAccessor: $"{regex.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{regex.Name}()"
                    )
                    : null;
            }

            /// <summary>
            /// The static method a <c>Pattern</c> or <c>Apply</c> argument names, or null after
            /// reporting why generated code cannot call it.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Generated code calls the method by name, so a method group is the form both take. A
            /// lambda whose whole body is one call to a static method is read as that method:
            /// <c>() =&gt; SkuPattern()</c>, or <c>(ref ValidationContext c, T v) =&gt; Check(ref c, v)</c>
            /// with the lambda's own parameters passed through in order. Anything else names no
            /// method, and is VM3008 with the method-group form in its tail.
            /// </para>
            /// <para>
            /// Generated code is another class in the same assembly, so the method also has to be
            /// reachable from there. A <c>[GeneratedRegex]</c> method is usually written
            /// <c>private</c>. A private method is VM3004, as it is anywhere else in the body,
            /// rather than CS0122 inside a generated file.
            /// </para>
            /// </remarks>
            private IMethodSymbol? CalledMethod(
                ExpressionSyntax argument,
                string vocabulary,
                string replacement
            )
            {
                var method =
                    StaticMethod(_writer._model.GetSymbolInfo(argument).Symbol)
                    ?? LambdaTarget(argument);

                if (method is null)
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.DelegateArgumentNamesNoMethod,
                        argument,
                        _writer._declaringClass.Name,
                        vocabulary,
                        replacement
                    );
                    return null;
                }

                if (
                    !_writer._compilation.IsSymbolAccessibleWithin(
                        method,
                        _writer._declaringClass.ContainingAssembly
                    )
                )
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.MemberNotReachableFromRegion,
                        argument,
                        $"{method.ContainingType.Name}.{method.Name}",
                        _writer._declaringClass.Name
                    );
                    return null;
                }

                return method;
            }

            /// <summary>
            /// The method a lambda's whole body calls, when it is static and receives the lambda's
            /// own parameters in order, or null.
            /// </summary>
            private IMethodSymbol? LambdaTarget(ExpressionSyntax argument)
            {
                if (
                    argument is not AnonymousFunctionExpressionSyntax lambda
                    || _writer._model.GetSymbolInfo(lambda).Symbol is not IMethodSymbol signature
                )
                {
                    return null;
                }

                var body = lambda.ExpressionBody;

                if (
                    body is null
                    && lambda.Block is { Statements.Count: 1 } block
                    && block.Statements[0] is ReturnStatementSyntax { Expression: { } returned }
                )
                {
                    body = returned;
                }

                if (
                    body is not InvocationExpressionSyntax call
                    || StaticMethod(_writer._model.GetSymbolInfo(call).Symbol) is not { } target
                    || call.ArgumentList.Arguments.Count != signature.Parameters.Length
                )
                {
                    return null;
                }

                for (var i = 0; i < signature.Parameters.Length; i++)
                {
                    var passed = call.ArgumentList.Arguments[i];

                    if (
                        passed.NameColon is not null
                        || RefKindOf(passed) != signature.Parameters[i].RefKind
                        || passed.Expression is not IdentifierNameSyntax name
                        || !SymbolEqualityComparer.Default.Equals(
                            _writer._model.GetSymbolInfo(name).Symbol,
                            signature.Parameters[i]
                        )
                    )
                    {
                        return null;
                    }
                }

                return target;
            }

            private static IMethodSymbol? StaticMethod(ISymbol? symbol) =>
                symbol is IMethodSymbol { IsStatic: true, MethodKind: MethodKind.Ordinary } method
                    ? method
                    : null;

            private static RefKind RefKindOf(ArgumentSyntax argument) =>
                argument.RefKindKeyword.Kind() switch
                {
                    SyntaxKind.RefKeyword => RefKind.Ref,
                    SyntaxKind.OutKeyword => RefKind.Out,
                    SyntaxKind.InKeyword => RefKind.In,
                    _ => RefKind.None,
                };

            /// <summary>
            /// <c>AllowedValues</c>, with every value it was given: the entry form's array or
            /// collection expression, and the chained form's <c>params</c>, whether they arrive as
            /// separate arguments or as one array.
            /// </summary>
            /// <remarks>
            /// The values are read through the bound call rather than by argument position, because
            /// a <c>params</c> set is spread over as many positions as it has values. Each value is
            /// written into the check and into its message at build time, so each has to be a
            /// compile-time constant; one that is not is VM3108 rather than a value the check
            /// quietly lacks.
            /// </remarks>
            private bool ReadAllowedValues(
                InvocationExpressionSyntax call,
                IReadOnlyDictionary<string, ExpressionSyntax> arguments
            )
            {
                if (
                    _writer._model.GetOperation(call) is not IInvocationOperation invocation
                    || invocation.Arguments.FirstOrDefault(argument =>
                        argument.Parameter?.Name == "allowed"
                    )
                        is not { } allowed
                )
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.NotTranscribable,
                        call,
                        _writer._declaringClass.Name,
                        "a call to 'AllowedValues' the reader does not know"
                    );
                    return false;
                }

                var elements = ElementsOf(allowed);

                if (elements is null)
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.AllowedValueNotConstant,
                        allowed.Syntax,
                        _writer._declaringClass.Name,
                        allowed.Syntax is ArgumentSyntax whole
                            ? whole.Expression.ToString()
                            : allowed.Syntax.ToString(),
                        ValidationDiagnostics.AllowedSetNotConstantTail
                    );
                    return false;
                }

                var values = new List<string>();
                var displays = new List<string>();
                var readable = true;

                foreach (var element in elements)
                {
                    if (
                        element is SpreadElementSyntax
                        || _writer._model.GetConstantValue(element)
                            is not { HasValue: true } constant
                    )
                    {
                        _writer._owner.Report(
                            ValidationDiagnostics.AllowedValueNotConstant,
                            element,
                            _writer._declaringClass.Name,
                            element.ToString(),
                            ValidationDiagnostics.AllowedValueNotConstantTail
                        );
                        readable = false;
                        continue;
                    }

                    values.Add(_writer.Rewrite(element));
                    displays.Add(DisplayOf(element, constant.Value));
                }

                if (!readable)
                {
                    return false;
                }

                // An empty set compiles to no check. Said where it was written, and nothing is
                // added, which is what would have been emitted anyway.
                if (values.Count == 0)
                {
                    _writer._owner.Report(
                        ValidationDiagnostics.AllowedValuesEmpty,
                        call,
                        "AllowedValues",
                        _facts?.PropertyName ?? _access ?? "the value"
                    );
                    return true;
                }

                var constraint = new ConstraintModel(
                    ConstraintKind.AllowedValues,
                    Values: new EquatableArray<string>(
                        System.Collections.Immutable.ImmutableArray.CreateRange(values)
                    ),
                    ValueDisplays: new EquatableArray<string>(
                        System.Collections.Immutable.ImmutableArray.CreateRange(displays)
                    )
                );

                // An element rule carries no field override, as in the default branch: its path is
                // the collection's wire name, indexed per element at the emission site.
                if (_perElement)
                {
                    _elementConstraints.Add(constraint);
                    return true;
                }

                _constraints.Add(constraint with { Field = FieldLiteral(arguments) });
                return true;
            }

            /// <summary>
            /// The value expressions an <c>allowed</c> argument supplies, or null when it supplies
            /// a set this reader cannot see into, such as an array held in a field.
            /// </summary>
            /// <remarks>
            /// A spread element is returned as itself, so the caller reports it as a value it cannot
            /// read rather than as a set.
            /// </remarks>
            private static IReadOnlyList<SyntaxNode>? ElementsOf(IArgumentOperation allowed)
            {
                // params written as separate arguments: the compiler builds the array, and each
                // element's syntax is one of the arguments as written.
                if (allowed.ArgumentKind == ArgumentKind.ParamArray)
                {
                    return allowed.Value is IArrayCreationOperation { Initializer: { } implicitly }
                        ? implicitly.ElementValues.Select(ElementSyntax).ToList()
                        : Array.Empty<SyntaxNode>();
                }

                var written = allowed.Syntax is ArgumentSyntax argument
                    ? argument.Expression
                    : allowed.Syntax;

                return written switch
                {
                    CollectionExpressionSyntax collection => collection
                        .Elements.Select(element =>
                            element is ExpressionElementSyntax expression
                                ? expression.Expression
                                : (SyntaxNode)element
                        )
                        .ToList(),
                    ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer
                        .Expressions.Cast<SyntaxNode>()
                        .ToList(),
                    ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitly } =>
                        implicitly.Expressions.Cast<SyntaxNode>().ToList(),
                    _ => null,
                };
            }

            private static SyntaxNode ElementSyntax(IOperation element) =>
                element.Syntax is ArgumentSyntax argument ? argument.Expression : element.Syntax;

            /// <summary>
            /// A value as the message shows it, read from the constant rather than from the
            /// source: an enum value by its member's name, a string without its quotes, and a
            /// <c>const</c> by what it holds rather than what it is called.
            /// </summary>
            private string DisplayOf(SyntaxNode element, object? constant)
            {
                if (
                    _writer._model.GetTypeInfo(element).Type is INamedTypeSymbol
                    {
                        TypeKind: TypeKind.Enum
                    } enumType
                )
                {
                    foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
                    {
                        if (member.HasConstantValue && Equals(member.ConstantValue, constant))
                        {
                            return member.Name;
                        }
                    }
                }

                return constant switch
                {
                    null => "null",
                    string text => text,
                    bool flag => flag ? "true" : "false",
                    IFormattable formattable => formattable.ToString(
                        null,
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                    _ => constant.ToString() ?? string.Empty,
                };
            }

            private string? Literal(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                string parameter
            ) =>
                arguments.TryGetValue(parameter, out var expression)
                && _writer._model.GetConstantValue(expression)
                    is { HasValue: true, Value: string text }
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
            private string? FieldLiteral(IReadOnlyDictionary<string, ExpressionSyntax> arguments)
            {
                if (
                    arguments.TryGetValue("field", out var expression)
                    && expression is InvocationExpressionSyntax invocation
                    && invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }
                    && invocation.ArgumentList.Arguments.Count == 1
                    && _writer.PathOf(invocation.ArgumentList.Arguments[0].Expression)
                        is { Count: > 0 } path
                )
                {
                    return _writer.WirePathOf(path);
                }

                return Literal(arguments, "field");
            }

            private string? SeverityOf(IReadOnlyDictionary<string, ExpressionSyntax> arguments)
            {
                if (!arguments.TryGetValue("severity", out var expression))
                {
                    return null;
                }

                return
                    _writer._model.GetConstantValue(expression)
                        is { HasValue: true, Value: int value }
                    ? value switch
                    {
                        1 => "Warning",
                        2 => "Info",
                        _ => null,
                    }
                    : null;
            }

            /// <summary>
            /// A bound as it lands in the check text - rewritten, not raw, so a bare reference to
            /// the rules class's own const qualifies or bakes exactly as it does anywhere else in
            /// the body.
            /// </summary>
            private string Bound(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                string parameter,
                string fallback
            ) =>
                arguments.TryGetValue(parameter, out var expression)
                    ? _writer.Rewrite(expression)
                    : fallback;

            private string? OptionalBound(
                IReadOnlyDictionary<string, ExpressionSyntax> arguments,
                string parameter
            ) =>
                arguments.TryGetValue(parameter, out var expression)
                && expression.ToString() != "null"
                    ? _writer.Rewrite(expression)
                    : null;

            private static string Quote(string text) =>
                SymbolDisplay.FormatLiteral(text, quote: true);
        }

        /// <summary>
        /// The validator array a descent walks, or null when the descent is dropped.
        /// </summary>
        /// <remarks>
        /// The target is judged here, before the walk is written, because a dropped descent must not
        /// reach the region's text: the walk names this array, and the array names the target's
        /// validator.
        /// </remarks>
        private RegionDependency? DependencyFor(
            IPropertySymbol property,
            bool elements,
            ExpressionSyntax value,
            SyntaxNode call,
            string construct
        )
        {
            foreach (var existing in _dependencies)
            {
                if (
                    SymbolEqualityComparer.Default.Equals(existing.Property, property)
                    && existing.Elements == elements
                )
                {
                    return existing;
                }
            }

            var target = elements ? TypeFacts.ElementTypeOf(property.Type) : Unwrap(property.Type);

            if (target is null)
            {
                _owner.Report(ValidationDiagnostics.SelectorNotAPath, value, _declaringClass.Name);
                return null;
            }

            var verdict = DescentTargets.Judge(
                target,
                _compilation,
                _owner._compileDataAnnotations,
                _owner._rulesTarget
            );

            if (verdict != DescentTargets.Verdict.Callable)
            {
                if (
                    DescentTargets.Problem(verdict, target, property.Name, construct) is { } problem
                )
                {
                    _owner.Report(problem.Descriptor, call, problem.Arguments);
                }

                return null;
            }

            var named = (INamedTypeSymbol)target;

            var camel =
                property.Name.Length == 0 || char.IsLower(property.Name[0])
                    ? property.Name
                    : char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);

            var dependency = new RegionDependency(
                property,
                elements,
                $"{camel}Validators",
                $"{property.Name}Validators",
                named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TypeFacts.CountAccessor(property.Type) ?? "Count"
            );

            _dependencies.Add(dependency);

            return dependency;
        }

        private static ITypeSymbol Unwrap(ITypeSymbol type) =>
            type
                is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                } nullable
                ? nullable.TypeArguments[0]
                : type;

        /// <summary>
        /// The rewrites transcription needs, applied to original nodes so the semantic model still
        /// answers for them: <c>nameof</c> through the subject becomes the wire path,
        /// <c>rules.Context</c> becomes the live context, and a bare reference to the rules class's
        /// own statics is qualified - the companion is a different class, so the name has lost its
        /// scope (the lifted-predicate precedent). An expansion of a generic fragment is not
        /// generic, so each mention of the fragment's type parameter is written as the type it
        /// stands for in that expansion.
        /// </summary>
        private sealed class TranscriptionRewriter : CSharpSyntaxRewriter
        {
            private static readonly SymbolDisplayFormat Annotated =
                SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                );

            /// <summary>
            /// A type where only a plain one is accepted. See <see cref="TakesPlainType"/>.
            /// </summary>
            private static readonly SymbolDisplayFormat Plain = Annotated.AddMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.ExpandValueTuple
            );

            private readonly RegionWriter _writer;

            public TranscriptionRewriter(RegionWriter writer) => _writer = writer;

            public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (
                    node.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }
                    && node.ArgumentList.Arguments.Count == 1
                    && _writer.PathOf(node.ArgumentList.Arguments[0].Expression)
                        is { Count: > 0 } path
                )
                {
                    return SyntaxFactory.ParseExpression(
                        SymbolDisplay.FormatLiteral(_writer.WirePathOf(path), quote: true)
                    );
                }

                // C# evaluates nameof(T) to the type parameter's own name, whatever type it stands
                // for, so every expansion gets that name.
                if (
                    node.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }
                    && node.ArgumentList.Arguments.Count == 1
                    && node.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax named
                    && _writer.TypeArgumentOf(named) is { } typeArgument
                )
                {
                    return SyntaxFactory.ParseExpression(
                        SymbolDisplay.FormatLiteral(typeArgument.Parameter.Name, quote: true)
                    );
                }

                return base.VisitInvocationExpression(node);
            }

            /// <summary>
            /// <c>T?</c> over a type parameter without the <c>struct</c> constraint means
            /// <c>T</c> itself when a value type stands for it. Written as <c>int?</c> it would be
            /// <c>Nullable&lt;int&gt;</c>, which is another type.
            /// </summary>
            public override SyntaxNode? VisitNullableType(NullableTypeSyntax node)
            {
                if (
                    node.ElementType is IdentifierNameSyntax element
                    && _writer.TypeArgumentOf(element)
                        is { Parameter.HasValueTypeConstraint: false } typeArgument
                )
                {
                    var concrete = typeArgument
                        .Concrete.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                        .ToDisplayString(Annotated);

                    return SyntaxFactory
                        .ParseTypeName(
                            typeArgument.Concrete.IsValueType ? concrete : concrete + "?"
                        )
                        .WithTriviaFrom(node);
                }

                return base.VisitNullableType(node);
            }

            public override SyntaxNode? VisitMemberAccessExpression(
                MemberAccessExpressionSyntax node
            )
            {
                if (
                    node.Name.Identifier.Text == "Context"
                    && node.Expression is IdentifierNameSyntax root
                    && SymbolEqualityComparer.Default.Equals(
                        _writer._model.GetSymbolInfo(root).Symbol,
                        _writer._builder
                    )
                )
                {
                    return SyntaxFactory.IdentifierName("ctx");
                }

                return base.VisitMemberAccessExpression(node);
            }

            public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
            {
                // The right-hand side of a member access is anchored by whatever precedes it; only
                // a bare name has lost its scope.
                if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node)
                {
                    return base.VisitIdentifierName(node);
                }

                if (_writer.TypeArgumentOf(node) is { } typeArgument)
                {
                    var written = TakesPlainType(node)
                        ? typeArgument
                            .Concrete.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                            .ToDisplayString(Plain)
                        : typeArgument.Concrete.ToDisplayString(Annotated);

                    return SyntaxFactory.ParseTypeName(written).WithTriviaFrom(node);
                }

                // A static local function names the rules class as its containing type but is not a
                // member of it. It is transcribed with the body, so its call is left as written.
                if (
                    _writer._model.GetSymbolInfo(node).Symbol is not { IsStatic: true } symbol
                    || symbol
                        is not (
                            IFieldSymbol
                            or IPropertySymbol
                            or IMethodSymbol { MethodKind: MethodKind.Ordinary }
                        )
                    || symbol.ContainingType is not { } declaring
                    || !DeclaredByTheClass(declaring)
                )
                {
                    return base.VisitIdentifierName(node);
                }

                // A private constant cannot be qualified, and does not need to be: C# bakes a
                // const at every use site, so the value is written back as a literal of its own
                // exact type. The accessibility walk already let it through on the same test.
                if (
                    symbol is IFieldSymbol { HasConstantValue: true } constant
                    && !_writer._compilation.IsSymbolAccessibleWithin(
                        constant,
                        _writer._declaringClass.ContainingAssembly
                    )
                    && _writer.ConstantText(node, constant) is { } literal
                )
                {
                    return SyntaxFactory.ParseExpression(literal);
                }

                return SyntaxFactory.ParseExpression(
                    $"{_writer._declaringClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{node.Identifier.Text}"
                );
            }

            /// <summary>
            /// Whether the type written in place of <paramref name="name"/> has to be plain, where
            /// the type parameter it replaces accepted any type. <c>typeof</c>, <c>is</c>,
            /// <c>as</c>, a pattern, a <c>new</c>, a <c>catch</c> and a member access refuse a
            /// nullable reference annotation. After <c>is</c> and in a pattern a tuple reads as a
            /// positional pattern, and <c>new</c> refuses tuple syntax, so a tuple is written as a
            /// <c>ValueTuple</c>.
            /// </summary>
            private static bool TakesPlainType(IdentifierNameSyntax name) =>
                name.Parent switch
                {
                    TypeOfExpressionSyntax
                    or DeclarationPatternSyntax
                    or TypePatternSyntax
                    or RecursivePatternSyntax
                    or ObjectCreationExpressionSyntax
                    or CatchDeclarationSyntax
                    or MemberAccessExpressionSyntax => true,
                    BinaryExpressionSyntax binary => binary.IsKind(SyntaxKind.IsExpression)
                        || binary.IsKind(SyntaxKind.AsExpression),
                    _ => false,
                };

            private bool DeclaredByTheClass(INamedTypeSymbol declaring)
            {
                for (
                    INamedTypeSymbol? current = _writer._declaringClass;
                    current is not null;
                    current = current.BaseType
                )
                {
                    if (SymbolEqualityComparer.Default.Equals(current, declaring))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}

/// <summary>Whether a rule descends, and into what. Retained for the model merge.</summary>
public enum Nesting
{
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
    string CountAccessor
);

/// <summary>Everything one rules class transcribed to, before the model merge.</summary>
public sealed record RulesDeclaration(
    INamedTypeSymbol Target,
    INamedTypeSymbol RulesClass,
    string SubjectParameterName,
    IReadOnlyList<RegionStatement> Body,
    IReadOnlyList<RegionDependency> Dependencies,
    IReadOnlyList<string> AppliedRules,
    IReadOnlyList<CompanionField> Fields,
    IReadOnlyList<(string Field, string Initializer)> MessageInfos,
    IReadOnlyList<INamedTypeSymbol> Facets
);

/// <summary>A lazily-built facet validator a region caches, emitted as a nullable static field on
/// the companion class. The race on first use is benign - two threads build equivalent validators
/// and one wins, the same reasoning the validator's own nested arrays rely on.</summary>
public sealed record CompanionField(string TypeQualified, string Name);

/// <summary>
/// One fragment method: a static, void, same-compilation method that received the builder,
/// transcribed once per concrete target and emitted into its declaring type's container.
/// </summary>
public sealed class FragmentMethod
{
    public FragmentMethod(
        string name,
        IMethodSymbol definition,
        INamedTypeSymbol target,
        IParameterSymbol? subject,
        string builderParameterName,
        IReadOnlyList<IParameterSymbol> extraParameters
    )
    {
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

    public List<RegionStatement> Body { get; } = new();

    public List<CompanionField> Fields { get; } = new();

    public List<(string Field, string Initializer)> MessageInfos { get; } = new();

    /// <summary>The facets this fragment validates its subject through with <c>As</c>.</summary>
    public List<INamedTypeSymbol> Facets { get; } = new();
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
    string? Condition = null
);

/// <summary>The fragment methods of one declaring type, emitted with that type's file usings.</summary>
public sealed class FragmentContainer
{
    public FragmentContainer(string ns, string name, INamedTypeSymbol declaringType)
    {
        Namespace = ns;
        Name = name;
        DeclaringType = declaringType;
    }

    public string Namespace { get; }

    public string Name { get; }

    public INamedTypeSymbol DeclaringType { get; }

    public List<FragmentMethod> Methods { get; } = new();
}
