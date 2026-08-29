using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>Code</c> on a constraint, with and without a <c>Message</c> beside it.
/// </summary>
/// <remarks>
/// errors.md calls <c>Code</c> a wire contract: it is the stable thing a client switches on, and
/// the one part of an error a consumer is told to build UI against. A constraint that carries one
/// and drops it is worse than one that rejects it, because the default code that ships instead is
/// a plausible value the client will happily match against the wrong branch.
/// </remarks>
public class CustomCodeTests {

    private static string Model(string constraint) => $$"""
        using ValidationModules.Constraints;

        namespace Sample;

        public record Pet {
            {{constraint}}
            public string? Name { get; init; }
        }
        """;

    [Fact]
    public void CodeWithMessage_EmitsBoth() {
        // The path that already worked, kept as the control.
        var source = GeneratorHarness.Run(
            Model("""[Required(Code = "pet_name_missing", Message = "Give the pet a name.")]"""));

        Assert.Contains("\"pet_name_missing\"", source.Sources["Sample.PetValidator.g.cs"]);
        Assert.Contains("Give the pet a name.", source.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void CodeWithoutMessage_StillEmitsTheCode() {
        // Read only inside the Message branch, so setting it alone was discarded in silence - no
        // diagnostic, and a default code on the wire. The documented example in errors.md is
        // exactly this shape.
        var source = GeneratorHarness.Run(Model("""[Required(Code = "pet_name_missing")]"""));

        Assert.Contains("\"pet_name_missing\"", source.Sources["Sample.PetValidator.g.cs"]);
    }

    [Theory]
    [InlineData("""[Required(Code = "c")]""")]
    [InlineData("""[StringLength(2, 40, Code = "c")]""")]
    [InlineData("""[Pattern("^[a-z]+$", Code = "c")]""")]
    public void CodeWithoutMessage_AcrossKinds_StillEmitsTheCode(string constraint) {
        // Each kind reaches the emitter through a different helper, and the code was dropped by all
        // of them for the same reason.
        var source = GeneratorHarness.Run(Model(constraint));

        Assert.Contains("\"c\"", source.Sources["Sample.PetValidator.g.cs"]);
    }

    [Fact]
    public void CodeWithoutMessage_KeepsTheComposedDefaultMessage() {
        // The reason this is not a one-line fix: the default text is composed by the runtime helper
        // from the constraint's own bounds. Overriding the code must not cost the message.
        var source = GeneratorHarness.Run(Model("""[StringLength(2, 40, Code = "too_long")]"""));

        var emitted = source.Sources["Sample.PetValidator.g.cs"];

        Assert.Contains("\"too_long\"", emitted);

        // Still carries the runtime-owned bounds template rather than a literal the emitter
        // duplicated - the hoisted info holds the template and the bounds, and the report carries
        // the overridden code beside it.
        Assert.Contains("global::ValidationModules.ValidationMessageTemplates.StringLengthBetween, 2, 40", emitted);
        Assert.Contains("ctx.Report(\"name\", \"too_long\"", emitted);
    }
}
