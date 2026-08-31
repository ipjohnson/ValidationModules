#!/usr/bin/env bash
#
# Packs the shipping projects and consumes them from a throwaway project, the way a stranger would.
#
# A ProjectReference-based test suite cannot see packaging faults: it resolves types through the
# compilation rather than through lib/ and analyzers/, so a validator emitted into the wrong
# namespace, an analyzer that never loads, or a missing build/*.targets all look fine. Two real bugs
# were found by doing this by hand, which is why it is a script.
#
# Usage: ./scripts/verify-packages.sh [version]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-0.1.0-local}"
FEED="$(mktemp -d)"
WORK="$(mktemp -d)"
trap 'rm -rf "${FEED}" "${WORK}"' EXIT

echo "Packing ${VERSION}"
for project in Runtime AspNetCore Options SourceGenerator SourceGenerator.Impl Messages; do
    dotnet pack "${REPO_ROOT}/src/ValidationModules.${project}/ValidationModules.${project}.csproj" \
        --configuration Release --output "${FEED}" --nologo \
        "/p:PackageVersion=${VERSION}" > /dev/null
done
ls -1 "${FEED}"

echo "Checking package layout"

# Listed once into a variable rather than piped into grep -q: grep exits at the first match, which
# SIGPIPEs unzip, and pipefail then reports the whole pipeline as failed on success.
GENERATOR_FILES="$(unzip -l "${FEED}/ValidationModules.SourceGenerator.${VERSION}.nupkg")"
IMPL_FILES="$(unzip -l "${FEED}/ValidationModules.SourceGenerator.Impl.${VERSION}.nupkg")"

expect() {
    case "$1" in
        *"$2"*) ;;
        *) echo "FAILED: $3"; exit 1 ;;
    esac
}

reject() {
    case "$1" in
        *"$2"*) echo "FAILED: $3"; exit 1 ;;
    esac
}

expect "${GENERATOR_FILES}" "analyzers/dotnet/cs/ValidationModules.SourceGenerator.dll" \
    "analyzer assembly is not under analyzers/dotnet/cs, so Roslyn will not load it"
expect "${GENERATOR_FILES}" "build/ValidationModules.SourceGenerator.targets" \
    "build targets missing, so MSBuild properties never reach the generator"
expect "${IMPL_FILES}" "src/ValidationModules.SourceGenerator.Impl/" \
    "Impl ships no sources"

# The [Generator] entry point must not be in Impl, or a framework author compiling it in registers
# a second generator alongside ours and every validator is emitted twice.
reject "${IMPL_FILES}" "ValidationSourceGenerator.cs" \
    "the [Generator] entry point is packed into Impl"

# Impl ships sources rather than an assembly, so its contract is the file set, not a public API
# surface. Nothing else notices a file the packaging glob stops matching: the solution still builds,
# because the projects here reference each other directly, and only a framework author compiling
# the package in discovers the gap - as a compile error that reads like their own mistake.
MISSING_SOURCES=""
while IFS= read -r source; do
    case "${IMPL_FILES}" in
        *"src/ValidationModules.SourceGenerator.Impl/${source}"*) ;;
        *) MISSING_SOURCES="${MISSING_SOURCES}  ${source}"$'\n' ;;
    esac
done < <(cd "${REPO_ROOT}/src/ValidationModules.SourceGenerator.Impl" && \
    find . -name '*.cs' -not -path './obj/*' -not -path './bin/*' | sed 's|^\./||' | sort)

if [ -n "${MISSING_SOURCES}" ]; then
    echo "FAILED: Impl compiles these sources but does not pack them:"
    printf '%s' "${MISSING_SOURCES}"
    exit 1
fi

# RuleText is compiled in from the runtime project and packed by a hand-written entry rather than by
# the glob above, so it is the one source the completeness check cannot see and the one most likely
# to be missed when either project moves a file.
expect "${IMPL_FILES}" "src/ValidationModules.SourceGenerator.Impl/FrontEnds/RuleText.cs" \
    "RuleText.cs is compiled into Impl but not packed, so a framework author gets CS0246 on it"

# The ASP.NET Core integration is an ordinary library and must ship as one, on both TFMs. Checked
# rather than assumed because it is the only package carrying a FrameworkReference, and getting
# that wrong yields a package that restores cleanly and then cannot resolve a type.
ASPNETCORE_FILES="$(unzip -l "${FEED}/ValidationModules.AspNetCore.${VERSION}.nupkg")"

expect "${ASPNETCORE_FILES}" "lib/net8.0/ValidationModules.AspNetCore.dll" \
    "the ASP.NET Core assembly is missing from lib/net8.0"
expect "${ASPNETCORE_FILES}" "lib/net10.0/ValidationModules.AspNetCore.dll" \
    "the ASP.NET Core assembly is missing from lib/net10.0"

# It must not carry an analyzer: a consumer referencing this and the generator would otherwise run
# two of them and get every validator emitted twice.
reject "${ASPNETCORE_FILES}" "analyzers/dotnet/cs" \
    "the ASP.NET Core package ships an analyzer"

# The Options package is an ordinary two-TFM library like the ASP.NET Core one, with the same
# no-analyzer rule.
OPTIONS_FILES="$(unzip -l "${FEED}/ValidationModules.Options.${VERSION}.nupkg")"

expect "${OPTIONS_FILES}" "lib/net8.0/ValidationModules.Options.dll" \
    "the Options assembly is missing from lib/net8.0"
expect "${OPTIONS_FILES}" "lib/net10.0/ValidationModules.Options.dll" \
    "the Options assembly is missing from lib/net10.0"
reject "${OPTIONS_FILES}" "analyzers/dotnet/cs" \
    "the Options package ships an analyzer"

# The Messages package is data plus props and nothing else: the JSON compiles at the consumer's
# build, so a lib/ here would mean the soft-publish shape regressed into an assembly.
MESSAGES_FILES="$(unzip -l "${FEED}/ValidationModules.Messages.${VERSION}.nupkg")"

expect "${MESSAGES_FILES}" "messages/fr.validation-messages.json" \
    "the French pack is missing from messages/"
expect "${MESSAGES_FILES}" "messages/ja.validation-messages.json" \
    "the Japanese pack is missing from messages/"
expect "${MESSAGES_FILES}" "build/ValidationModules.Messages.props" \
    "build props missing, so the JSON never reaches a consumer's AdditionalFiles"
reject "${MESSAGES_FILES}" "lib/" \
    "the Messages package ships an assembly; it must ship data"

echo "Consuming from a clean project"
cat > "${WORK}/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear/>
    <add key="local" value="${FEED}"/>
    <add key="nuget" value="https://api.nuget.org/v3/index.json"/>
  </packageSources>
</configuration>
EOF

cat > "${WORK}/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>

    <!--
      Restore into a throwaway folder rather than ~/.nuget/packages. NuGet caches by id and version,
      so a second run at the same version serves the first run's package and never looks at the feed
      this script just packed - which made the check pass against a generator two days old. CI never
      saw it, because a fresh runner has an empty cache; that is what made it worth fixing rather
      than working around with a unique version.
    -->
    <RestorePackagesPath>${WORK}/packages</RestorePackagesPath>
    <DisableImplicitNuGetFallbackFolder>true</DisableImplicitNuGetFallbackFolder>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ValidationModules.Runtime" Version="${VERSION}"/>
    <PackageReference Include="ValidationModules.SourceGenerator" Version="${VERSION}" PrivateAssets="all"/>
    <PackageReference Include="ValidationModules.Messages" Version="${VERSION}"/>
    <PackageReference Include="ValidationModules.Options" Version="${VERSION}"/>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1"/>
  </ItemGroup>
</Project>
EOF

cat > "${WORK}/Program.cs" <<'PROGRAM'
using System.Text.RegularExpressions;
using ValidationModules;
using ValidationModules.Constraints;
using Sample;

var errors = new PetValidator().Validate(new Pet { Toys = new List<Toy> { new() } }).Errors;
var actual = string.Join("; ", errors.Select(e => $"{e.Field}:{e.Code}"));
var expected = "name:required; toys[0].name:required";

if (actual != expected) {
    Console.Error.WriteLine($"FAILED: expected '{expected}' but got '{actual}'");
    Environment.Exit(1);
}

// A global-namespace type must get its validator in the global namespace, not in one of ours.
if (new GlobalPetValidator().Validate(new GlobalPet()).Errors.Count != 1) {
    Console.Error.WriteLine("FAILED: global-namespace type did not validate");
    Environment.Exit(1);
}

// The Messages package, end to end from the feed: its props put the JSON into this build, the
// generator compiled and registered the packs, and one registration call localises per culture.
System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("fr");

// Static-form calls, because this file's usings sit above and extension syntax would need the DI
// namespace imported - the same reason the generated registration calls them this way.
var collection = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

Microsoft.Extensions.DependencyInjection.ConsumerValidationExtensions.AddConsumerValidators(collection);

using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
    .BuildServiceProvider(collection);

var formatter = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
    .GetRequiredService<ValidationMessageFormatter>(provider);
var required = new PetValidator().Validate(new Pet()).Errors.First(e => e.Code == "required");

if (required.ToMessage(formatter) != "name est obligatoire.") {
    Console.Error.WriteLine($"FAILED: French pack did not render; got '{required.ToMessage(formatter)}'");
    Environment.Exit(1);
}

// The Options package from the feed: the bridge resolves the generated validator and renders a
// failure as `field [code] message`.
var optionsCollection = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

Microsoft.Extensions.DependencyInjection.ConsumerValidationExtensions.AddConsumerValidators(optionsCollection);
Microsoft.Extensions.DependencyInjection.ValidationModulesOptionsExtensions.AddValidatedOptions<Pet>(optionsCollection);

using var optionsProvider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
    .BuildServiceProvider(optionsCollection);

var bridge = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
    .GetRequiredService<Microsoft.Extensions.Options.IValidateOptions<Pet>>(optionsProvider);
var verdict = bridge.Validate(Microsoft.Extensions.Options.Options.DefaultName, new Pet());

if (!verdict.Failed || verdict.FailureMessage?.Contains("name [required]") != true) {
    Console.Error.WriteLine($"FAILED: options bridge did not report; got '{verdict.FailureMessage}'");
    Environment.Exit(1);
}

Console.WriteLine("Package verification passed");

public record GlobalPet {
    [Required] public string? Name { get; init; }
}

namespace Sample {
    public static partial class Patterns {
        [GeneratedRegex("^[A-Z]{3}$")] public static partial Regex Sku();
    }

    public sealed record Pet {
        [Required][StringLength(min: 1, max: 10)] public string? Name { get; init; }
        [Pattern(typeof(Patterns), nameof(Patterns.Sku))] public string? Sku { get; init; }
        [ItemCount(min: 1, max: 3)][ValidateNested] public IReadOnlyList<Toy> Toys { get; init; } = new List<Toy>();
    }

    public sealed record Toy {
        [Required] public string? Name { get; init; }
    }
}
PROGRAM

dotnet run --project "${WORK}/Consumer.csproj" -c Release --nologo
