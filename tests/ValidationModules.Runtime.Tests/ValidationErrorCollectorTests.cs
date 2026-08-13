using ValidationModules.Runtime.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.Runtime.Tests;

public class ValidationErrorCollectorTests {

    [Fact]
    public void ToResult_NoErrors_ReturnsTheSharedValidInstance() {
        var collector = new ValidationErrorCollector();

        Assert.Same(ValidationResult.Valid, collector.ToResult());
    }

    [Fact]
    public void Reset_ClearsErrors() {
        var collector = new ValidationErrorCollector();
        new ValidationContext(collector).Push("home").Add("postalCode", "required", "x");

        collector.Reset();
        new ValidationContext(collector).Push("work").Add("postalCode", "required", "x");

        Assert.Equal("work.postalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Reset_ReusedAcrossManyPasses_KeepsPathsCorrect() {
        var collector = new ValidationErrorCollector();

        for (var pass = 0; pass < 50; pass++) {
            collector.Reset();

            var context = new ValidationContext(collector);
            context.PushIndex("toys", pass).Push("owner").Add("name", "required", "x");

            Assert.Equal($"toys[{pass}].owner.name", Assert.Single(collector.ToResult().Errors).Field);
        }
    }

    [Fact]
    public void ToResult_SnapshotsRatherThanWrapping() {
        // A pooled collector resets under a result the caller is still holding. If ToResult wrapped
        // the live list instead of copying it, that result would silently empty.
        var collector = new ValidationErrorCollector();
        new ValidationContext(collector).Add("name", "required", "x");

        var result = collector.ToResult();
        collector.Reset();

        Assert.Single(result.Errors);
    }

    [Fact]
    public void Push_ManySiblings_EachKeepsItsOwnPath() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        // Held simultaneously and added to out of order. Each context owns its path outright, so
        // there is no shared buffer here to grow, overwrite or exhaust.
        var contexts = Enumerable.Range(0, 100).Select(i => context.PushIndex("toys", i)).ToArray();

        foreach (var (child, index) in contexts.Select((child, index) => (child, index))) {
            child.Add("name", "required", "x");

            Assert.Equal($"toys[{index}].name", collector.ToResult().Errors[index].Field);
        }
    }

    [Fact]
    public void Add_PrePathedError_IsTakenAsGiven() {
        var collector = new ValidationErrorCollector();

        collector.Add(new ValidationError("Home.PostalCode", "required", "x"));

        Assert.Equal("Home.PostalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public async Task CreateSynchronized_ConcurrentBranches_RecordEveryPathExactly() {
        // The case a depth-indexed path stack cannot survive: every branch holds a context taken
        // before the others existed, and they all add at once.
        const int branches = 200;

        var collector = ValidationErrorCollector.CreateSynchronized();
        var context = new ValidationContext(collector);

        using var gate = new SemaphoreSlim(0);

        var tasks = Enumerable.Range(0, branches).Select(i => {
            var child = context.PushIndex("toys", i);

            return Task.Run(async () => {
                await gate.WaitAsync(TestContext.Current.CancellationToken);
                child.Add("name", "required", "x");
            });
        }).ToArray();

        gate.Release(branches);
        await Task.WhenAll(tasks);

        Assert.Equal(
            Enumerable.Range(0, branches).Select(i => $"toys[{i}].name").OrderBy(field => field, StringComparer.Ordinal),
            collector.ToResult().Errors.Select(error => error.Field).OrderBy(field => field, StringComparer.Ordinal));
    }

    [Fact]
    public void Profile_IsCarriedThroughToTheContext() {
        var collector = ValidationErrorCollector.CreateSynchronized(typeof(Strict));

        Assert.Equal(typeof(Strict), collector.Profile);
    }
}
