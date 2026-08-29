using ValidationModules.SourceGenerator.Tests.Infrastructure;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// The structured-error emission from docs/structured-errors.md: hoisted static message infos,
/// value capture and its compile-time kill switch, and the message fixes that rode in with the
/// reshape - {field} substitution, DataAnnotations template baking, resx compilation, the denied
/// wording, and exclusive range wording.
/// </summary>
public class StructuredEmissionTests {

    private const string InfoType = "global::ValidationModules.ValidationMessageInfo";
    private const string Templates = "global::ValidationModules.ValidationMessageTemplates";

    private static string Emit(string source, params (string Key, string Value)[] properties) {
        var result = GeneratorHarness.Run(source, properties);

        Assert.Empty(result.CompilationErrors);

        return result.Sources.Single(pair => pair.Key.Contains("Validator.g.cs")).Value;
    }

    [Fact]
    public void CaptureValuesOff_LeavesNoValueInTheBinary() {
        var emitted = Emit("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Pet {
                [Required]
                [StringLength(min: 1, max: 100)]
                public string? Name { get; init; }

                [Range(0, 30)]
                public int Age { get; init; }
            }
            """, ("ValidationModules_CaptureValues", "false"));

        // The helper call carries no value argument, and the structured reports pass null - the
        // capture is absent from the compiled output, not merely disabled at run time.
        Assert.DoesNotContain("value: value.", emitted);
        Assert.Contains(
            "ctx.Report(\"age\", global::ValidationModules.ValidationCodes.Range, null, _message",
            emitted);
    }

    [Fact]
    public void IdenticalConstraints_ShareOneHoistedInfo() {
        var emitted = Emit("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Pet {
                [StringLength(min: 1, max: 100)]
                public string? Name { get; init; }

                [StringLength(min: 1, max: 100)]
                public string? Nickname { get; init; }
            }
            """);

        var fields = emitted.Split($"new {InfoType}(").Length - 1;

        Assert.Equal(1, fields);
        Assert.Contains($"{Templates}.StringLengthBetween, 1, 100", emitted);
    }

    [Fact]
    public void MessageOverride_SubstitutesFieldAtGenerationTime() {
        var emitted = Emit("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Pet {
                [Required(Message = "{field} really needs a value!")]
                public string? Name { get; init; }
            }
            """);

        // The XML-doc contract on ValidationConstraintAttribute.Message, finally honoured - and
        // at generation time, where the field is a literal, not per failure.
        Assert.Contains("\"name really needs a value!\"", emitted);
        Assert.DoesNotContain("{field}", emitted);
    }

    [Fact]
    public void DataAnnotationsComposite_BakesConstantsAndDisplayName() {
        var emitted = Emit("""
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class Customer {
                [StringLength(3, ErrorMessage = "The field {0} is over {1} chars")]
                public string? Name { get; set; }
            }
            """);

        // {1} is StringLength's maximum - the attribute's own FormatErrorMessage order - baked as
        // a constant; {0} is the display name the front end resolved. Classic DataAnnotations
        // formatted this per failure; here the wire carries finished text.
        Assert.Contains("\"The field Name is over 3 chars\"", emitted);
    }

    [Fact]
    public void ResourceMessages_CompileToAPerRenderPropertyRead() {
        var emitted = Emit("""
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public static class Msgs {
                public static string NameRequired => "{0} fehlt.";
            }

            public class Customer {
                [Required(ErrorMessageResourceType = typeof(Msgs), ErrorMessageResourceName = "NameRequired")]
                public string? Name { get; set; }
            }
            """);

        // A direct static property read, wrapped so it is consulted per render - which is what
        // lets a resx accessor's culture fallback work - with the holes rendered in
        // DataAnnotations' own dialect. Nothing resolves reflectively.
        Assert.Contains(
            "new global::ValidationModules.DelegateMessageProvider(static () => global::Sample.Msgs.NameRequired)",
            emitted);
        Assert.Contains("DataAnnotationsHoles = true", emitted);
    }

    [Fact]
    public void DeniedValues_GetTheNegatedTemplate() {
        var emitted = Emit("""
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public class Customer {
                [DeniedValues("admin", "root")]
                public string? Role { get; set; }
            }
            """);

        Assert.Contains($"{Templates}.DeniedValues, \"admin, root\"", emitted);
        Assert.DoesNotContain($"{Templates}.AllowedValues", emitted);
    }

    [Fact]
    public void ExclusiveRangeBounds_PickTheTemplateThatSaysSo() {
        var emitted = Emit("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Pet {
                [Range(0, 30, ExclusiveMin = true)]
                public int Age { get; init; }
            }
            """);

        Assert.Contains($"{Templates}.RangeGreaterAndAtMost, 0, 30", emitted);
    }

    [Fact]
    public void FlagsEnumDefined_CarriesTheCombinationTemplate() {
        var emitted = Emit("""
            using System;
            using ValidationModules.Constraints;

            namespace Sample;

            [Flags]
            public enum Traits { None = 0, Fluffy = 1, Loud = 2 }

            public sealed record Pet {
                [EnumDefined]
                public Traits Traits { get; init; }
            }
            """);

        Assert.Contains($"{Templates}.EnumFlags", emitted);
    }

    [Fact]
    public void ValueCapture_SitsInsideTheFailureBranch() {
        var emitted = Emit("""
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed record Pet {
                [Range(0, 30)]
                public int Age { get; init; }
            }
            """);

        // The report call - and with it the box for a failing value type - is a conjunct of the
        // failed test, so a clean pass allocates nothing for capture.
        Assert.Contains(
            "if ((value.Age < 0 || value.Age > 30) && ctx.Report(\"age\", " +
            "global::ValidationModules.ValidationCodes.Range, value.Age, _message0).ShouldStop)",
            emitted);
    }
}
