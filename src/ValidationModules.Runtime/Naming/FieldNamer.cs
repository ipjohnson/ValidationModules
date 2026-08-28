using System.Text;

namespace ValidationModules.Naming;

/// <summary>
/// The built-in naming policies, and the shared path-joining they all use.
/// </summary>
/// <remarks>
/// Paths are dotted with bracketed indices - <c>home.postalCode</c>, <c>toys[3].name</c> - rather
/// than JSON Pointer. FluentValidation already produces that shape, which reduces the adapter's job
/// to a case conversion.
/// </remarks>
public abstract class FieldNamer : IValidationFieldNamer {

    /// <inheritdoc />
    public abstract string ToFieldName(string clrPropertyName);

    /// <inheritdoc />
    public string Combine(string parentPath, string fieldName) =>
        string.IsNullOrEmpty(parentPath) ? fieldName : string.Concat(parentPath, ".", fieldName);

    /// <inheritdoc />
    public string CombineIndex(string parentPath, string fieldName, int index) =>
        string.Concat(Combine(parentPath, fieldName), "[", index.ToString(), "]");
}

/// <summary>
/// <c>PostalCode</c> becomes <c>postalCode</c>. The default, because it matches what a JSON API
/// puts on the wire.
/// </summary>
/// <remarks>
/// Deliberately <c>JsonNamingPolicy.CamelCase</c>'s algorithm rather than a lowercased first
/// character. The two agree on <c>PostalCode</c> and part company on a leading acronym: a property
/// named <c>SKU</c> serializes as <c>sku</c> and used to be reported as <c>sKU</c>, so the error
/// named a field that was not in the payload the caller sent and no client could match it. The
/// whole leading uppercase run goes down, except a final capital that begins the next word -
/// <c>IPAddress</c> is <c>ipAddress</c>, not <c>ipaddress</c>. Reimplemented rather than called
/// because the runtime does not reference System.Text.Json, and pinned to the serializer by a test
/// that asserts against it directly.
/// </remarks>
public sealed class CamelCaseFieldNamer : FieldNamer {

    /// <summary>The shared instance. Stateless.</summary>
    public static CamelCaseFieldNamer Instance { get; } = new();

    /// <inheritdoc />
    public override string ToFieldName(string clrPropertyName) {
        ArgumentNullException.ThrowIfNull(clrPropertyName);

        if (clrPropertyName.Length == 0 || !char.IsUpper(clrPropertyName[0])) {
            return clrPropertyName;
        }

        return string.Create(clrPropertyName.Length, clrPropertyName, static (destination, source) => {
            source.AsSpan().CopyTo(destination);

            for (var i = 0; i < destination.Length; i++) {
                // A second character that is already lowercase means the first was a word on its
                // own, and it has been dealt with.
                if (i == 1 && !char.IsUpper(destination[i])) {
                    break;
                }

                var hasNext = i + 1 < destination.Length;

                // The run has ended: this capital starts the next word and keeps its case.
                if (i > 0 && hasNext && !char.IsUpper(destination[i + 1])) {
                    if (destination[i + 1] == ' ') {
                        destination[i] = char.ToLowerInvariant(destination[i]);
                    }

                    break;
                }

                destination[i] = char.ToLowerInvariant(destination[i]);
            }
        });
    }
}

/// <summary>
/// <c>PostalCode</c> stays <c>PostalCode</c>. For APIs whose wire format is the CLR name.
/// </summary>
public sealed class PascalCaseFieldNamer : FieldNamer {

    /// <summary>The shared instance. Stateless.</summary>
    public static PascalCaseFieldNamer Instance { get; } = new();

    /// <inheritdoc />
    public override string ToFieldName(string clrPropertyName) {
        ArgumentNullException.ThrowIfNull(clrPropertyName);

        return clrPropertyName;
    }
}

/// <summary>
/// <c>PostalCode</c> becomes <c>postal_code</c>.
/// </summary>
public sealed class SnakeCaseFieldNamer : FieldNamer {

    /// <summary>The shared instance. Stateless.</summary>
    public static SnakeCaseFieldNamer Instance { get; } = new();

    /// <inheritdoc />
    public override string ToFieldName(string clrPropertyName) {
        ArgumentNullException.ThrowIfNull(clrPropertyName);

        if (clrPropertyName.Length == 0) {
            return clrPropertyName;
        }

        var builder = new StringBuilder(clrPropertyName.Length + 4);

        for (var i = 0; i < clrPropertyName.Length; i++) {
            var character = clrPropertyName[i];

            if (char.IsUpper(character)) {
                // A capital starts a new word when it follows a non-capital, or when it ends a run
                // of capitals by being followed by a lowercase one. The second clause is what makes
                // HTTPStatus into http_status rather than httpstatus or h_t_t_p_status.
                var startsWord =
                    i > 0 &&
                    (!char.IsUpper(clrPropertyName[i - 1]) ||
                     (i + 1 < clrPropertyName.Length && char.IsLower(clrPropertyName[i + 1])));

                if (startsWord) {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            } else {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
