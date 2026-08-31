using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ValidationModules.SourceGenerator.Impl;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// VM5001, version lockstep. Plan §7.5.
/// </summary>
/// <remarks>
/// The probe is exercised against hand-built compilations rather than through
/// <see cref="GeneratorHarness"/>, because the harness always references the real runtime and so can
/// only ever produce the passing case. Declaring a second <c>RuntimeContract</c> in harness source
/// would not help either - two definitions of one metadata name make
/// <c>GetTypeByMetadataName</c> return null, which is the missing case, not the old one.
/// </remarks>
public class RuntimeContractTests {

    [Fact]
    public void Probe_ReportsWhenRuntimeIsAbsent() {
        var diagnostic = EmitterContract.Probe(Compile(""));

        Assert.NotNull(diagnostic);
        Assert.Equal("VM5001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Probe_ReportsWhenContractIsOlderThanRequired() {
        var diagnostic = EmitterContract.Probe(Compile(MarkerWith(EmitterContract.RequiredRuntimeContract - 1)));

        Assert.NotNull(diagnostic);
        Assert.Equal("VM5001", diagnostic.Id);
    }

    [Fact]
    public void Probe_NamesBothVersionsInTheMessage() {
        var message = EmitterContract.Probe(Compile(MarkerWith(0)))!.GetMessage();

        Assert.Contains($"contract {EmitterContract.RequiredRuntimeContract} or later", message);
        Assert.Contains("is contract 0", message);
    }

    [Fact]
    public void Probe_PassesWhenContractMatchesExactly() {
        Assert.Null(EmitterContract.Probe(Compile(MarkerWith(EmitterContract.RequiredRuntimeContract))));
    }

    [Fact]
    public void Probe_PassesWhenContractIsNewerThanRequired() {
        Assert.Null(EmitterContract.Probe(Compile(MarkerWith(EmitterContract.RequiredRuntimeContract + 5))));
    }

    [Fact]
    public void ResolveRuntimeContract_TreatsAMarkerWithNoVersionFieldAsZero() {
        var source = "namespace ValidationModules { public static class RuntimeContract { } }";

        Assert.Equal(0, EmitterContract.ResolveRuntimeContract(Compile(source)));
    }

    [Fact]
    public void ResolveRuntimeContract_TreatsANonIntegerVersionAsZero() {
        var source = """
            namespace ValidationModules {
                public static class RuntimeContract { public const string Version = "1"; }
            }
            """;

        Assert.Equal(0, EmitterContract.ResolveRuntimeContract(Compile(source)));
    }

    [Fact]
    public void ResolveRuntimeContract_IgnoresANonConstantVersion() {
        var source = """
            namespace ValidationModules {
                public static class RuntimeContract { public static readonly int Version = 9; }
            }
            """;

        Assert.Equal(0, EmitterContract.ResolveRuntimeContract(Compile(source)));
    }

    /// <summary>
    /// The failure mode that would hurt most. A probe that misfires does not degrade one feature -
    /// it fails every build that references the package, so the ordinary path is pinned explicitly
    /// rather than left to be implied by other tests passing.
    /// </summary>
    [Fact]
    public void Generator_DoesNotReportVM5001AgainstTheRealRuntime() {
        var result = GeneratorHarness.Run("""
            using ValidationModules.Constraints;

            namespace Sample;

            [GenerateValidator]
            public class Person {
                [Required] public string? Name { get; set; }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Id == "VM5001");
        Assert.Empty(result.CompilationErrors);
    }

    /// <summary>
    /// The emitters in this repo must not require more than the runtime in this repo supplies. A
    /// bump to <see cref="EmitterContract.RequiredRuntimeContract"/> without the matching bump to
    /// <c>RuntimeContract.Version</c> would ship a package that rejects its own sibling.
    /// </summary>
    [Fact]
    public void RequiredContract_DoesNotExceedTheRuntimeInThisRepo() {
        Assert.True(EmitterContract.RequiredRuntimeContract <= RuntimeContract.Version,
            $"Emitter requires {EmitterContract.RequiredRuntimeContract}, runtime supplies {RuntimeContract.Version}.");
    }

    /// <summary>
    /// MSBuild hosts read the props rather than the constant, so the two are one value in two files
    /// and nothing but this test keeps them together.
    /// </summary>
    [Fact]
    public void PropsContractVersion_MatchesTheRuntimeConstant() {
        var path = Path.Combine(AppContext.BaseDirectory, "ValidationModules.Runtime.props");
        Assert.True(File.Exists(path), $"Expected the runtime props to be copied to output at {path}.");

        var match = Regex.Match(
            File.ReadAllText(path),
            @"<ValidationModulesRuntimeContract>\s*(\d+)\s*</ValidationModulesRuntimeContract>");

        Assert.True(match.Success, "ValidationModulesRuntimeContract is not declared in the runtime props.");
        Assert.Equal(RuntimeContract.Version, int.Parse(match.Groups[1].Value));
    }

    private static string MarkerWith(int version) => $$"""
        namespace ValidationModules {
            public static class RuntimeContract { public const int Version = {{version}}; }
        }
        """;

    /// <summary>
    /// A compilation carrying only corlib, so the runtime this test project references cannot
    /// satisfy the probe by accident.
    /// </summary>
    private static Compilation Compile(string source) =>
        CSharpCompilation.Create(
            "ContractProbeTests",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
