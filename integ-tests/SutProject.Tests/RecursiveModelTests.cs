using Microsoft.Extensions.DependencyInjection;
using SutProject.Nesting;
using ValidationModules;
using Xunit;

namespace SutProject.Tests;

/// <summary>
/// A model that reaches itself, resolved out of the container rather than constructed directly.
/// </summary>
/// <remarks>
/// Constructing the validator by hand never showed this: the parameterless constructor is the one a
/// test calls, and it falls back to the generated nested validators. The container calls the other
/// one, which asks for <c>IEnumerable&lt;IValidatorFor&lt;Node&gt;&gt;</c> - itself. MS.DI reports a
/// cycle, and because ASP.NET Core turns <c>ValidateOnBuild</c> on in Development, the application
/// does not start. nesting.md documents this exact shape as supported, and category trees, comment
/// threads, BOMs and org charts all have it.
/// </remarks>
public class RecursiveModelTests {

    private static ServiceProvider Provider() {
        var services = new ServiceCollection();
        services.AddSutProjectValidators();

        // What WebApplicationBuilder does in Development, and the whole point of this test: the
        // cycle is a resolution-time failure that a lazy container would not report until the first
        // request.
        return services.BuildServiceProvider(new ServiceProviderOptions {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void SelfReferentialModel_DoesNotBlockContainerValidation() {
        using var provider = Provider();

        Assert.NotNull(provider.GetRequiredService<IValidatorFor<Node>>());
    }

    [Fact]
    public void SelfReferentialModel_ResolvedFromTheContainer_StillValidatesDownTheTree() {
        // Resolving it is not enough - a fix that broke the cycle by dropping the nested descent
        // would pass the test above and validate nothing.
        using var provider = Provider();

        var validator = provider.GetRequiredService<IValidatorFor<Node>>();

        var node = new Node { Label = "a", Child = new Node { Label = "b", Child = new Node() } };

        Assert.Equal("child.child.label", Assert.Single(validator.Validate(node).Errors).Field);
    }
}
