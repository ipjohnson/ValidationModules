using Microsoft.AspNetCore.Builder;
using PublicApiGenerator;
using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.AspNetCore.Tests;

/// <summary>
/// Pins the public surface of the ASP.NET Core integration.
/// </summary>
/// <remarks>
/// <para>
/// The runtime has had one of these since early on; this package shipped without. That gap is the
/// reason to add it now rather than later: the convention registration is deferred past 1.0.0,
/// and the whole argument for deferring it
/// safely is that nothing shipped today pins a shape it will need to change. A snapshot is what
/// turns that argument into something the build checks.
/// </para>
/// <para>
/// Two types are deliberately absent from it. <c>ValidationEndpointFilter&lt;T&gt;</c> and
/// <c>ValidationExceptionHandler</c> are internal, because a consumer reaches both through
/// <c>Validate&lt;T&gt;()</c> and <c>AddValidationProblemDetails()</c> and neither constructor is
/// worth freezing. If either appears here, someone has widened them and should say why.
/// </para>
/// <para>
/// To accept an intended change:
/// <c>UPDATE_SNAPSHOTS=1 dotnet test tests/ValidationModules.AspNetCore.Tests</c>
/// </para>
/// </remarks>
public class PublicApiTests {

    [Fact]
    public void AspNetCoreApi() {
        Snapshot.Match(typeof(ValidationProblem).Assembly.GeneratePublicApi(
            new ApiGeneratorOptions {
                // The endpoint and DI extensions live in Microsoft.* namespaces by MS convention,
                // so without this the two entry points a consumer actually calls go unpinned.
                AllowNamespacePrefixes = ["Microsoft.AspNetCore", "Microsoft.Extensions.DependencyInjection"],
                ExcludeAttributes = [
                    "System.Runtime.Versioning.TargetFrameworkAttribute",
                    "System.Reflection.AssemblyMetadataAttribute",
                    "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                    "System.Diagnostics.DebuggableAttribute",
                    "System.Runtime.CompilerServices.CompilationRelaxationsAttribute",
                ],
            }));
    }
}
