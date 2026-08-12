using System.ComponentModel.DataAnnotations;

namespace SutProject.DataAnnotations;

/// <summary>
/// Declared entirely with DataAnnotations. Nothing from ValidationModules.Constraints is imported
/// here beyond the opt-in marker, which is the on-ramp the front-end exists for: a model already
/// annotated for EF Core or Swashbuckle gets a validator with no edits.
/// </summary>
[ValidationModules.Constraints.GenerateValidator]
public sealed class Customer {
    [Required]
    [StringLength(20, MinimumLength = 2)]
    public string? Name { get; set; }

    /// <summary>Anchored, unlike the native [Pattern]: "xABCx" must fail.</summary>
    [RegularExpression("[A-Z]{3}")]
    public string? Code { get; set; }

    [Range(1, 120)]
    public int Age { get; set; }

    [MaxLength(3)]
    public List<string> Tags { get; set; } = new();

    [AllowedValues("gold", "silver")]
    public string? Tier { get; set; }
}
