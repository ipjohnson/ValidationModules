using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace ValidationModules.AspNetCore;

/// <summary>
/// The source-generated metadata for serialising a validation problem.
/// </summary>
/// <remarks>
/// <para>
/// Writing <see cref="ValidationProblemDetails"/> through the reflection-based serialiser is
/// IL2026 and IL3050, and this project treats both as errors. Declaring the shapes here means the
/// converters are generated at build time, which is the same trade the rest of the library makes.
/// </para>
/// <para>
/// <b><see cref="Dictionary{TKey,TValue}"/> is listed for a reason that is easy to miss.</b>
/// <see cref="ProblemDetails.Extensions"/> is typed as <c>object?</c>, so the codes dictionary
/// reaches the serialiser boxed and its real type is only known at run time. A source-generated
/// context resolves that by looking the runtime type up among the ones it was told about - so if it
/// were not declared here, writing a problem with codes would fail at run time in a published app
/// and nowhere else.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class ValidationProblemJsonContext : JsonSerializerContext;
