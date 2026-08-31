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
public sealed class RegistrationEmitter {

    private const string DependencyInjection = "Microsoft.Extensions.DependencyInjection";

    private static readonly ITypeDefinition ServiceCollection =
        TypeDefinition.Get(TypeDefinitionEnum.InterfaceDefinition, DependencyInjection, "IServiceCollection");

    /// <summary>Declares the <c>AddSingleton</c> family.</summary>
    private static readonly ITypeDefinition ServiceExtensions =
        TypeDefinition.Get(DependencyInjection, "ServiceCollectionServiceExtensions");

    /// <summary>Declares the <c>TryAdd</c> family.</summary>
    private static readonly ITypeDefinition DescriptorExtensions =
        TypeDefinition.Get(DependencyInjection + ".Extensions", "ServiceCollectionDescriptorExtensions");

    /// <summary>Declares <c>AddValidationRunner</c>; the runtime puts it in the DI namespace.</summary>
    private static readonly ITypeDefinition RunnerExtensions =
        TypeDefinition.Get(DependencyInjection, "ValidationModulesServiceCollectionExtensions");

    private static readonly ITypeDefinition LanguagePack =
        TypeDefinition.Get(TypeDefinitionEnum.InterfaceDefinition, "ValidationModules", "IValidationLanguagePack");

    private static readonly ITypeDefinition DynamicValidator =
        TypeDefinition.Get(TypeDefinitionEnum.InterfaceDefinition, "ValidationModules", "IDynamicValidator");

    /// <param name="models">The validated types, already ordered.</param>
    /// <param name="mode">Which wrapper to emit around the shared body.</param>
    /// <param name="assemblyNamespace">The sanitized assembly name, which names the method.</param>
    /// <param name="fieldNamer">
    /// The naming policy the validators were emitted with. Registered as the default
    /// <see cref="ValidationModules.Naming.IValidationFieldNamer"/> so that the engines which
    /// resolve one at run time agree with the literals baked into the generated code.
    /// </param>
    public string? Emit(
        IReadOnlyList<ValidatedTypeModel> models,
        RegistrationMode mode,
        string assemblyNamespace,
        string? fieldNamer = null,
        bool withDynamicAdapters = false,
        BraceStyle style = BraceStyle.Allman,
        IReadOnlyList<LanguagePackModel>? languagePacks = null) {

        var packs = languagePacks ?? Array.Empty<LanguagePackModel>();

        // A pack-only assembly - five languages, zero validated types - still earns the extension;
        // language packs are a legitimate reason for it to exist.
        if ((models.Count == 0 && packs.Count == 0) || mode == RegistrationMode.None) {
            return null;
        }

        // No namespace of its own: the extension belongs in the DI namespace by convention and the
        // module belongs beside the consumer's own types, and both land in this one file as sibling
        // namespace blocks.
        var file = new CSharpFileDefinition();

        Header(file);

        var di = new NamespaceDefinition(DependencyInjection);

        file.AddComponent(di);
        EmitExtension(di, models, assemblyNamespace, fieldNamer, withDynamicAdapters, packs);

        if (mode == RegistrationMode.DependencyModules) {
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
        IReadOnlyList<LanguagePackModel> languagePacks) {

        var extensions = di.AddClass($"{Identifier(ns)}ValidationExtensions");

        extensions.Modifiers = ComponentModifier.Public | ComponentModifier.Static;
        extensions.Comment = "Registers every validator this assembly generated.";

        var add = extensions.AddMethod($"Add{Identifier(ns)}Validators");

        add.Modifiers = ComponentModifier.Public | ComponentModifier.Static;
        add.Comment =
            "Adds this assembly's generated validators, and a validation runner for each\n" +
            "validated type.\n" +
            "\n" +
            "Not idempotent. Calling this twice registers every validator twice, and a\n" +
            "runner merges every registered validator for a type - so each error would be\n" +
            "reported twice. Add rather than TryAdd is deliberate: registering a second\n" +
            "validator for one type is how a hand-written rule composes with the generated\n" +
            "one, so this cannot dedupe without breaking that.";
        add.SetReturnType(ServiceCollection);

        var services = add.AddParameter(ServiceCollection, "services");

        services.This = true;

        foreach (var model in models) {
            // The container constructs it and owns its lifetime, and injects whatever validates
            // each nested type. Closed generics, so the trimmer keeps the constructor and nothing
            // resolves reflectively at run time.
            add.AddIndentedStatement(InvokeGeneric(
                ServiceExtensions, "AddSingleton",
                new[] { ValidatorFor(TypeRef(model.QualifiedTypeName)), ValidatorType(model) },
                services));
        }

        BlankLine(add);

        // The adapters a Polymorphism.Runtime descent looks up. Emitted for every validated type in
        // an assembly that dispatches dynamically, and for none at all in one that does not: a
        // registration roots its adapter, so charging every consumer for a mode most never use is
        // not free. Within a dispatching assembly the set is complete, so a registry miss means
        // "that assembly never registered" and never "that type had no rules".
        if (withDynamicAdapters) {
            foreach (var model in models) {
                add.AddIndentedStatement(InvokeGeneric(
                    ServiceExtensions, "AddSingleton",
                    new[] { DynamicValidator, AdapterType(model) },
                    services));
            }

            BlankLine(add);
            add.AddLineComment(
                "TryAdd and a factory: every assembly's registration wants the same registry,\n" +
                "built once over whatever adapters all of them contributed. A factory rather\n" +
                "than a type so nothing is constructed reflectively under Native AOT.");
            add.AddIndentedStatement(Invoke(DescriptorExtensions, "TryAddSingleton", services, Registry()));
            BlankLine(add);
        }

        // Closed per type rather than an open generic: AddScoped(typeof(ValidationRunner<>)) would
        // have MS.DI construct it reflectively, which a Native AOT publish cannot do.
        foreach (var model in models) {
            add.AddIndentedStatement(InvokeGeneric(
                RunnerExtensions, "AddValidationRunner",
                new[] { TypeRef(model.QualifiedTypeName) },
                services));
        }

        BlankLine(add);
        add.AddLineComment(
            "Element-wise validation for collection bodies - List<T> and T[] - so a batch\n" +
            "endpoint's .Validate<List<T>>() resolves a validator that walks the elements\n" +
            "with indexed paths. Closed per type, for the AOT reason above.");

        foreach (var model in models) {
            add.AddIndentedStatement(InvokeGeneric(
                RunnerExtensions, "AddCollectionValidatorsFor",
                new[] { TypeRef(model.QualifiedTypeName) },
                services));
        }

        if (languagePacks.Count > 0) {
            BlankLine(add);
            add.AddLineComment(
                "The language packs this assembly compiled, in additional-files order - which\n" +
                "is what makes the layering rule hold: MSBuild adds package-delivered files\n" +
                "before project items, so an app-local file registers later and wins per key.");

            foreach (var pack in languagePacks) {
                add.AddIndentedStatement(InvokeGeneric(
                    ServiceExtensions, "AddSingleton",
                    new[] { LanguagePack, NamedType(ns, pack.ClassName) },
                    services));
            }

            BlankLine(add);
            add.AddLineComment(
                "TryAdd and a factory: every assembly contributes packs, one formatter reads\n" +
                "them all, and an app that installed its own formatter first keeps it.");
            add.AddIndentedStatement(InvokeGeneric(
                DescriptorExtensions, "TryAddSingleton",
                new[] { (ITypeDefinition)TypeDefinition.Get("ValidationModules", "ValidationMessageFormatter") },
                services,
                PackFormatter()));
        }

        BlankLine(add);
        add.AddLineComment(
            "TryAdd, so a namer the consumer registered first survives. The policy here is\n" +
            "the one the literals above were emitted with, so the engines that resolve a\n" +
            "namer at run time agree with the generated code by default.");
        add.AddIndentedStatement(InvokeGeneric(
            DescriptorExtensions, "TryAddSingleton",
            new[] {
                (ITypeDefinition)TypeDefinition.Get(
                    TypeDefinitionEnum.InterfaceDefinition, "ValidationModules.Naming", "IValidationFieldNamer"),
            },
            services,
            Property(TypeDefinition.Get("ValidationModules.Naming", NamerFor(fieldNamer)), "Instance")));
        BlankLine(add);
        add.Return(services);
    }

    /// <summary>
    /// The registry factory: <c>provider =&gt; new DynamicValidatorRegistry(GetServices…)</c>,
    /// with every name written in full because the lambda is an expression the type model cannot
    /// carry whole.
    /// </summary>
    private static IOutputComponent Registry() {
        var resolve = InvokeGeneric(
            TypeDefinition.Get(DependencyInjection, "ServiceProviderServiceExtensions"),
            "GetServices", new[] { DynamicValidator }, "provider");

        var construct = New(TypeDefinition.Get("ValidationModules", "DynamicValidatorRegistry"), resolve);

        return new WrapStatement(construct, new CodeOutputComponent("provider => ") { Indented = false }, null);
    }

    /// <summary>
    /// The formatter factory: <c>provider =&gt; new LanguagePackFormatter(GetServices…)</c>, the
    /// same closed-type shape as <see cref="Registry"/> so nothing constructs reflectively.
    /// </summary>
    private static IOutputComponent PackFormatter() {
        var resolve = InvokeGeneric(
            TypeDefinition.Get(DependencyInjection, "ServiceProviderServiceExtensions"),
            "GetServices", new[] { LanguagePack }, "provider");

        var construct = New(TypeDefinition.Get("ValidationModules", "LanguagePackFormatter"), resolve);

        return new WrapStatement(construct, new CodeOutputComponent("provider => ") { Indented = false }, null);
    }

    /// <summary>
    /// The DependencyModules wrapper: one call into the body above.
    /// </summary>
    /// <remarks>
    /// Emitted whole rather than as a partial for DM's own generator to complete, because generators
    /// cannot see each other's output - an attribute this one wrote would never reach DM's.
    /// <c>IDependencyModule</c> has exactly one member without a default implementation, so there is
    /// nothing left to complete.
    /// </remarks>
    private static void EmitModule(NamespaceDefinition consumer, string ns) {
        var module = consumer.AddClass("ValidationModule");

        module.Modifiers = ComponentModifier.Public | ComponentModifier.Sealed;
        module.Comment = "Registers every validator this assembly generated.";
        module.AddBaseType(TypeDefinition.Get(
            TypeDefinitionEnum.InterfaceDefinition, "DependencyModules.Runtime.Interfaces", "IDependencyModule"));

        var populate = module.AddMethod("PopulateServiceCollection");

        populate.AddParameter(ServiceCollection, "services");
        populate.LambdaSyntax = true;
        populate.AddIndentedStatement(Invoke(
            TypeDefinition.Get(DependencyInjection, $"{Identifier(ns)}ValidationExtensions"),
            $"Add{Identifier(ns)}Validators",
            "services"));
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
