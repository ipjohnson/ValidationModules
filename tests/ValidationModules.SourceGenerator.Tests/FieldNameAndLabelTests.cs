using Microsoft.CodeAnalysis;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Which name goes where: the field is <c>[JsonPropertyName]</c> or the naming policy, a
/// <c>[Display(Name)]</c> labels the message and nothing else, and a name resolved here reaches
/// the error without a second trip through the runtime's field namer.
/// </summary>
public class FieldNameAndLabelTests
{
    private static GeneratorHarness.Result Run(string source)
    {
        var result = GeneratorHarness.Run(source);

        Assert.Empty(result.CompilationErrors);

        return result;
    }

    private static string Validator(GeneratorHarness.Result result, string name) =>
        result.Sources.Single(pair => pair.Key.EndsWith($".{name}Validator.g.cs")).Value;

    [Fact]
    public void ADisplayName_LabelsTheMessage_AndTheFieldStaysTheWireName()
    {
        var result = Run(
            """
            using DA = System.ComponentModel.DataAnnotations;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Address {
                [Required, DA.Display(Name = "Postal code")]
                public string? PostalCode { get; init; }
            }
            """
        );

        var emitted = Validator(result, "Address");

        Assert.Contains(
            "ctx.Report(\"postalCode\", global::ValidationModules.ValidationCodes.Required",
            emitted
        );
        Assert.Contains(
            "new global::ValidationModules.ValidationMessageInfo(global::ValidationModules.ValidationMessageTemplates.Required) { DisplayName = \"Postal code\" }",
            emitted
        );
        Assert.DoesNotContain(
            "\"Postal code\", global::ValidationModules.ValidationCodes",
            emitted
        );
    }

    [Fact]
    public void JsonPropertyName_NamesTheField_WhicheverAttributeComesFirst()
    {
        var result = Run(
            """
            using System.Text.Json.Serialization;
            using DA = System.ComponentModel.DataAnnotations;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Person {
                [DA.Display(Name = "Given name"), JsonPropertyName("given_name"), StringLength(max: 20)]
                public string? GivenName { get; init; }
            }
            """
        );

        var emitted = Validator(result, "Person");

        Assert.Contains("ctx.Report(\"given_name\"", emitted);
        Assert.Contains("DisplayName = \"Given name\"", emitted);
    }

    [Fact]
    public void AnAuthoredMessage_NamesTheFieldByItsLabel()
    {
        var result = Run(
            """
            using DA = System.ComponentModel.DataAnnotations;
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Address {
                [Required(Message = "{field} needs a value."), DA.Display(Name = "Postal code")]
                public string? PostalCode { get; init; }
            }
            """
        );

        Assert.Contains(
            "ctx.ReportAuthored(\"postalCode\", global::ValidationModules.ValidationCodes.Required, \"Postal code needs a value.\")",
            Validator(result, "Address")
        );
    }

    [Fact]
    public void AResourceMessage_CarriesTheDataAnnotationsDisplayName()
    {
        // A resx template's {0} was written against the display name, which is the
        // [Display(Name)] value or else the property name. The error's field stays the wire name.
        var result = Run(
            """
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            public static class Res {
                public static string Missing => "{0} is missing";
            }

            public sealed class Person {
                [Required(ErrorMessageResourceType = typeof(Res), ErrorMessageResourceName = nameof(Res.Missing))]
                public string? LastName { get; init; }

                [Required(ErrorMessageResourceType = typeof(Res), ErrorMessageResourceName = nameof(Res.Missing))]
                [Display(Name = "First name")]
                public string? FirstName { get; init; }
            }
            """
        );

        var emitted = Validator(result, "Person");

        Assert.Contains("DataAnnotationsHoles = true, DisplayName = \"LastName\"", emitted);
        Assert.Contains("DataAnnotationsHoles = true, DisplayName = \"First name\"", emitted);
    }

    [Fact]
    public void ARulesClassRule_NamesTheFieldByPolicy_AndHoistsItsLabel()
    {
        var result = Run(
            """
            using DA = System.ComponentModel.DataAnnotations;
            using ValidationModules;

            namespace Sample;

            public sealed class Parcel {
                [DA.Display(Name = "Tracking number")]
                public string? TrackingNumber { get; init; }

                public string? Carrier { get; init; }
            }

            public sealed class ParcelRules : IValidationRulesFor<Parcel> {
                public static void Describe(ValidationRules<Parcel> rules, Parcel x) {
                    rules.Require(x.TrackingNumber).Length(5, 20);
                    rules.Require(x.Carrier);
                }
            }
            """
        );

        var region = result.Sources["Sample.ParcelRules_Rules.g.cs"];

        Assert.Contains(
            "ctx.Report(\"trackingNumber\", global::ValidationModules.ValidationCodes.Required, null, _message0)",
            region
        );
        Assert.Contains(
            "ValidationMessageTemplates.Required) { DisplayName = \"Tracking number\" }",
            region
        );
        Assert.Contains(
            "ValidationMessageTemplates.StringLengthBetween, 5, 20) { DisplayName = \"Tracking number\" }",
            region
        );

        // A property without a label keeps the shared helpers.
        Assert.Contains("ValidationContextExtensions.ReportRequired(ctx, \"carrier\")", region);
    }

    [Fact]
    public void TheGeneratedValidator_ReportsThroughAContextWithResolvedFieldNames()
    {
        var result = Run(
            """
            using ValidationModules.Constraints;

            namespace Sample;

            public sealed class Pet {
                [Required] public string? Name { get; init; }
            }
            """
        );

        var emitted = Validator(result, "Pet");

        Assert.Contains(
            "Validate(ref global::ValidationModules.ValidationContext context,",
            emitted
        );
        Assert.Contains("var ctx = context.WithResolvedFieldNames();", emitted);
    }

    [Fact]
    public void AnAppliedRule_GetsTheContextTheValidatorWasGiven()
    {
        // An Apply method is hand-written, so its nameof(...) fields still go through the namer.
        var result = Run(
            """
            using ValidationModules;

            namespace Sample;

            public sealed class Order {
                public string? Reference { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.Apply(Check);
                }

                internal static ValidationFlow Check(ref ValidationContext context, Order value) =>
                    ValidationFlow.Continue;
            }
            """
        );

        Assert.Contains(
            "global::Sample.OrderRules.Check(ref context, value)",
            Validator(result, "Order")
        );
    }

    [Fact]
    public void AFacetFromTheContainer_GetsAnOrdinaryContext()
    {
        // The container may hand back a hand-written validator for the facet.
        var shared = GeneratorHarness.CompileToReference(
            """
            namespace Shared;

            public interface IAudited {
                string? CreatedBy { get; }
            }
            """,
            "Shared.Contracts"
        );

        var result = GeneratorHarness.Run(
            """
            using Shared;
            using ValidationModules;

            namespace App;

            public sealed record Order : IAudited {
                public string? CreatedBy { get; init; }
            }

            public sealed class OrderRules : IValidationRulesFor<Order> {
                public static void Describe(ValidationRules<Order> rules, Order x) {
                    rules.As<IAudited>(x);
                }
            }
            """,
            "App",
            OutputKind.DynamicallyLinkedLibrary,
            new[] { shared }
        );

        Assert.Empty(result.CompilationErrors);

        var region = result.Sources["App.OrderRules_Rules.g.cs"];

        Assert.Contains("var facet0Context = ctx.WithResolvedFieldNames(false);", region);
        Assert.Contains("facet0[vi0].Validate(ref facet0Context, x)", region);
    }

    [Fact]
    public void AValidatableObject_SpellsMemberNamesWithTheResolvedFieldNames()
    {
        var result = Run(
            """
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;
            using System.Text.Json.Serialization;

            namespace Sample;

            [ValidationModules.Constraints.GenerateValidator]
            public sealed class Registrant : IValidatableObject {
                [JsonPropertyName("given_name")]
                public string? GivenName { get; init; }

                public string? Nickname { get; init; }

                public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
                    yield return new ValidationResult("bad", [nameof(GivenName)]);
                }
            }
            """
        );

        var emitted = Validator(result, "Registrant");

        Assert.Contains(
            "private sealed class MemberFieldNames : global::ValidationModules.Naming.FieldNamer",
            emitted
        );
        Assert.Contains("case \"GivenName\":", emitted);
        Assert.Contains("return \"given_name\";", emitted);
        Assert.DoesNotContain("case \"Nickname\":", emitted);
        Assert.Contains(
            "return global::ValidationModules.Naming.CamelCaseFieldNamer.Instance.ToFieldName(clrPropertyName);",
            emitted
        );
        Assert.Contains("ValidateObject(ref ctx, value, _memberFieldNames)", emitted);
    }

    [Fact]
    public void ACustomValidationMethod_SpellsMemberNamesWithTheResolvedFieldNames()
    {
        var result = Run(
            """
            using System.ComponentModel.DataAnnotations;
            using System.Text.Json.Serialization;

            namespace Sample;

            public sealed class Applicant {
                [CustomValidation(typeof(Applicant), nameof(Check)), JsonPropertyName("family_name")]
                public string? FamilyName { get; init; }

                public static ValidationResult? Check(string? value, ValidationContext context) =>
                    new("bad", [context.MemberName!]);
            }
            """
        );

        var emitted = Validator(result, "Applicant");

        Assert.Contains("case \"FamilyName\":", emitted);
        Assert.Contains("\"family_name\", _memberFieldNames)", emitted);
    }

    [Fact]
    public void WithoutAJsonName_TheBridgeKeepsThePolicysNamer()
    {
        var result = Run(
            """
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;

            namespace Sample;

            [ValidationModules.Constraints.GenerateValidator]
            public sealed class Registrant : IValidatableObject {
                public string? GivenName { get; init; }

                public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
                    yield break;
                }
            }
            """
        );

        var emitted = Validator(result, "Registrant");

        Assert.DoesNotContain("MemberFieldNames", emitted);
        Assert.Contains(
            "ValidateObject(ref ctx, value, global::ValidationModules.Naming.CamelCaseFieldNamer.Instance)",
            emitted
        );
    }

    [Fact]
    public void AnEnsureMessage_NamesMembersByTheirFieldNames_AndTheCodeKeepsThePolicysSpelling()
    {
        var result = Run(
            """
            using System;
            using System.Text.Json.Serialization;
            using ValidationModules;

            namespace Sample;

            public sealed class Stay {
                [JsonPropertyName("begins")]
                public DateOnly StartDate { get; init; }

                [JsonPropertyName("end_date")]
                public DateOnly EndDate { get; init; }
            }

            public sealed class StayRules : IValidationRulesFor<Stay> {
                public static void Describe(ValidationRules<Stay> rules, Stay x) =>
                    rules.Ensure(x.StartDate < x.EndDate);
            }
            """
        );

        var region = result.Sources["Sample.StayRules_Rules.g.cs"];

        Assert.Contains(
            "ctx.Report(\"begins\", \"start_date_less_than_end_date\", \"begins < end_date.\")",
            region
        );

        // VM3103 quotes what the code is derived from, which is the condition in the policy's
        // spelling rather than the message.
        var derived = Assert.Single(result.Diagnostics, d => d.Id == "VM3103").GetMessage();

        Assert.Contains("'startDate < endDate.'", derived);
    }

    [Fact]
    public void AnEnsureMessage_WalksAModelPath_AndLeavesFrameworkMembersAsWritten()
    {
        var result = Run(
            """
            using System.Text.Json.Serialization;
            using ValidationModules;

            namespace Sample;

            public sealed class Address {
                [JsonPropertyName("zip")]
                public string? PostalCode { get; init; }
            }

            public sealed class Customer {
                [JsonPropertyName("home_address")]
                public Address? Home { get; init; }

                public string? Name { get; init; }
            }

            public sealed class CustomerRules : IValidationRulesFor<Customer> {
                public static void Describe(ValidationRules<Customer> rules, Customer x) =>
                    rules.Ensure(x.Home?.PostalCode != null || x.Name!.Length > 3);
            }
            """
        );

        Assert.Contains(
            "\"home_address?.zip != null || name!.Length > 3.\"",
            result.Sources["Sample.CustomerRules_Rules.g.cs"]
        );
    }
}
