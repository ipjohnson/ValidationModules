using ValidationModules.Constraints;

namespace SutProject;

/// <summary>
/// The seven attributes that completed DataAnnotations parity, native spelling. This file's only
/// using is <c>ValidationModules.Constraints</c> - which is the point: the rc1013 trial's CS0104
/// storms all started with a model file importing the second namespace to reach one of these.
/// </summary>
public sealed record Registration {
    [EmailAddress]
    public string? Email { get; init; }

    [Phone]
    public string? Contact { get; init; }

    [Url]
    public string? Homepage { get; init; }

    [Url]
    public Uri? Docs { get; init; }

    [CreditCard]
    public string? CardNumber { get; init; }

    [Base64String]
    public string? Signature { get; init; }

    [FileExtensions(Extensions = "pdf,docx")]
    public string? Attachment { get; init; }

    [DeniedValues("admin", "root", "system")]
    public string? Username { get; init; }
}
