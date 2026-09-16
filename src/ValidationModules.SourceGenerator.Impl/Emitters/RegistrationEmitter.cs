using System.Text.RegularExpressions;
using CSharpAuthor;
using ValidationModules.SourceGenerator.Impl.Models;
using static CSharpAuthor.SyntaxHelpers;
using static ValidationModules.SourceGenerator.Impl.Emitters.EmitterOutput;

namespace ValidationModules.SourceGenerator.Impl.Emitters;

/// <summary>
/// Emits the registration for an assembly's validators, in whichever shape the consumer's
/// references call for.
/// </summary>
/// <remarks>
/// <para>
/// <b>One body, two wrappers</b>, which is what plan §7.3 asks for. The body is an
/// <c>IServiceCollection</c> extension - <c>services.AddMyAppValidators()</c> - and when
/// DependencyModules is referenced the emitted module is a one-line call to it rather than a second
/// copy of the same registrations. Before this, the two branches shared an emitter and produced
/// genuinely different bodies: a table of <c>ValidatorRegistration</c> records on one side and
/// direct <c>AddSingleton</c> calls on the other.
/// </para>
/// <para>
/// <b>Why the extension rather than the table.</b> The table erased the generic - a
/// <c>Type</c> beside a <c>Func&lt;IServiceProvider, object&gt;</c>, so nothing checked that the
/// factory for <c>typeof(IValidatorFor&lt;Pet&gt;)</c> returned one - allocated an array of
/// closures at static init only to iterate it once at startup, and lived in a class the consumer
/// had to already know the name of, in a namespace derived from the sanitized assembly name.
/// <c>ValidatorRegistration</c> and <c>AddValidationModules(IReadOnlyList&lt;…&gt;)</c> remain in the
/// runtime for anyone hand-building a table; nothing generates one.
/// </para>
/// <para>
/// <b>The method name carries the assembly, and has to.</b> Each assembly registers its own
/// validators - there is deliberately no cross-assembly scanning - so two of them emitting
/// <c>AddValidationModules()</c> on <c>IServiceCollection</c> would be CS0121 at the composition
/// root. <c>AddMyAppValidators()</c> and <c>AddMyLibValidators()</c> compose without ceremony.
/// </para>
/// <para>
/// <b>Every registration is a static-form call on its declaring extension class</b> -
/// <c>global::…ServiceCollectionServiceExtensions.AddSingleton&lt;…&gt;(services)</c> - rather than
/// the <c>services.AddSingleton&lt;…&gt;()</c> a person would write. The generated file carries no
/// using directives, and a <c>global::</c> name cannot reach an extension method; the static form
/// can, and it also closes the one door qualification leaves open, because anyone may declare a
/// static class inside <c>Microsoft.Extensions.DependencyInjection</c> and instance-form lookup
/// would consider it.
/// </para>
/// </remarks>
public sealed class RegistrationEmitter
{
    private const string DependencyInjection = "Microsoft.Extensions.DependencyInjection";

    /// <summary>
    /// The two members the entry-point partial contributes. Named after this package rather than
    /// after what they do, because they land in a class DependencyModules' own generator is also
    /// writing members into - and a collision there is CS0102 in the consumer's build.
    /// </summary>
    private const string RegistrationMethod = "ValidationModulesDependencies";

    private const string RegistrationField = "validationModulesField";

    private static readonly ITypeDefinition ServiceCollection = TypeDefinition.Get(
        TypeDefinitionEnum.InterfaceDefinition,
        DependencyInjection,
        "IServiceCollection"
    );

    /// <summary>Declares the <c>AddSingleton</c> family.</summary>
    private static readonly ITypeDefinition ServiceExtensions = TypeDefinition.Get(
        DependencyInjection,
        "ServiceCollectionServiceExtensions"
    );

    /// <summary>Declares the <c>TryAdd</c> family.</summary>
    private static readonly ITypeDefinition DescriptorExtensions = TypeDefinition.Get(
        DependencyInjection + ".Extensions",
        "ServiceCollectionDescriptorExtensions"
    );

    /// <summary>Declares <c>AddValidationRunner</c>; the runtime puts it in the DI namespace.</summary>
    private static readonly ITypeDefinition RunnerExtensions = TypeDefinition.Get(
        DependencyInjection,
        "ValidationModulesServiceCollectionExtensions"
    );

    private static readonly ITypeDefinition LanguagePack = TypeDefinition.Get(
        TypeDefinitionEnum.InterfaceDefinition,
        "ValidationModules",
        "IValidationLanguagePack"
    );

    private static readonly ITypeDefinition DynamicValidator = TypeDefinition.Get(
        TypeDefinitionEnum.InterfaceDefinition,
        "ValidationModules",
        "IDynamicValidator"
    );

    /// <param name="models">The validated types, already ordered.</param>
    /// <param name="mode">Which wrapper to emit around the shared body.</param>
    /// <param name="assemblyNamespace">The sanitized assembly name, which names the method.</param>
    /// <param name="fieldNamer">
    /// The naming policy the validators were emitted with. Registered as the default
    /// <see cref="ValidationModules.Naming.IValidationFieldNamer"/> so that the engines which
    /// resolve one at run time agree with the literals baked into the generated code.
    /// </param>
    /// <param name="entryPoints">
    /// The module entry points this compilation declares, from <see cref="EntryPointLookup"/>.
    /// Each gets a partial that adds the extension to its registry. Empty means there is nothing
    /// to register into, and the sibling module is emitted instead.
    /// </param>
    public string? Emit(
        IReadOnlyList<ValidatedTypeModel> models,
        RegistrationMode mode,
        string assemblyNamespace,
        string? fieldNamer = null,
        bool withDynamicAdapters = false,
        BraceStyle style = BraceStyle.Allman,
        IReadOnlyList<LanguagePackModel>? languagePacks = null,
        IReadOnlyList<ModuleEntryPoint>? entryPoints = null
    )
    {
        var packs = languagePacks ?? Array.Empty<LanguagePackModel>();
        var modules = entryPoints ?? Array.Empty<ModuleEntryPoint>();

        // A pack-only assembly - five languages, zero validated types - still earns the extension;
        // language packs are a legitimate reason for it to exist.
        if ((models.Count == 0 && packs.Count == 0) || mode == RegistrationMode.None)
        {
            return null;
        }

        // No namespace of its own: the extension belongs in the DI namespace by convention and the
        // registrations belong beside the consumer's own types, and both land in this one file as
        // sibling namespace blocks.
        var file = new CSharpFileDefinition();

        Header(file);

        var di = new NamespaceDefinition(DependencyInjection);

        file.AddComponent(di);
        EmitExtension(di, models, assemblyNamespace, fieldNamer, withDynamicAdapters, packs);

        if (mode == RegistrationMode.DependencyModules)
        {
            if (modules.Count > 0)
            {
                EmitEntryPointRegistrations(file, modules, assemblyNamespace);

                return ApplyRecordDeclarations(Render(file, style), modules);
            }

            var consumer = new NamespaceDefinition(assemblyNamespace);

            file.AddComponent(consumer);
            EmitModule(consumer, assemblyNamespace);
        }

        return Render(file, style);
    }

    private static void EmitExtension(
        NamespaceDefinition di,
        IReadOnlyList<ValidatedTypeModel> models,
        string ns,
        string? fieldNamer,
        bool withDynamicAdapters,
        IReadOnlyList<LanguagePackModel> languagePacks
    )
    {
        var extensions = di.AddClass($"{Identifier(ns)}ValidationExtensions");

        extensions.Modifiers = ComponentModifier.Public | ComponentModifier.Static;
        extensions.Comment = "Registers every validator this assembly generated.";

        var add = extensions.AddMethod($"Add{Identifier(ns)}Validators");

        add.Modifiers = ComponentModifier.Public | ComponentModifier.Static;
        add.Comment =
            "Adds this assembly's generated validators, and a validation runner for each\n"
            + "validated type.\n"
            + "\n"
            + "Not idempotent. Calling this twice registers every validator twice, and a\n"
            + "runner merges every registered validator for a type - so each error would be\n"
            + "reported twice. Add rather than TryAdd is deliberate: registering a second\n"
            + "validator for one type is how a hand-written rule composes with the generated\n"
            + "one, so this cannot dedupe without breaking that.";
        add.SetReturnType(ServiceCollection);

        var services = add.AddParameter(ServiceCollection, "services");

        services.This = true;

        foreach (var model in models)
        {
            // The container constructs it and owns its lifetime, and injects whatever validates
            // each nested type. Closed generics, so the trimmer keeps the constructor and nothing
            // resolves reflectively at run time.
            add.AddIndentedStatement(
                InvokeGeneric(
                    ServiceExtensions,
                    "AddSingleton",
                    new[] { ValidatorFor(TypeRef(model.QualifiedTypeName)), ValidatorType(model) },
                    services
                )
            );
        }

        BlankLine(add);

        // The adapters a Polymorphism.Runtime descent looks up. Emitted for every validated type in
        // an assembly that dispatches dynamically, and for none at all in one that does not: a
        // registration roots its adapter, so charging every consumer for a mode most never use is
        // not free. Within a dispatching assembly the set is complete, so a registry miss means
        // "that assembly never registered" and never "that type had no rules".
        if (withDynamicAdapters)
        {
            foreach (var model in models)
            {
                add.AddIndentedStatement(
                    InvokeGeneric(
                        ServiceExtensions,
                        "AddSingleton",
                        new[] { DynamicValidator, AdapterType(model) },
                        services
                    )
                );
            }

            BlankLine(add);
            add.AddLineComment(
                "TryAdd and a factory: every assembly's registration wants the same registry,\n"
                    + "built once over whatever adapters all of them contributed. A factory rather\n"
                    + "than a type so nothing is constructed reflectively under Native AOT."
            );
            add.AddIndentedStatement(
                Invoke(DescriptorExtensions, "TryAddSingleton", services, Registry())
            );
            BlankLine(add);
        }

        // Closed per type rather than an open generic: AddScoped(typeof(ValidationRunner<>)) would
        // have MS.DI construct it reflectively, which a Native AOT publish cannot do.
        foreach (var model in models)
        {
            add.AddIndentedStatement(
                InvokeGeneric(
                    RunnerExtensions,
                    "AddValidationRunner",
                    new[] { TypeRef(model.QualifiedTypeName) },
                    services
                )
            );
        }

        BlankLine(add);
        add.AddLineComment(
            "Element-wise validation for collection bodies - List<T> and T[] - so a batch\n"
                + "endpoint's .Validate<List<T>>() resolves a validator that walks the elements\n"
                + "with indexed paths. Closed per type, for the AOT reason above."
        );

        foreach (var model in models)
        {
            add.AddIndentedStatement(
                InvokeGeneric(
                    RunnerExtensions,
                    "AddCollectionValidatorsFor",
                    new[] { TypeRef(model.QualifiedTypeName) },
                    services
                )
            );
        }

        if (languagePacks.Count > 0)
        {
            BlankLine(add);
            add.AddLineComment(
                "The language packs this assembly compiled, in additional-files order - which\n"
                    + "is what makes the layering rule hold: MSBuild adds package-delivered files\n"
                    + "before project items, so an app-local file registers later and wins per key."
            );

            foreach (var pack in languagePacks)
            {
                add.AddIndentedStatement(
                    InvokeGeneric(
                        ServiceExtensions,
                        "AddSingleton",
                        new[] { LanguagePack, NamedType(ns, pack.ClassName) },
                        services
                    )
                );
            }

            BlankLine(add);
            add.AddLineComment(
                "TryAdd and a factory: every assembly contributes packs, one formatter reads\n"
                    + "them all, and an app that installed its own formatter first keeps it."
            );
            add.AddIndentedStatement(
                InvokeGeneric(
                    DescriptorExtensions,
                    "TryAddSingleton",
                    new[]
                    {
                        (ITypeDefinition)
                            TypeDefinition.Get("ValidationModules", "ValidationMessageFormatter"),
                    },
                    services,
                    PackFormatter()
                )
            );
        }

        BlankLine(add);
        add.AddLineComment(
            "TryAdd, so a namer the consumer registered first survives. The policy here is\n"
                + "the one the literals above were emitted with, so the engines that resolve a\n"
                + "namer at run time agree with the generated code by default."
        );
        add.AddIndentedStatement(
            InvokeGeneric(
                DescriptorExtensions,
                "TryAddSingleton",
                new[]
                {
                    (ITypeDefinition)
                        TypeDefinition.Get(
                            TypeDefinitionEnum.InterfaceDefinition,
                            "ValidationModules.Naming",
                            "IValidationFieldNamer"
                        ),
                },
                services,
                Property(
                    TypeDefinition.Get("ValidationModules.Naming", NamerFor(fieldNamer)),
                    "Instance"
                )
            )
        );
        BlankLine(add);
        add.Return(services);
    }

    /// <summary>
    /// The registry factory: <c>provider =&gt; new DynamicValidatorRegistry(GetServices…)</c>,
    /// with every name written in full because the lambda is an expression the type model cannot
    /// carry whole.
    /// </summary>
    private static IOutputComponent Registry()
    {
        var resolve = InvokeGeneric(
            TypeDefinition.Get(DependencyInjection, "ServiceProviderServiceExtensions"),
            "GetServices",
            new[] { DynamicValidator },
            "provider"
        );

        var construct = New(
            TypeDefinition.Get("ValidationModules", "DynamicValidatorRegistry"),
            resolve
        );

        return new WrapStatement(
            construct,
            new CodeOutputComponent("provider => ") { Indented = false },
            null
        );
    }

    /// <summary>
    /// The formatter factory: <c>provider =&gt; new LanguagePackFormatter(GetServices…)</c>, the
    /// same closed-type shape as <see cref="Registry"/> so nothing constructs reflectively.
    /// </summary>
    private static IOutputComponent PackFormatter()
    {
        var resolve = InvokeGeneric(
            TypeDefinition.Get(DependencyInjection, "ServiceProviderServiceExtensions"),
            "GetServices",
            new[] { LanguagePack },
            "provider"
        );

        var construct = New(
            TypeDefinition.Get("ValidationModules", "LanguagePackFormatter"),
            resolve
        );

        return new WrapStatement(
            construct,
            new CodeOutputComponent("provider => ") { Indented = false },
            null
        );
    }

    /// <summary>
    /// A partial of every entry point in the compilation, each adding the extension above to its
    /// own <c>DependencyRegistry&lt;TEntryPoint&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All of them, not one.</b> Two entry points in an assembly are two applications composed
    /// from the same source, and this assembly's validators belong to both. Registering into one
    /// would leave the other silently unvalidated, and picking which one by name would put a
    /// behaviour change one rename away. <c>VM6001</c> is reported alongside, because two entry
    /// points is far more often a leftover than a decision.
    /// </para>
    /// <para>
    /// Grouped by namespace so a file declares each namespace once. Two blocks for one namespace
    /// compile, but the generated file is read by people diagnosing why a validator did not
    /// register.
    /// </para>
    /// </remarks>
    private static void EmitEntryPointRegistrations(
        CSharpFileDefinition file,
        IReadOnlyList<ModuleEntryPoint> entryPoints,
        string ns
    )
    {
        foreach (var group in entryPoints.GroupBy(entryPoint => entryPoint.Namespace))
        {
            // A module in the global namespace has no block to sit in; the class goes straight
            // into the file.
            IConstructContainer container;

            if (group.Key.Length == 0)
            {
                container = file;
            }
            else
            {
                var block = new NamespaceDefinition(group.Key);

                file.AddComponent(block);
                container = block;
            }

            foreach (var entryPoint in group)
            {
                EmitEntryPointRegistration(container, entryPoint, ns);
            }
        }
    }

    /// <summary>
    /// The registry hook: a field initializer that runs at class construction and a method holding
    /// the one call into the extension.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape DependencyModules' own writers use for the same job - its
    /// <c>DependencyFileWriter</c>, <c>DecoratorFileWriter</c> and
    /// <c>InterceptorRegistrationWriter</c> each build it - reproduced rather than called, because
    /// all three keep it private and, more to the point, these sources are compiled into
    /// <c>Hardened.Validation.SourceGenerator</c>, which references no part of DependencyModules.
    /// The type names are literals for the reason every other DependencyModules name in this file
    /// is one.
    /// </para>
    /// <para>
    /// No accessibility on the partial: a part that states one has to agree with every other part
    /// that does, and an <c>internal</c> module would make <c>public partial</c> a build error the
    /// consumer cannot fix.
    /// </para>
    /// <para>
    /// <c>[DynamicDependency]</c> because a field is the only thing referencing the method, and a
    /// trimmer that removes it removes every registration with it.
    /// </para>
    /// </remarks>
    private static void EmitEntryPointRegistration(
        IConstructContainer container,
        ModuleEntryPoint entryPoint,
        string ns
    )
    {
        var partial = container.AddClass(entryPoint.Name);

        partial.Modifiers = ComponentModifier.Partial | ComponentModifier.NoAccessibility;

        var register = partial.AddMethod(RegistrationMethod);

        register.Modifiers = ComponentModifier.Private | ComponentModifier.Static;
        register.Comment = "Registers every validator this assembly generated.";
        register.AddParameter(ServiceCollection, "services");
        register.LambdaSyntax = true;
        register.AddIndentedStatement(
            Invoke(
                TypeDefinition.Get(DependencyInjection, $"{Identifier(ns)}ValidationExtensions"),
                $"Add{Identifier(ns)}Validators",
                "services"
            )
        );

        var field = partial.AddField(typeof(int), RegistrationField);

        field.Modifiers = ComponentModifier.Private | ComponentModifier.Static;
        field.AddAttribute(
            TypeDefinition.Get("System.Diagnostics.CodeAnalysis", "DynamicDependency"),
            $"nameof({RegistrationMethod})"
        );
        field.InitializeValue = new StaticInvokeStatement(
            RegistryOf(NamedType(entryPoint.Namespace, entryPoint.Name)),
            "Add",
            new List<IOutputComponent> { CodeOutputComponent.Get(RegistrationMethod) }
        )
        {
            Indented = false,
        };
    }

    /// <summary><c>DependencyRegistry&lt;TEntryPoint&gt;</c>.</summary>
    private static ITypeDefinition RegistryOf(ITypeDefinition entryPoint) =>
        new GenericTypeDefinition(
            TypeDefinitionEnum.ClassDefinition,
            "DependencyModules.Runtime.Helpers",
            "DependencyRegistry",
            new[] { entryPoint }
        );

    /// <summary>
    /// Rewrites the declaration of a record entry point, which CSharpAuthor has no class for.
    /// </summary>
    /// <remarks>
    /// Without it, a <c>[DependencyModule] public partial record Foo</c> gets a
    /// <c>partial class Foo</c> here and the consumer's build fails with CS0261.
    /// DependencyModules' <c>EntryModelUtil.ApplyRecordDeclaration</c> does the same thing for the
    /// same reason.
    /// </remarks>
    private static string ApplyRecordDeclarations(
        string source,
        IReadOnlyList<ModuleEntryPoint> entryPoints
    )
    {
        foreach (var entryPoint in entryPoints)
        {
            if (!entryPoint.IsRecord)
            {
                continue;
            }

            source = Regex.Replace(
                source,
                @"partial class " + Regex.Escape(entryPoint.Name) + @"(?!\w)",
                $"partial record class {entryPoint.Name}"
            );
        }

        return source;
    }

    /// <summary>
    /// The DependencyModules wrapper, for a compilation that declares no entry point to register
    /// into: a library of validated types, compiled on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A consumer composes this one by hand - <c>services.AddModule&lt;ValidationModule&gt;()</c>
    /// or a direct <c>PopulateServiceCollection</c> - because that is all a sibling module can be
    /// composed with. Nothing generated can reach it: the attribute DependencyModules composes a
    /// module with is generated by DependencyModules' generator, and generators cannot see each
    /// other's output.
    /// </para>
    /// <para>
    /// That is the reason an entry point wins where there is one. Everything else about the two is
    /// the same: one body, two wrappers, so the branches cannot drift.
    /// </para>
    /// </remarks>
    private static void EmitModule(NamespaceDefinition consumer, string ns)
    {
        var module = consumer.AddClass("ValidationModule");

        module.Modifiers = ComponentModifier.Public | ComponentModifier.Sealed;
        module.Comment = "Registers every validator this assembly generated.";
        module.AddBaseType(
            TypeDefinition.Get(
                TypeDefinitionEnum.InterfaceDefinition,
                "DependencyModules.Runtime.Interfaces",
                "IDependencyModule"
            )
        );

        var populate = module.AddMethod("PopulateServiceCollection");

        populate.AddParameter(ServiceCollection, "services");
        populate.LambdaSyntax = true;
        populate.AddIndentedStatement(
            Invoke(
                TypeDefinition.Get(DependencyInjection, $"{Identifier(ns)}ValidationExtensions"),
                $"Add{Identifier(ns)}Validators",
                "services"
            )
        );
    }

    /// <summary>
    /// The sanitized assembly name as a single identifier: "My.App" names AddMyAppValidators, and
    /// a kebab-case "app2-signupapi" names AddApp2SignupapiValidators. See
    /// <see cref="RegistrationNaming"/>.
    /// </summary>
    private static string Identifier(string ns) => RegistrationNaming.Identifier(ns);

    private static string NamerFor(string? fieldNamer) => EmitterOutput.NamerFor(fieldNamer);

    private static ITypeDefinition AdapterType(ValidatedTypeModel model) =>
        NamedType(model.Namespace, $"{model.TypeName}DynamicValidator");

    private static ITypeDefinition ValidatorType(ValidatedTypeModel model) =>
        NamedType(model.Namespace, model.ValidatorName);

    // Kept deliberately: registering by implementation type is what lets the container inject the
    // nested sets. A factory returning a hand-built instance would pin the graph at generation
    // time and defeat the point.
}
