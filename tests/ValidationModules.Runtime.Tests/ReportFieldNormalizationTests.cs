using ValidationModules.Naming;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// <c>Report(field, …)</c> spells a bare identifier the way the pass's field namer would.
/// </summary>
/// <remarks>
/// The defect this pins: a generated <c>[Pattern]</c> reported <c>FIELD='deviceId'</c> while a
/// hand-written <c>IAsyncValidatorFor&lt;T&gt;</c> calling
/// <c>Report(nameof(value.DeviceId), …)</c> reported <c>FIELD='DeviceId'</c> - one property, one
/// runner, one result list, two wire names. The rules front end rewrites <c>nameof</c> at build
/// time; hand-written validators are runtime code the generator never sees, so the runtime is the
/// only place left to normalize.
/// </remarks>
public class ReportFieldNormalizationTests {

    private sealed class NamerProvider(IValidationFieldNamer namer) : IServiceProvider {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IValidationFieldNamer) ? namer : null;
    }

    private static ValidationResult Report(string field, IValidationFieldNamer? namer = null) {
        var collector = namer is null
            ? new ValidationErrorCollector()
            : new ValidationErrorCollector(new NamerProvider(namer));
        var context = new ValidationContext(collector);

        context.Report(field, "code", "message");

        return collector.ToResult();
    }

    [Fact]
    public void ABareIdentifier_GoesThroughThePassesNamer() {
        var result = Report("DeviceId", CamelCaseFieldNamer.Instance);

        Assert.Equal("deviceId", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void TheNamerIsThePolicyTheProjectChose_NotAHardCodedCase() {
        var result = Report("DeviceId", SnakeCaseFieldNamer.Instance);

        Assert.Equal("device_id", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void AnIndexedField_IsAlreadyShapedAndPassesVerbatim() {
        var result = Report("steps[0]", CamelCaseFieldNamer.Instance);

        Assert.Equal("steps[0]", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void ADottedPath_IsAlreadyShapedAndPassesVerbatim() {
        var result = Report("Owner.Name", CamelCaseFieldNamer.Instance);

        Assert.Equal("Owner.Name", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void APassWithoutServices_HasNoPolicyToConsultAndKeepsTheFieldVerbatim() {
        // A PascalCase project validating outside DI would otherwise have its correct CLR-name
        // fields rewritten by a guessed default.
        var result = Report("DeviceId");

        Assert.Equal("DeviceId", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void TheStructuredOverload_NormalizesTheSameWay() {
        var collector = new ValidationErrorCollector(new NamerProvider(CamelCaseFieldNamer.Instance));
        var context = new ValidationContext(collector);

        context.Report(
            "DeviceId", "code", value: null,
            new ValidationMessageInfo("{field} is wrong."));

        Assert.Equal("deviceId", Assert.Single(collector.ToResult().Errors).Field);
    }

    [Fact]
    public void ANestedReport_NormalizesTheFieldButNotThePathAbove() {
        var collector = new ValidationErrorCollector(new NamerProvider(CamelCaseFieldNamer.Instance));
        var context = new ValidationContext(collector);
        var nested = context.Push("order");

        nested.Report("DeviceId", "code", "message");

        Assert.Equal("order.deviceId", Assert.Single(collector.ToResult().Errors).Field);
    }
}
