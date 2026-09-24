using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// The packs <c>ValidationModules.Messages</c> ships carry the library's vocabulary and nothing
/// else: shape keys and the codes on <see cref="ValidationCodes"/>.
/// </summary>
/// <remarks>
/// Every consumer that installs the package compiles every key in these files. A code an
/// application invented, such as the demo's <c>date_order</c>, belongs in that application's own
/// pack. Left in a shipped one, it translates the code for every consumer who happens to choose the
/// same name, in five languages they did not write.
/// </remarks>
public class ShippedLanguagePackTests
{
    public static TheoryData<string> Packs()
    {
        var packs = new TheoryData<string>();

        foreach (
            var file in Directory
                .EnumerateFiles(MessagesRoot, "*.validation-messages.json")
                .OrderBy(file => file, StringComparer.Ordinal)
        )
        {
            packs.Add(Path.GetFileName(file));
        }

        return packs;
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void EveryKey_IsAShapeKeyOrALibraryCode(string pack)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(MessagesRoot, pack)));

        var library = ValidationMessageTemplates.KnownKeys.Concat(LibraryCodes()).ToHashSet();

        var foreign = document
            .RootElement.GetProperty("templates")
            .EnumerateObject()
            .Select(entry => entry.Name)
            .Where(key => !library.Contains(key))
            .ToList();

        Assert.Empty(foreign);
    }

    private static IEnumerable<string> LibraryCodes() =>
        typeof(ValidationCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static string MessagesRoot { get; } =
        Path.Combine(ResolveRepositoryRoot(), "src", "ValidationModules.Messages", "messages");

    private static string ResolveRepositoryRoot()
    {
        var configured = typeof(ShippedLanguagePackTests)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryRoot")
            ?.Value;

        if (configured is null || !Directory.Exists(Path.Combine(configured, "src")))
        {
            throw new InvalidOperationException(
                "RepositoryRoot assembly metadata is missing or does not contain src/."
            );
        }

        return configured;
    }
}
