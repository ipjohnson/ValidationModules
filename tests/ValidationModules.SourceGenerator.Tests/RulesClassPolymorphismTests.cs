using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// <c>rules.Nested</c> and <c>rules.Each</c> passing a <c>Polymorphism</c>: the dispatch
/// <c>[ValidateNested]</c> gets, asked for from a rules class.
/// </summary>
/// <remarks>
/// <para>
/// The region owns the walk. It pushes the path and then calls a method the validator writes for
/// the descent, whose body is the one a <c>[ValidateNested]</c> descent gets. So each mode has one
/// implementation, and a rules class for a model its author cannot change runs the rules for the
/// value's actual type.
/// </para>
/// <para>
/// These check what is written and that it compiles. <c>SutProject</c> runs the same shapes.
/// </para>
/// </remarks>
public class RulesClassPolymorphismTests
{
    private const string Animals = """
        public class Animal {
            [Required] public string? Name { get; init; }
        }

        public sealed class Dog : Animal {
            [Required] public string? Breed { get; init; }
        }

        public sealed record Owner {
            public bool Strict { get; init; }
            public Animal? Pet { get; init; }
            public IReadOnlyList<Animal>? Pets { get; init; }
        }
        """;

    private static GeneratorHarness.Result Run(
        string statements,
        params (string Key, string Value)[] buildProperties
    ) =>
        GeneratorHarness.Run(
            $$"""
            using System.Collections.Generic;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            {{Animals}}

            public sealed class OwnerRules : IValidationRulesFor<Owner> {
                public static void Describe(ValidationRules<Owner> rules, Owner x) {
                    {{statements}}
                }
            }
            """,
            buildProperties
        );

    /// <summary>A run that compiles and reports nothing, with the two files that matter.</summary>
    private static (string Validator, string Region) Clean(string statements)
    {
        var result = Run(statements);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);

        return (
            result.Sources["Sample.OwnerValidator.g.cs"],
            result.Sources["Sample.OwnerRules_Rules.g.cs"]
        );
    }

    private static int Count(string text, string fragment) =>
        Regex.Matches(text, Regex.Escape(fragment)).Count;

    // -- CompileTime ----------------------------------------------------------------------------

    [Theory]
    [InlineData(
        "rules.Nested(x.Pet, Polymorphism.CompileTime);",
        "DescendPetCompileTime",
        "validator.DescendPetCompileTime(ref ctx0, nested0).ShouldStop"
    )]
    [InlineData(
        "rules.Each(x.Pets, Polymorphism.CompileTime);",
        "DescendPetsCompileTime",
        "validator.DescendPetsCompileTime(ref elementCtx0, element0).ShouldStop"
    )]
    public void CompileTime_TheRegionCallsTheValidatorsTypeSwitch(
        string statement,
        string method,
        string call
    )
    {
        var (validator, region) = Clean(statement);

        Assert.Contains(
            $"internal global::ValidationModules.ValidationFlow {method}(ref global::ValidationModules.ValidationContext context, global::Sample.Animal value)",
            validator
        );
        Assert.Contains("switch (value)", validator);
        Assert.Contains("case global::Sample.Dog __typed:", validator);
        Assert.Contains("private global::Sample.DogValidator? _dispatch0;", validator);
        Assert.Contains(
            "if ((_dispatch0 ??= new()).Validate(ref context, __typed).ShouldStop)",
            validator
        );

        // An Animal that is not a Dog runs the injected validators for Animal, in the default arm,
        // as a [ValidateNested] descent's switch does.
        var defaultArm = validator.IndexOf("default:", StringComparison.Ordinal);
        var declared = validator.IndexOf(
            "validators[vi].Validate(ref context, value)",
            StringComparison.Ordinal
        );

        Assert.True(defaultArm > 0 && declared > defaultArm);

        // The validator hands itself to the region, and the region's walk calls back into it.
        Assert.Contains(".Describe(ref ctx, value, this).ShouldStop", validator);
        Assert.Contains("global::Sample.Owner x, global::Sample.OwnerValidator validator)", region);
        Assert.Contains(call, region);
        Assert.DoesNotContain("Validators", region);
    }

    /// <summary>
    /// A subtype's validator is one field on the validator, however many descents dispatch to it.
    /// </summary>
    [Fact]
    public void DescentsDispatchingToOneSubtype_ShareItsField()
    {
        var (validator, region) = Clean(
            """
            rules.Nested(x.Pet, Polymorphism.CompileTime);
                    rules.Each(x.Pets, Polymorphism.CompileTime);
            """
        );

        Assert.Equal(1, Count(validator, "global::Sample.DogValidator? _dispatch"));
        Assert.Equal(2, Count(validator, "(_dispatch0 ??= new())"));

        // One parameter carries the validator for every descent that calls into it.
        Assert.Equal(1, Count(region, "global::Sample.OwnerValidator validator"));
    }

    // -- Runtime --------------------------------------------------------------------------------

    [Theory]
    [InlineData(
        "rules.Nested(x.Pet, Polymorphism.Runtime);",
        "DescendPetRuntime",
        "\"pet\"",
        "validator.DescendPetRuntime(ref ctx0, nested0).ShouldStop"
    )]
    [InlineData(
        "rules.Each(x.Pets, Polymorphism.Runtime);",
        "DescendPetsRuntime",
        "\"pets\"",
        "validator.DescendPetsRuntime(ref elementCtx0, element0).ShouldStop"
    )]
    public void Runtime_TheRegionCallsTheValidatorsLookup(
        string statement,
        string method,
        string field,
        string call
    )
    {
        var result = Run(statement);

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);

        var validator = result.Sources["Sample.OwnerValidator.g.cs"];

        Assert.Contains($"internal global::ValidationModules.ValidationFlow {method}(", validator);
        Assert.Contains(
            $"if (global::ValidationModules.DynamicValidation.Validate(ref context, value, {field}, \"Owner\").ShouldStop)",
            validator
        );
        Assert.DoesNotContain("switch (", validator);
        Assert.Contains(call, result.Sources["Sample.OwnerRules_Rules.g.cs"]);

        // The lookup finds adapters, and a rules-class Runtime descent is the only one in this
        // assembly, so it alone has to be enough to emit them.
        var emitted = string.Concat(result.Sources.Values);

        Assert.Contains(
            "internal sealed class DogDynamicValidator : global::ValidationModules.IDynamicValidator",
            emitted
        );
        Assert.Contains("new global::ValidationModules.DynamicValidatorRegistry(", emitted);
    }

    // -- DeclaredOnly ---------------------------------------------------------------------------

    /// <summary>
    /// <c>DeclaredOnly</c> is the default, so passing it writes what omitting it writes. Only
    /// VM3111 tells the two apart.
    /// </summary>
    [Theory]
    [InlineData("rules.Nested(x.Pet);", "rules.Nested(x.Pet, Polymorphism.DeclaredOnly);")]
    [InlineData("rules.Each(x.Pets);", "rules.Each(x.Pets, Polymorphism.DeclaredOnly);")]
    [InlineData(
        "rules.Require(x.Pet).Nested();",
        "rules.Require(x.Pet).Nested(Polymorphism.DeclaredOnly);"
    )]
    public void DeclaredOnly_WritesWhatOmittingTheModeWrites(string omitted, string passed)
    {
        var implicitRun = Run(omitted);
        var explicitRun = Run(passed);

        Assert.Empty(explicitRun.CompilationErrors);
        Assert.Contains(implicitRun.Diagnostics, d => d.Id == "VM3111");
        Assert.DoesNotContain(explicitRun.Diagnostics, d => d.Id == "VM3111");
        Assert.Equal(implicitRun.Sources, explicitRun.Sources);
        Assert.DoesNotContain("Descend", string.Concat(explicitRun.Sources.Values));
    }

    // -- chains and conditions ------------------------------------------------------------------

    /// <summary>
    /// The chained forms take the mode too, and a failed <c>Require</c> earlier in the chain still
    /// skips the descent.
    /// </summary>
    [Theory]
    [InlineData(
        "rules.Require(x.Pet).Nested(Polymorphism.CompileTime);",
        "if (!missingPet && x.Pet is { } nested0)",
        "validator.DescendPetCompileTime(ref ctx0, nested0)"
    )]
    [InlineData(
        "rules.Count(x.Pets, 1, 5).Each(Polymorphism.Runtime);",
        "if (x.Pets is not null && (x.Pets.Count < 1 || x.Pets.Count > 5)",
        "validator.DescendPetsRuntime(ref elementCtx0, element0)"
    )]
    public void TheChainedForms_Dispatch(string statement, string guard, string call)
    {
        var (_, region) = Clean(statement);

        Assert.Contains(guard, region);
        Assert.Contains(call, region);
    }

    /// <summary>
    /// Each descent keeps the mode it passed, so two into one property can differ. Every mode gets
    /// its own method, and a <c>DeclaredOnly</c> descent still walks the injected array.
    /// </summary>
    [Fact]
    public void DescentsIntoOnePropertyWithDifferentModes_EachDispatchAsTheyAsk()
    {
        var (validator, region) = Clean(
            """
            if (x.Strict) {
                        rules.Nested(x.Pet, Polymorphism.CompileTime);
                    } else if (x.Pet is not null) {
                        rules.Nested(x.Pet, Polymorphism.Runtime);
                    } else {
                        rules.Nested(x.Pet, Polymorphism.DeclaredOnly);
                    }
            """
        );

        Assert.Contains("DescendPetCompileTime(", validator);
        Assert.Contains("DescendPetRuntime(", validator);
        Assert.Contains("validator.DescendPetCompileTime(ref ctx0, nested0)", region);
        Assert.Contains("validator.DescendPetRuntime(ref ctx1, nested1)", region);
        Assert.Contains("petValidators[vi2].Validate(ref ctx2, nested2)", region);

        // The injected sets keep the places they always had, and the validator comes after them.
        Assert.Contains(
            "global::ValidationModules.IValidatorFor<global::Sample.Animal>[] petValidators, global::Sample.OwnerValidator validator)",
            region
        );
        Assert.Contains(".Describe(ref ctx, value, PetValidators, this).ShouldStop", validator);
    }

    /// <summary>
    /// The region transcribes the body's own parameters and locals, so the parameter holding the
    /// validator takes a name the body does not use.
    /// </summary>
    [Theory]
    [InlineData(
        "Owner validator",
        "var validator1 = 1; rules.Ensure(validator1 > 0, field: \"one\"); rules.Nested(validator.Pet, Polymorphism.CompileTime);",
        "validator2"
    )]
    [InlineData(
        "Owner x",
        "var validator = x.Strict; rules.Ensure(validator || x.Pet is null); rules.Nested(x.Pet, Polymorphism.CompileTime);",
        "validator1"
    )]
    public void TheValidatorParameter_TakesANameTheBodyDoesNotUse(
        string subject,
        string body,
        string parameter
    )
    {
        var result = GeneratorHarness.Run(
            $$"""
            using System.Collections.Generic;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            {{Animals}}

            public sealed class OwnerRules : IValidationRulesFor<Owner> {
                public static void Describe(ValidationRules<Owner> rules, {{subject}}) {
                    {{body}}
                }
            }
            """
        );

        Assert.Empty(result.CompilationErrors);

        var region = result.Sources["Sample.OwnerRules_Rules.g.cs"];

        Assert.Contains($"global::Sample.OwnerValidator {parameter})", region);
        Assert.Contains($"{parameter}.DescendPetCompileTime(", region);
    }

    // -- where the validator lives --------------------------------------------------------------

    /// <summary>
    /// The region names the validator's type for the parameter it is passed, so the name has to
    /// resolve wherever the target is declared and whatever its accessibility.
    /// </summary>
    [Theory]
    [InlineData("namespace Sample;", "public", "", "")]
    [InlineData("", "public", "", "")]
    [InlineData("namespace Sample;", "internal", "", "")]
    [InlineData("namespace Sample;", "public", "public static class Outer {", "}")]
    public void TheValidatorParameter_ResolvesWhereverTheTargetIsDeclared(
        string ns,
        string access,
        string open,
        string close
    )
    {
        var target = open.Length == 0 ? "Owner" : "Outer.Owner";
        var result = GeneratorHarness.Run(
            $$"""
            using ValidationModules;
            using ValidationModules.Constraints;

            {{ns}}

            {{access}} class Animal {
                [Required] public string? Name { get; init; }
            }

            {{access}} sealed class Dog : Animal {
                [Required] public string? Breed { get; init; }
            }

            {{open}}
            {{access}} sealed record Owner {
                public Animal? Pet { get; init; }
            }
            {{close}}

            {{access}} sealed class OwnerRules : IValidationRulesFor<{{target}}> {
                public static void Describe(ValidationRules<{{target}}> rules, {{target}} x) =>
                    rules.Nested(x.Pet, Polymorphism.CompileTime);
            }
            """
        );

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Contains("DescendPetCompileTime(", string.Concat(result.Sources.Values));
    }

    /// <summary>
    /// The method is the validator's, so <c>ValidationModules_FailFast</c> governs it as it
    /// governs the attribute's descent.
    /// </summary>
    [Fact]
    public void WithFailFastOff_TheMethodDiscardsTheAnswer()
    {
        var result = Run(
            "rules.Nested(x.Pet, Polymorphism.CompileTime);",
            ("ValidationModules_FailFast", "Disabled")
        );

        Assert.Empty(result.CompilationErrors);

        var validator = result.Sources["Sample.OwnerValidator.g.cs"];

        Assert.Contains("(_dispatch0 ??= new()).Validate(ref context, __typed);", validator);
        Assert.Contains("validators[vi].Validate(ref context, value);", validator);
        Assert.DoesNotContain("ShouldStop", validator);
    }

    // -- diagnostics ----------------------------------------------------------------------------

    private static GeneratorHarness.Result RunSealed(string call) =>
        GeneratorHarness.Run(
            $$"""
            using System.Collections.Generic;
            using ValidationModules;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Address {
                [Required] public string? Street { get; init; }
            }

            public sealed record Person {
                public Address? Home { get; init; }
                public IReadOnlyList<Address>? Homes { get; init; }
            }

            public sealed class PersonRules : IValidationRulesFor<Person> {
                public static void Describe(ValidationRules<Person> rules, Person x) {
                    {{call}};
                }
            }
            """
        );

    /// <summary>
    /// A sealed type's runtime type is its declared type, so <c>Runtime</c> on it is a container
    /// lookup for nothing. Reported at the call, where the attribute reports it at the property.
    /// </summary>
    [Theory]
    [InlineData("rules.Nested(x.Home, Polymorphism.Runtime)")]
    [InlineData("rules.Each(x.Homes, Polymorphism.Runtime)")]
    public void RuntimeOnASealedTarget_IsVM1504AtTheCall(string call)
    {
        var result = RunSealed(call);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "VM1504");

        Assert.Equal(
            "'Address' is sealed, so its runtime type can never differ from its declared type and "
                + "dispatching on it costs a container lookup for the same answer. Use "
                + "Polymorphism.DeclaredOnly",
            diagnostic.GetMessage()
        );
        Assert.Equal(
            call,
            diagnostic
                .Location.SourceTree!.GetText(TestContext.Current.CancellationToken)
                .ToString(diagnostic.Location.SourceSpan)
        );
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// <c>CompileTime</c> on a sealed type is allowed, as it is on the attribute. There is nothing
    /// to switch over, so the method runs the declared type's validators.
    /// </summary>
    [Fact]
    public void CompileTimeOnASealedTarget_RunsTheDeclaredTypesValidators()
    {
        var result = RunSealed("rules.Nested(x.Home, Polymorphism.CompileTime)");

        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);

        var validator = result.Sources["Sample.PersonValidator.g.cs"];

        Assert.Contains("DescendHomeCompileTime(", validator);
        Assert.DoesNotContain("switch (", validator);
        Assert.Contains("validators[vi].Validate(ref context, value)", validator);
    }

    /// <summary>
    /// The mode decides which code is written, so it has to be a constant, as an attribute
    /// argument always is.
    /// </summary>
    [Fact]
    public void AModeThatIsNotAConstant_IsVM3001()
    {
        var result = Run(
            """
            var mode = x.Strict ? Polymorphism.CompileTime : Polymorphism.DeclaredOnly;
                    rules.Nested(x.Pet, mode);
            """
        );

        Assert.Contains(
            "a Polymorphism argument that is not a constant, 'mode' (pass Polymorphism.CompileTime, "
                + "Polymorphism.Runtime or Polymorphism.DeclaredOnly)",
            Assert.Single(result.Diagnostics, d => d.Id == "VM3001").GetMessage()
        );
        Assert.Empty(result.CompilationErrors);
    }
}
