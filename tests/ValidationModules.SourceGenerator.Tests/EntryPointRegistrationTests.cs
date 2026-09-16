using ValidationModules.SourceGenerator.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The DependencyModules branch registering into the entry point rather than beside it.
/// </summary>
/// <remarks>
/// <para>
/// The harness compilation deliberately references neither DependencyModules nor Hardened - the
/// probe in <see cref="RegistrationEmitterTests"/> needs to be able to observe the negative answer
/// - so the module attributes are declared in each fixture instead. The lookup matches on the full
/// metadata name, so a declaration is as good as a package reference for deciding what is emitted;
/// <c>integ-tests/SutProject.DependencyModules</c> is what proves the emitted partial compiles and
/// runs against the real packages.
/// </para>
/// <para>
/// <c>compiles: false</c> throughout, for the same reason: the emitted partial names
/// <c>DependencyRegistry&lt;T&gt;</c>, which the harness compilation has no reference for.
/// </para>
/// </remarks>
public class EntryPointRegistrationTests
{
    private const string DependencyModuleAttribute = """
        namespace DependencyModules.Runtime.Attributes {
            public class DependencyModuleAttribute : System.Attribute { }
        }
        """;

    private const string HardenedModuleAttribute = """
        namespace Hardened.Shared.Runtime.Attributes {
            public class HardenedModuleAttribute : System.Attribute { }
        }
        """;

    private const string ConstrainedType = """
        namespace Sample {
            public record Pet {
                [ValidationModules.Constraints.Required]
                public string? Name { get; init; }
            }
        }
        """;

    private static string Registration(string source)
    {
        var result = GeneratorHarness.Run(
            source,
            ("ValidationModules_Registration", "DependencyModules")
        );

        return result.Sources.TryGetValue("GeneratedValidatorRegistration.g.cs", out var emitted)
            ? emitted
            : "<no registration emitted>";
    }

    private static string WithModule(string declaration, string attributeSource) =>
        attributeSource + "\n" + ConstrainedType + "\nnamespace App {\n" + declaration + "\n}\n";

    [Fact]
    public void ADependencyModule_GetsAPartialThatAddsToItsRegistry()
    {
        // The whole fix, in one golden file: a partial of the entry point rather than a sibling
        // class nothing loads.
        Snapshot.Match(
            Registration(
                WithModule(
                    "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { }",
                    DependencyModuleAttribute
                )
            )
        );
    }

    [Fact]
    public void AHardenedModule_GetsTheSamePartial()
    {
        // A Hardened application carries [HardenedModule] and no [DependencyModule] of its own:
        // HardenedSourceGenerator names that attribute where DependencyModules' generator names
        // its own. Both are module entry points and both key a DependencyRegistry.
        var source = Registration(
            WithModule(
                "[Hardened.Shared.Runtime.Attributes.HardenedModule] public partial class Application { }",
                HardenedModuleAttribute
            )
        );

        Assert.Contains("partial class Application", source);
        Assert.Contains(
            "global::DependencyModules.Runtime.Helpers.DependencyRegistry<global::App.Application>.Add(ValidationModulesDependencies)",
            source
        );
    }

    [Fact]
    public void TheRegisteredMethodCallsTheExtensionRatherThanRepeatingTheRegistrations()
    {
        // One body, two wrappers. The partial is a single call into the extension, so the branch
        // that registers into an entry point cannot drift from the one that does not.
        var source = Registration(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { }",
                DependencyModuleAttribute
            )
        );

        Assert.Contains(
            "private static void ValidationModulesDependencies(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) => global::Microsoft.Extensions.DependencyInjection.GeneratorTestsValidationExtensions.AddGeneratorTestsValidators(services);",
            source
        );

        Assert.Equal(
            1,
            source
                .Split(
                    "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<"
                )
                .Length - 1
        );
    }

    [Fact]
    public void TheFieldCarriesDynamicDependency()
    {
        // A field initializer is the only thing referencing the method, and a trimmer that removes
        // it removes every registration with it.
        Assert.Contains(
            "[global::System.Diagnostics.CodeAnalysis.DynamicDependency(nameof(ValidationModulesDependencies))]",
            Registration(
                WithModule(
                    "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { }",
                    DependencyModuleAttribute
                )
            )
        );
    }

    [Fact]
    public void ThePartialStatesNoAccessibility()
    {
        // A part that states one has to agree with every other part that does, so `public partial`
        // against an internal module would be CS0262 in the consumer's build.
        var source = Registration(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] internal partial class ApplicationModule { }",
                DependencyModuleAttribute
            )
        );

        Assert.Contains("\n    partial class ApplicationModule", source);
        Assert.DoesNotContain("public partial class ApplicationModule", source);
    }

    [Fact]
    public void ARecordModule_IsCompletedAsARecord()
    {
        // CSharpAuthor has no record class, so a `partial class` here against a record module is
        // CS0261 in the consumer's build.
        var source = Registration(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial record ApplicationModule { }",
                DependencyModuleAttribute
            )
        );

        Assert.Contains("partial record class ApplicationModule", source);
        Assert.DoesNotContain("partial class ApplicationModule", source);
    }

    [Fact]
    public void ARecordStructModule_IsNotTreatedAsAnEntryPoint()
    {
        // A struct cannot be completed by the class this emits. Nothing is registered into it, so
        // the sibling module stays as the only thing a consumer can compose.
        var source = Registration(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial record struct ApplicationModule { }",
                DependencyModuleAttribute
            )
        );

        Assert.Contains("class ValidationModule", source);
        Assert.DoesNotContain("DependencyRegistry<", source);
    }

    [Fact]
    public void ANonPartialModule_IsSkipped()
    {
        // A second declaration of a non-partial class is CS0260. DependencyModules reports the
        // missing modifier itself; adding a second error to the same line helps nobody.
        var source = Registration(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public class ApplicationModule { }",
                DependencyModuleAttribute
            )
        );

        Assert.DoesNotContain("DependencyRegistry<", source);
    }

    [Fact]
    public void AModuleNestedInAnotherType_IsSkipped()
    {
        // A namespace-level partial cannot complete a nested type; it declares a second, detached
        // class, and the registration lands somewhere nothing loads.
        var source = Registration(
            WithModule(
                "public partial class Host { [DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { } }",
                DependencyModuleAttribute
            )
        );

        Assert.DoesNotContain("DependencyRegistry<", source);
    }

    [Fact]
    public void WithNoEntryPoint_TheSiblingModuleIsStillEmitted()
    {
        // A library of validated types compiled on its own has nothing to register into. The
        // sibling module is what a consumer composes by hand, so it stays.
        var source = Registration(DependencyModuleAttribute + "\n" + ConstrainedType);

        Assert.Contains("class ValidationModule", source);
        Assert.Contains("IDependencyModule", source);
        Assert.DoesNotContain("DependencyRegistry<", source);
    }

    [Fact]
    public void WithAnEntryPoint_TheSiblingModuleIsNotEmitted()
    {
        // Both would register the same validators, and the extension is deliberately not
        // idempotent: composing the two would report every error twice.
        var source = Registration(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { }",
                DependencyModuleAttribute
            )
        );

        Assert.DoesNotContain("class ValidationModule", source);
        Assert.DoesNotContain("IDependencyModule", source);
    }

    [Fact]
    public void OnePartialDeclaredTwice_RegistersOnce()
    {
        // Every declaration of a partial type reaches the syntax provider, and each answers with
        // the same entry point. Registering per declaration would report every error twice.
        var source = Registration(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { }\n"
                    + "public partial class ApplicationModule { }",
                DependencyModuleAttribute
            )
        );

        Assert.Equal(1, source.Split("DependencyRegistry<").Length - 1);
    }

    [Fact]
    public void TwoEntryPoints_AreBothRegisteredInto()
    {
        // Two entry points are two applications composed from the same source, and this
        // assembly's validators belong to both. Registering into one would leave the other
        // silently unvalidated.
        var source = Registration(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class FirstModule { }\n"
                    + "[Hardened.Shared.Runtime.Attributes.HardenedModule] public partial class SecondApp { }",
                DependencyModuleAttribute + "\n" + HardenedModuleAttribute
            )
        );

        Assert.Contains("DependencyRegistry<global::App.FirstModule>", source);
        Assert.Contains("DependencyRegistry<global::App.SecondApp>", source);
    }

    [Fact]
    public void TwoEntryPoints_ReportVM6001NamingThem()
    {
        // Far more often a leftover, a copy-paste or an attribute on the wrong class than a
        // deliberate pair, so it is said rather than assumed.
        var result = GeneratorHarness.Run(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class FirstModule { }\n"
                    + "[Hardened.Shared.Runtime.Attributes.HardenedModule] public partial class SecondApp { }",
                DependencyModuleAttribute + "\n" + HardenedModuleAttribute
            ),
            ("ValidationModules_Registration", "DependencyModules")
        );

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM6001");

        Assert.Contains("App.FirstModule, App.SecondApp", diagnostic.GetMessage());
    }

    [Fact]
    public void OneEntryPoint_ReportsNothing()
    {
        var result = GeneratorHarness.Run(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { }",
                DependencyModuleAttribute
            ),
            ("ValidationModules_Registration", "DependencyModules")
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM6001");
    }

    [Fact]
    public void TheServiceCollectionBranch_RegistersIntoNoEntryPointAtAll()
    {
        // The escape hatch stays an escape hatch: a project that asked for the extension alone
        // gets the extension alone, module attributes in the compilation or not.
        var result = GeneratorHarness.Run(
            WithModule(
                "[DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { }",
                DependencyModuleAttribute
            ),
            ("ValidationModules_Registration", "ServiceCollection")
        );

        var source = result.Sources["GeneratedValidatorRegistration.g.cs"];

        Assert.Contains("AddGeneratorTestsValidators", source);
        Assert.DoesNotContain("DependencyRegistry<", source);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "VM6001");
    }

    [Fact]
    public void AModuleInTheGlobalNamespace_IsRegisteredInto()
    {
        // No namespace block to sit in, so the partial goes straight into the file.
        var source = Registration(
            DependencyModuleAttribute
                + "\n"
                + ConstrainedType
                + "\n[DependencyModules.Runtime.Attributes.DependencyModule] public partial class ApplicationModule { }\n"
        );

        Assert.Contains(
            "global::DependencyModules.Runtime.Helpers.DependencyRegistry<global::ApplicationModule>.Add(ValidationModulesDependencies)",
            source
        );
    }
}
