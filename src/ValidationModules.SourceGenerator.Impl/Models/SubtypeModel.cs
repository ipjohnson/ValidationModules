namespace ValidationModules.SourceGenerator.Impl.Models;

/// <summary>
/// How a nested descent treats a value more derived than the property's declared type.
/// </summary>
/// <remarks>
/// Mirrors <c>ValidationModules.Constraints.Polymorphism</c> by value. The generator cannot
/// reference the runtime - it is loaded into the compiler, not into the application - so the two
/// are kept in step by ordinal, the same way every other constraint argument crosses this boundary.
/// </remarks>
public enum PolymorphismMode {
    DeclaredOnly = 0,
    CompileTime = 1,
    Runtime = 2,
}

/// <summary>
/// One subtype a polymorphic descent can dispatch to.
/// </summary>
/// <param name="QualifiedTypeName">The subtype, fully qualified, for the type pattern.</param>
/// <param name="ValidatorName">Its validator, fully qualified, for the branch body.</param>
/// <param name="Depth">
/// Inheritance distance from the declared type. The emitted switch must be sorted by this
/// descending: a type pattern matches derived types too, so <c>case Card</c> written before
/// <c>case Premium : Card</c> makes the second arm unreachable - <b>CS8120</b>, inside a generated
/// file, which is the class of error the no-emit-after-diagnostic work exists to prevent.
/// </param>
public sealed record SubtypeModel(
    string QualifiedTypeName,
    string ValidatorName,
    int Depth) : IEquatable<SubtypeModel>;
