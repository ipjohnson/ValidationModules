using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ValidationModules.SourceGenerator.Tests;

/// <summary>
/// Pins the one assumption inherited-constraint collection rests on: that Roslyn surfaces a
/// constraint attribute declared on a base type's property when that base type arrives through an
/// assembly reference rather than through source.
/// </summary>
/// <remarks>
/// <para>
/// It is expected to hold - attributes are serialised into metadata and <c>GetAttributes()</c>
/// reads them back - but "expected" is why this exists. A shared <c>BaseRequest</c> lives in a
/// package far more often than it lives in the consuming project, so if this did not hold, walking
/// the base chain would work in every test and silently do nothing for the case it was built for.
/// </para>
/// <para>
/// Asserted at the symbol level rather than through the generator, so a failure says which of the
/// two things broke: whether Roslyn stopped surfacing the attribute, or whether the walk stopped
/// asking for it.
/// </para>
/// </remarks>
public class CrossAssemblyMetadataSpike {

    private const string BaseAssembly = """
        using ValidationModules.Constraints;

        namespace Shared;

        public record BaseRequest {
            [Required]
            [StringLength(1, 64)]
            public string? CorrelationId { get; init; }

            [Required]
            public string? TenantId { get; init; }
        }

        public interface IAudited {
            [Required]
            string? ModifiedBy { get; }
        }
        """;

    private const string Consumer = """
        using Shared;
        using ValidationModules.Constraints;

        namespace Consumer;

        public record CreateOrder : BaseRequest {
            [Required]
            public string? Sku { get; init; }
        }

        public record Document : IAudited {
            [Required]
            public string? Title { get; init; }

            public string? ModifiedBy { get; init; }
        }
        """;

    private static INamedTypeSymbol Lookup(string metadataName) {
        var reference = GeneratorHarness.CompileToReference(BaseAssembly, "Shared.Fixture");

        var compilation = CSharpCompilation.Create(
            "Consumer.Fixture",
            new[] { CSharpSyntaxTree.ParseText(Consumer) },
            GeneratorHarness.ReferencesIncluding(reference),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        return compilation.GetTypeByMetadataName(metadataName)
            ?? throw new InvalidOperationException($"'{metadataName}' did not resolve.");
    }

    [Fact]
    public void ConstraintAttributesOnAMetadataBaseType_AreVisibleToTheDerivedType() {
        var derived = Lookup("Consumer.CreateOrder");
        var baseType = derived.BaseType;

        Assert.NotNull(baseType);
        Assert.Equal("Shared.Fixture", baseType!.ContainingAssembly.Name);

        var correlationId = Assert.IsAssignableFrom<IPropertySymbol>(
            Assert.Single(baseType.GetMembers("CorrelationId")));

        var names = correlationId.GetAttributes()
            .Select(attribute => attribute.AttributeClass?.Name)
            .ToList();

        Assert.Contains("RequiredAttribute", names);
        Assert.Contains("StringLengthAttribute", names);
    }

    /// <summary>
    /// A constructor argument has to survive the round trip too, not just the attribute's presence -
    /// a <c>[StringLength(1, 64)]</c> that reads back with no bounds is worse than one that does not
    /// read back at all.
    /// </summary>
    [Fact]
    public void ConstructorArgumentsSurviveTheMetadataRoundTrip() {
        var baseType = Lookup("Consumer.CreateOrder").BaseType!;

        var correlationId = (IPropertySymbol)baseType.GetMembers("CorrelationId").Single();

        var stringLength = correlationId.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.Name == "StringLengthAttribute");

        Assert.Equal([1, 64], stringLength.ConstructorArguments.Select(argument => argument.Value));
    }

    /// <summary>
    /// The interface half of the same question. The constraint is declared on the interface in
    /// metadata; the implementing property in source carries none of its own.
    /// </summary>
    [Fact]
    public void ConstraintAttributesOnAMetadataInterface_ResolveToTheImplementingMember() {
        var document = Lookup("Consumer.Document");

        var contract = Assert.Single(document.AllInterfaces, i => i.Name == "IAudited");
        var declared = (IPropertySymbol)contract.GetMembers("ModifiedBy").Single();

        Assert.Contains(
            declared.GetAttributes(),
            attribute => attribute.AttributeClass?.Name == "RequiredAttribute");

        // The half the walk actually needs: getting from the interface declaration to the property
        // the generated validator will read.
        var implementation = document.FindImplementationForInterfaceMember(declared);

        Assert.NotNull(implementation);
        Assert.Equal("ModifiedBy", implementation!.Name);
        Assert.Empty(((IPropertySymbol)implementation).GetAttributes());
    }
}
