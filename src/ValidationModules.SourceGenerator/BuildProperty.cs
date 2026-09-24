namespace ValidationModules.SourceGenerator;

/// <summary>
/// A <c>ValidationModules_*</c> MSBuild property that takes one of a fixed set of values.
/// </summary>
/// <remarks>
/// <para>
/// Every property is matched without regard to case, and with surrounding whitespace ignored. One
/// rule for all of them means nobody has to know which property is strict.
/// </para>
/// <para>
/// A value that matches none of the spellings falls back to the default and is reported as
/// VM5004, naming the property, the value, and the values it accepts. Without the report, a typo
/// such as <c>snake_case</c> builds cleanly and does something other than what was asked. An
/// absent or empty value is the default and is not reported: MSBuild hands an unset
/// <c>CompilerVisibleProperty</c> to the generator as an empty string.
/// </para>
/// </remarks>
internal sealed class BuildProperty
{
    public static readonly BuildProperty Registration = new(
        "ValidationModules_Registration",
        "Auto",
        "DependencyModules",
        "ServiceCollection",
        "None"
    );

    public static readonly BuildProperty FieldNaming = new(
        "ValidationModules_FieldNaming",
        "CamelCase",
        "PascalCase",
        "AsDeclared",
        "SnakeCase"
    );

    public static readonly BuildProperty PatternPolicy = new(
        "ValidationModules_PatternPolicy",
        "Auto",
        "Error",
        "Warn",
        "Allow"
    );

    public static readonly BuildProperty DataAnnotations = new(
        "ValidationModules_DataAnnotations",
        "Compile",
        "Ignore"
    );

    public static readonly BuildProperty FailFast = new(
        "ValidationModules_FailFast",
        "Enabled",
        "Disabled",
        "true",
        "false"
    );

    public static readonly BuildProperty CaptureValues = new(
        "ValidationModules_CaptureValues",
        "Enabled",
        "Disabled",
        "true",
        "false"
    );

    private readonly string[] _values;

    /// <param name="name">The property as it is written in a project file.</param>
    /// <param name="defaultValue">
    /// The value an absent, empty or unrecognised setting stands for. Listed first among the
    /// accepted values, so writing the default out is never reported.
    /// </param>
    /// <param name="others">The other accepted values, as the reference documents them.</param>
    private BuildProperty(string name, string defaultValue, params string[] others)
    {
        Name = name;
        Default = defaultValue;
        _values = new[] { defaultValue }.Concat(others).ToArray();
    }

    /// <summary>The property as it is written in a project file.</summary>
    public string Name { get; }

    /// <summary>
    /// The key <c>AnalyzerConfigOptions.GlobalOptions</c> exposes the property under.
    /// </summary>
    public string Key => "build_property." + Name;

    /// <summary>The value an absent, empty or unrecognised setting stands for.</summary>
    public string Default { get; }

    /// <summary>
    /// The accepted values as VM5004 lists them, such as <c>Compile or Ignore</c>.
    /// </summary>
    public string AcceptedValues =>
        string.Join(", ", _values, 0, _values.Length - 1) + " or " + _values[_values.Length - 1];

    /// <summary>
    /// The accepted spelling <paramref name="setting"/> matches, or null when it is absent, empty
    /// or matches none of them.
    /// </summary>
    public string? Canonical(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return null;
        }

        var trimmed = setting!.Trim();

        foreach (var value in _values)
        {
            if (string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="setting"/> is present and matches no accepted value.
    /// </summary>
    public bool IsUnrecognised(string? setting) =>
        !string.IsNullOrWhiteSpace(setting) && Canonical(setting) is null;
}
