using DependencyModules.Runtime;
using Microsoft.Extensions.DependencyInjection;
using SutProject.Dm;
using ValidationModules;
using Xunit;

namespace SutProject.DependencyModules.Tests;

/// <summary>
/// <c>rules.As&lt;IAudited&gt;(x)</c> with the facet shipped as IL from a referenced assembly: the
/// service binding, validated from a second module, exactly as the composition model intends.
/// </summary>
/// <remarks>
/// SutProject declares the facet and its rules, runs the generator itself, and its
/// <c>AddSutProjectValidators()</c> registers the generated <c>IAuditedValidator</c>. This
/// assembly's <c>DeploymentValidator</c> resolves the closed
/// <c>IValidatorFor&lt;IAudited&gt;</c> through the pass's services - no scanning, no naming
/// protocol - and a missing registration is a loud error naming the module to compose.
/// </remarks>
public class CrossAssemblyFacetTests {

    private static ServiceProvider BuildProvider(bool composeSutProject) {
        var services = new ServiceCollection();

        services.AddModule<global::SutProject.DependencyModules.ValidationModule>();

        if (composeSutProject) {
            services.AddSutProjectValidators();
        }

        return services.BuildServiceProvider();
    }

    private static ValidationResult Validate(ServiceProvider provider, Deployment deployment) {
        using var scope = provider.CreateScope();

        return scope.ServiceProvider
            .GetRequiredService<ValidationRunner<Deployment>>()
            .Validate(deployment);
    }

    [Fact]
    public void WithTheFacetModuleComposed_FacetErrorsReportAtTheCurrentLevel() {
        using var provider = BuildProvider(composeSutProject: true);

        var result = Validate(provider, new Deployment { Environment = "prod", Version = 0 });

        Assert.Equal(
            [
                ("createdBy", ValidationCodes.Required),
                ("version", ValidationCodes.Range),
            ],
            result.Errors.Select(error => (error.Field, error.Code)));
    }

    [Fact]
    public void WithTheFacetModuleComposed_AValidValuePasses() {
        using var provider = BuildProvider(composeSutProject: true);

        var result = Validate(provider, new Deployment {
            CreatedBy = "ada", Version = 1, Environment = "prod",
        });

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// Failure is loud, never a silent skip: a collector whose services cannot supply the facet's
    /// validator throws naming the module to compose.
    /// </summary>
    [Fact]
    public void WithoutTheFacetModule_TheThrowNamesIt() {
        using var provider = BuildProvider(composeSutProject: false);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            Validate(provider, new Deployment { CreatedBy = "ada", Version = 1, Environment = "prod" }));

        Assert.Contains("AddSutProjectValidators()", thrown.Message);
        Assert.Contains("IValidatorFor<IAudited>", thrown.Message);
    }

    /// <summary>A collector started without services at all gets the same answer.</summary>
    [Fact]
    public void WithoutServicesOnTheCollector_TheThrowNamesIt() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);
        var validator = new DeploymentValidator();

        var deployment = new Deployment { CreatedBy = "ada", Version = 1, Environment = "prod" };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => validator.Validate(ref context, deployment));

        Assert.Contains("AddSutProjectValidators()", thrown.Message);
    }
}
