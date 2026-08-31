using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM0108 — <c>.Validate&lt;T&gt;()</c> naming a type this compilation declares and generates no
/// validator for, reported where the call was written rather than when the endpoint is built.
/// </summary>
/// <remarks>
/// The analyzer matches the extension by name and namespace, so these tests declare the shape
/// themselves instead of referencing ASP.NET Core - only the genuine package declares
/// <c>Microsoft.AspNetCore.Builder.ValidationModulesEndpointExtensions</c>.
/// </remarks>
public class ValidateCallAnalyzerTests {

    private const string EndpointShape = """
        namespace Microsoft.AspNetCore.Builder {
            public sealed class RouteHandlerBuilder { }

            public static class ValidationModulesEndpointExtensions {
                public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder, int? statusCode = null)
                    => builder;
            }
        }
        """;

    private static ImmutableArray<Diagnostic> Analyze(string source) {
        var compilation = CSharpCompilation.Create(
            "AnalyzerTests",
            new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(EndpointShape) },
            GeneratorHarness.ReferencesIncluding(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        return compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ValidateCallAnalyzer()))
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    private const string Usings = """
        using System.Collections.Generic;
        using Microsoft.AspNetCore.Builder;
        using ValidationModules;
        using ValidationModules.Constraints;

        namespace Sample;
        """;

    [Fact]
    public void ARulelessDeclaredType_IsVM0108() {
        var diagnostics = Analyze(Usings + """

            public sealed record Coupon {
                public string? Code { get; init; }
            }

            public static class Wiring {
                public static void Map(RouteHandlerBuilder builder) => builder.Validate<Coupon>();
            }
            """);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "VM0108");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Coupon", diagnostic.GetMessage());
    }

    [Fact]
    public void AListOfARulelessDeclaredType_IsVM0108AtTheElement() {
        var diagnostics = Analyze(Usings + """

            public sealed record Coupon {
                public string? Code { get; init; }
            }

            public static class Wiring {
                public static void Map(RouteHandlerBuilder builder) => builder.Validate<List<Coupon>>();
            }
            """);

        Assert.Contains("Coupon", Assert.Single(diagnostics, d => d.Id == "VM0108").GetMessage());
    }

    [Fact]
    public void AConstrainedType_AndItsListAndArray_AreSilent() {
        var diagnostics = Analyze(Usings + """

            public sealed record Order {
                [Required] public string? Reference { get; init; }
            }

            public static class Wiring {
                public static void Map(RouteHandlerBuilder builder) {
                    builder.Validate<Order>();
                    builder.Validate<List<Order>>();
                    builder.Validate<Order[]>();
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VM0108");
    }

    [Fact]
    public void ARulesClassTarget_IsSilent() {
        // The compilation-wide half: the type carries nothing, and its rules live elsewhere.
        var diagnostics = Analyze(Usings + """

            public sealed record Coupon {
                public string? Code { get; init; }
            }

            public sealed class CouponRules : IValidationRulesFor<Coupon> {
                public static void Describe(ValidationRules<Coupon> rules, Coupon x) {
                    rules.Require(x.Code);
                }
            }

            public static class Wiring {
                public static void Map(RouteHandlerBuilder builder) => builder.Validate<Coupon>();
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VM0108");
    }

    [Fact]
    public void AHandWrittenValidatorTarget_IsSilent() {
        // A hand-registered IValidatorFor<T> satisfies the startup check, so the analyzer must
        // not be louder than the check it fronts.
        var diagnostics = Analyze(Usings + """

            public sealed record Coupon {
                public string? Code { get; init; }
            }

            public sealed class CouponValidator : IValidatorFor<Coupon> {
                public ValidationFlow Validate(ref ValidationContext context, Coupon value) =>
                    ValidationFlow.Continue;
            }

            public static class Wiring {
                public static void Map(RouteHandlerBuilder builder) => builder.Validate<Coupon>();
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VM0108");
    }

    [Fact]
    public void ATypeFromAnotherAssembly_IsSilent() {
        // The cross-assembly caution VM0007 set: a metadata type may carry a validator generated
        // over there, so the startup check owns it.
        var diagnostics = Analyze(Usings + """

            public static class Wiring {
                public static void Map(RouteHandlerBuilder builder) =>
                    builder.Validate<System.Text.StringBuilder>();
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VM0108");
    }

    [Fact]
    public void AGenerateValidatorMarkedType_IsSilent() {
        var diagnostics = Analyze(Usings + """

            [GenerateValidator]
            public sealed record Coupon {
                public string? Code { get; init; }
            }

            public static class Wiring {
                public static void Map(RouteHandlerBuilder builder) => builder.Validate<Coupon>();
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "VM0108");
    }
}
