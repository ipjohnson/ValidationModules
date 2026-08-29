using Microsoft.CodeAnalysis;

namespace ValidationModules.SourceGenerator.Impl;

/// <summary>
/// What the emitters in this package require of the runtime the consumer references, and the probe
/// that checks it. Plan §7.5.
/// </summary>
/// <remarks>
/// <para>
/// Two hosts drive these emitters and both need the same answer. A Roslyn generator has a
/// <see cref="Compilation"/> and calls <see cref="Probe"/>. An MSBuild task has no compilation and
/// compares <see cref="RequiredRuntimeContract"/> against <c>$(ValidationModulesRuntimeContract)</c>,
/// which <c>ValidationModules.Runtime</c>'s props sets - see that package's build folder. Neither
/// route loads an assembly.
/// </para>
/// </remarks>
public static class EmitterContract {

    /// <summary>
    /// The lowest <c>ValidationModules.RuntimeContract.Version</c> the emitted code works against.
    /// </summary>
    public const int RequiredRuntimeContract = 9;

    /// <summary>The metadata name of the runtime's contract marker.</summary>
    public const string RuntimeContractType = "ValidationModules.RuntimeContract";

    private const string VersionField = "Version";

    /// <summary>
    /// Checks the referenced runtime against <see cref="RequiredRuntimeContract"/>, returning the
    /// diagnostic to report, or null when the runtime is new enough.
    /// </summary>
    /// <remarks>
    /// A missing marker type is treated as contract 0 rather than as "no runtime referenced". The
    /// two cases are indistinguishable here and produce the same failure downstream - generated
    /// code that does not compile - so they get one diagnostic that names the version needed.
    /// </remarks>
    public static Diagnostic? Probe(Compilation compilation) {
        var found = ResolveRuntimeContract(compilation);

        return found >= RequiredRuntimeContract
            ? null
            : Diagnostic.Create(
                ValidationDiagnostics.RuntimeContractTooOld,
                Location.None,
                RequiredRuntimeContract,
                found);
    }

    /// <summary>
    /// Reads <c>ValidationModules.RuntimeContract.Version</c> out of the compilation, or 0 when the
    /// type, the field, or its constant value is absent.
    /// </summary>
    public static int ResolveRuntimeContract(Compilation compilation) {
        if (compilation.GetTypeByMetadataName(RuntimeContractType) is not { } marker) {
            return 0;
        }

        foreach (var member in marker.GetMembers(VersionField)) {
            if (member is IFieldSymbol { HasConstantValue: true, ConstantValue: int version }) {
                return version;
            }
        }

        return 0;
    }
}
