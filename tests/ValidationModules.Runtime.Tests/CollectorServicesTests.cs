using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Services ride on the collector, and <see cref="ValidationContext.Services"/> forwards to it.
/// </summary>
/// <remarks>
/// This is what gives a <c>rules.Apply(…)</c> rule a way to reach a dependency at all: its
/// <c>RuleAction&lt;T&gt;</c> signature has no other, which is why checks needing services currently
/// get hoisted into static helpers away from their declarations.
/// </remarks>
public class CollectorServicesTests {

    private static ServiceProvider Provider() =>
        new ServiceCollection().AddSingleton("injected").BuildServiceProvider();

    [Fact]
    public void ContextForwardsTheCollectorsServices() {
        using var provider = Provider();

        var context = new ValidationContext(new ValidationErrorCollector(provider));

        Assert.Same(provider, context.Services);
    }

    [Fact]
    public void WithoutServices_TheContextReportsNone() {
        Assert.Null(new ValidationContext(new ValidationErrorCollector()).Services);
    }

    /// <summary>
    /// A descent carries the services with it, because the context forwards rather than holding.
    /// </summary>
    [Fact]
    public void ServicesSurviveADescent() {
        using var provider = Provider();

        var context = new ValidationContext(new ValidationErrorCollector(provider));

        Assert.Same(provider, context.Push("home").PushIndex("lines", 0).Services);
    }

    /// <summary>
    /// <c>Reset</c> keeps the provider, and that is deliberate rather than an oversight: a collector
    /// belongs to one unit of work and carries that scope's services, so reuse within a scope is the
    /// point. Crossing a scope means a new collector.
    /// </summary>
    [Fact]
    public void ResetKeepsTheServices() {
        using var provider = Provider();
        var collector = new ValidationErrorCollector(provider);

        collector.Reset();

        Assert.Same(provider, collector.Services);
    }

    /// <summary>
    /// Constructor-only with no setter is what encodes the invariant in the type: re-arming a
    /// pooled collector for a different scope is not expressible.
    /// </summary>
    [Fact]
    public void ServicesHasNoSetter() {
        var property = typeof(ValidationErrorCollector).GetProperty(nameof(ValidationErrorCollector.Services));

        Assert.NotNull(property);
        Assert.Null(property!.SetMethod);
    }

    [Fact]
    public void PathModeIsUnaffectedByCarryingServices() {
        using var provider = Provider();

        Assert.Equal(
            ValidationPathMode.Full,
            new ValidationErrorCollector(provider, ValidationPathMode.Full).PathMode);
    }

    /// <summary>
    /// The motivating case: an applied rule reaching a dependency at its declaration site.
    /// </summary>
    [Fact]
    public void AnAppliedRuleCanReachServicesThroughTheContext() {
        using var provider = Provider();

        var collector = new ValidationErrorCollector(provider);
        var context = new ValidationContext(collector);

        static void Rule(ref ValidationContext context, string value) {
            if (context.Services?.GetService(typeof(string)) is string expected && value != expected) {
                context.Report("value", "mismatch", $"expected '{expected}'.");
            }
        }

        Rule(ref context, "something else");

        Assert.Equal("mismatch", Assert.Single(collector.ToResult().Errors).Code);
    }
}
