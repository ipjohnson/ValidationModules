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
        new ValidationContext(collector).Push("home").Report("postalCode", "required", "x");

        collector.Reset();
        new ValidationContext(collector).Push("work").Report("postalCode", "required", "x");

        Assert.Equal("work.postalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void Reset_ReusedAcrossManyPasses_KeepsPathsCorrect() {
        var collector = new ValidationErrorCollector();

        for (var pass = 0; pass < 50; pass++) {
            collector.Reset();

            var context = new ValidationContext(collector);
            context.PushIndex("toys", pass).Push("owner").Report("name", "required", "x");

            Assert.Equal($"toys[{pass}].owner.name", Assert.Single(collector.ToResult().Errors).Field);
        }
    }

    [Fact]
    public void ToResult_SnapshotsRatherThanWrapping() {
        // A pooled collector resets under a result the caller is still holding. If ToResult wrapped
        // the live list instead of copying it, that result would silently empty.
        var collector = new ValidationErrorCollector();
        new ValidationContext(collector).Report("name", "required", "x");

        var result = collector.ToResult();
        collector.Reset();

        Assert.Single(result.Errors);
    }

    [Fact]
    public void Push_ManySequentialSiblings_EachKeepsItsOwnPath() {
        var collector = new ValidationErrorCollector();
        var context = new ValidationContext(collector);

        // One at a time, as a generated loop does: the buffer slot for this depth is rewritten per
        // iteration and read before the next iteration touches it.
        for (var i = 0; i < 100; i++) {
            context.PushIndex("toys", i).Report("name", "required", "x");
        }

        var fields = collector.ToResult().Errors.Select(error => error.Field).ToArray();

        Assert.Equal(100, fields.Length);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"toys[{i}].name"), fields);
    }

    [Fact]
    public void Reset_RepeatedPasses_KeepOrderAndCarryNothingOver() {
        // The chain is stored newest-first and unwound by ToResult, so declaration order depends on
        // that reversal rather than on the order things were linked. Getting it wrong shows up as
        // reversed errors or a stale one reappearing, neither of which a single-pass test catches.
        var collector = new ValidationErrorCollector();

        for (var pass = 0; pass < 5; pass++) {
            collector.Reset();

            var context = new ValidationContext(collector);
            for (var i = 0; i < 4 + pass; i++) {
                context.Report($"field{i}", "required", $"pass {pass}");
            }

            var errors = collector.ToResult().Errors;

            Assert.Equal(4 + pass, errors.Count);
            Assert.Equal(
                Enumerable.Range(0, 4 + pass).Select(i => $"field{i}"),
                errors.Select(error => error.Field));
            Assert.All(errors, error => Assert.Equal($"pass {pass}", error.Message));
        }
    }

    [Fact]
    public void Reset_ShrinkingPass_DoesNotLeakTheLongerPassBehindIt() {
        var collector = new ValidationErrorCollector();
        var first = new ValidationContext(collector);

        for (var i = 0; i < 10; i++) {
            first.Report($"field{i}", "required", "x");
        }

        collector.Reset();
        new ValidationContext(collector).Report("only", "required", "x");

        Assert.Equal("only", Assert.Single(collector.ToResult().Errors).Field);
        Assert.Equal(1, collector.Count);
    }

    [Fact]
    public void Add_PrePathedError_IsTakenAsGiven() {
        var collector = new ValidationErrorCollector();

        collector.Add(new ValidationError("Home.PostalCode", "required", "x"));

        Assert.Equal("Home.PostalCode", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public async Task ConcurrentValidations_EachWithItsOwnCollector_RecordEveryPathExactly() {
        // A pass is single-threaded: its contexts share one depth-indexed path buffer, so two
        // branches walking at once would overwrite each other's segments. Concurrency is supported
        // by giving each branch its own collector and merging, which also measured faster than
        // sharing one under a lock.
        const int branches = 200;

        using var gate = new SemaphoreSlim(0);

        var tasks = Enumerable.Range(0, branches).Select(i => Task.Run(async () => {
            await gate.WaitAsync(TestContext.Current.CancellationToken);

            var collector = new ValidationErrorCollector();
            var context = new ValidationContext(collector);
            context.PushIndex("toys", i).Report("name", "required", "x");

            return collector.ToResult();
        })).ToArray();

        gate.Release(branches);
        var results = await Task.WhenAll(tasks);

        var fields = results.SelectMany(result => result.Errors).Select(error => error.Field).ToArray();

        Assert.Equal(branches, fields.Length);
        Assert.Equal(branches, fields.Distinct().Count());
    }
}
