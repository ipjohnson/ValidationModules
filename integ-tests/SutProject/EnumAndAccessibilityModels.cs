using ValidationModules.Constraints;

namespace SutProject;

/// <summary>
/// The two shapes that used to emit C# which did not compile, kept as models rather than as golden
/// files because the fault was never in the text - it was that the text did not build.
/// </summary>
public enum Tier {
    Free,
    Pro,
    Enterprise,
}

/// <summary>
/// <c>[AllowedValues]</c> over enum members. The reader used to render these from
/// <c>TypedConstant.Value</c>, which holds the underlying <c>int</c>, so the emitted comparison was
/// <c>value.Plan != 1</c> - CS0019 against an enum, with no diagnostic pointing at the cause.
/// </summary>
public sealed record Account {
    [AllowedValues(Tier.Pro, Tier.Enterprise)]
    public Tier Plan { get; init; }

    /// <summary>A value with no member of its own, which has to survive as a cast.</summary>
    [AllowedValues(Tier.Pro, (Tier)7)]
    public Tier Unnamed { get; init; }
}

/// <summary>
/// An internal model. The emitter wrote <c>public sealed partial class</c> unconditionally, so the
/// generated validator exposed a less accessible type in <c>Validate</c>'s signature - CS0051, again
/// inside generated code.
/// </summary>
internal sealed record InternalReading {
    [Required]
    public string? Label { get; init; }

    [Range(0, 10)]
    public int Level { get; init; }
}

/// <summary>Public, but nested in an internal type, so internal in effect.</summary>
internal static class Enclosing {
    public sealed record Nested {
        [Required]
        public string? Name { get; init; }
    }
}
