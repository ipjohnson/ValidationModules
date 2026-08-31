using PublicApiGenerator;
using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the public surface of every package that ships a referenceable assembly.
///
/// The snapshot doubles as the readable index of the API: one file, every type and member, in one
/// place. Nothing else in the suite notices if a member is removed, a signature changes, or a
/// type's accessibility narrows - the library still builds, and its own tests still pass, because
/// they are compiled together.
///
/// A failure here is not automatically a bug. It means the surface moved, and someone has to decide
/// whether that is intended. To accept a change:
///     UPDATE_SNAPSHOTS=1 dotnet test tests/ValidationModules.Runtime.Tests
/// then read the diff before committing it.
/// </summary>
public class PublicApiTests {

    [Fact]
    public void RuntimeApi() {
        Snapshot.Match(ApiOf(typeof(IValidatorFor<>)));
    }

    /// <summary>
    /// Options shipped without one of these, which is the reason to add it before 1.0.0 rather than
    /// after: it is the newest referenceable assembly, so it is the one whose shape has had the
    /// least chance to be found wrong by use.
    /// </summary>
    /// <remarks>
    /// <c>ValidatorForValidateOptions&lt;T&gt;</c> is deliberately absent, on the same reasoning the
    /// ASP.NET Core snapshot gives for its two: a consumer reaches it through
    /// <c>AddValidatedOptions&lt;T&gt;()</c>, so its constructor is not worth freezing. If it
    /// appears here, someone has widened it and should say why.
    /// </remarks>
    [Fact]
    public void OptionsApi() {
        Snapshot.Match(ApiOf(typeof(Microsoft.Extensions.DependencyInjection.ValidationModulesOptionsExtensions)));
    }

    private static string ApiOf(Type typeFromAssembly) =>
        typeFromAssembly.Assembly.GeneratePublicApi(
            new ApiGeneratorOptions {
                // PublicApiGenerator denies the System.* and Microsoft.* prefixes by default, on the
                // assumption that anything there is the BCL leaking in. Our IServiceCollection
                // extensions live in Microsoft.Extensions.DependencyInjection by MS convention, so
                // without this the most consumer-facing entry point in the package is the one thing
                // the snapshot does not pin.
                AllowNamespacePrefixes = ["Microsoft.Extensions.DependencyInjection"],

                // Assembly-level attributes are build metadata, not API, and several of them
                // (SourceLink, InternalsVisibleTo, TFM) change with build configuration.
                ExcludeAttributes = [
                    "System.Runtime.Versioning.TargetFrameworkAttribute",
                    "System.Reflection.AssemblyMetadataAttribute",
                    "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                    "System.Diagnostics.DebuggableAttribute",
                    "System.Runtime.CompilerServices.CompilationRelaxationsAttribute",
                ],
            });
}
