using ValidationModules.Constraints;

namespace OptionsDemo;

/// <summary>
/// A worker's configuration: the same constraint vocabulary a request body uses, bound from
/// <c>appsettings.json</c> and checked before the host serves anything.
/// </summary>
public sealed class HubOptions {

    [Required]
    [StringLength(min: 3, max: 40)]
    public string? HubName { get; set; }

    [Range(1, 500)]
    public int MaxBatchSize { get; set; }
}
