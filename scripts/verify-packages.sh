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
# Resolved with pwd -P, not left as mktemp returns them. On macOS $TMPDIR is under /var, which is a
# symlink to /private/var, and the compiler resolves a source file's path while .editorconfig
# discovery keeps the unresolved one - so no section ever matches and every rule in the file is
# ignored, compiler diagnostics included. That cost an afternoon here; it is not VM-specific.
FEED="$(cd "$(mktemp -d)" && pwd -P)"
WORK="$(cd "$(mktemp -d)" && pwd -P)"
# Its own directory rather than a subfolder of WORK: the consumer project's default **/*.cs glob
# would otherwise compile this project's sources into it.
KNOBS="$(cd "$(mktemp -d)" && pwd -P)"
trap 'rm -rf "${FEED}" "${WORK}" "${KNOBS}"' EXIT

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

# Every property the reference documents has to be declared CompilerVisibleProperty in the shipped
# targets, or MSBuild never forwards it and the knob silently takes its default in a real project.
# Read out of the nupkg rather than the source tree, because the packed copy is the one that runs.
GENERATOR_TARGETS="$(unzip -p "${FEED}/ValidationModules.SourceGenerator.${VERSION}.nupkg" \
    'build/ValidationModules.SourceGenerator.targets')"

while IFS= read -r property; do
    expect "${GENERATOR_TARGETS}" "CompilerVisibleProperty Include=\"${property}\"" \
        "${property} is documented but not CompilerVisibleProperty, so setting it does nothing"
done < <(sed -n 's/^## `\(ValidationModules_[A-Za-z]*\)`.*/\1/p' "${REPO_ROOT}/website/reference/msbuild.md")

# The Impl targets are a copy of the same list, and a knob added to one and not the other reaches
# our own generator and not a framework author's.
IMPL_TARGETS="$(unzip -p "${FEED}/ValidationModules.SourceGenerator.Impl.${VERSION}.nupkg" \
    'build/ValidationModules.SourceGenerator.Impl.targets')"

if [ "$(printf '%s' "${GENERATOR_TARGETS}" | grep -c CompilerVisibleProperty)" \
     != "$(printf '%s' "${IMPL_TARGETS}" | grep -c CompilerVisibleProperty)" ]; then
    echo "FAILED: the generator and Impl targets declare different property sets"
    exit 1
fi

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

# ---------------------------------------------------------------------------------------------
# The build knobs, and .editorconfig severity, through the package.
#
# Nothing else here reaches either. Every in-repo consumer references the generator as a
# ProjectReference with OutputItemType="Analyzer", which does not import a referenced project's
# build/*.targets, and the generator tests hand the harness `build_property.X` keys directly. Both
# prove the generator reads a key that MSBuild was never asked to produce, so deleting a
# CompilerVisibleProperty line left the whole suite green while every documented knob silently took
# its default in a real project.
#
# The project above deliberately keeps the defaults, so this is a second one rather than settings
# added to it.
echo "Checking the build knobs and .editorconfig severity"

cat > "${KNOBS}/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear/>
    <add key="local" value="${FEED}"/>
    <add key="nuget" value="https://api.nuget.org/v3/index.json"/>
  </packageSources>
</configuration>
EOF

cat > "${KNOBS}/Knobs.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <RestorePackagesPath>${KNOBS}/packages</RestorePackagesPath>
    <DisableImplicitNuGetFallbackFolder>true</DisableImplicitNuGetFallbackFolder>

    <!-- Four knobs whose effects are visible at run time and cannot be confused with each other. -->
    <ValidationModules_FieldNaming>SnakeCase</ValidationModules_FieldNaming>
    <ValidationModules_CodeNamespace>myapp</ValidationModules_CodeNamespace>
    <ValidationModules_DataAnnotations>Ignore</ValidationModules_DataAnnotations>
    <ValidationModules_CaptureValues>Disabled</ValidationModules_CaptureValues>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ValidationModules.Runtime" Version="${VERSION}"/>
    <PackageReference Include="ValidationModules.SourceGenerator" Version="${VERSION}" PrivateAssets="all"/>
  </ItemGroup>
</Project>
EOF

cat > "${KNOBS}/Program.cs" <<'PROGRAM'
using ValidationModules;
using ValidationModules.Constraints;
using Knobs;
using DataAnnotations = System.ComponentModel.DataAnnotations;

var errors = new AccountValidator().Validate(new Account()).Errors;
var fields = string.Join("; ", errors.Select(e => $"{e.Field}:{e.Code}"));

void Fail(string message) {
    Console.Error.WriteLine($"FAILED: {message}");
    Console.Error.WriteLine($"  errors were: {fields}");
    Environment.Exit(1);
}

// ValidationModules_FieldNaming=SnakeCase. The default camelCase would say postalCode.
if (!errors.Any(e => e.Field == "postal_code")) {
    Fail("FieldNaming=SnakeCase did not reach the generator");
}

// ValidationModules_CodeNamespace=myapp prefixes the codes this assembly invents...
if (!errors.Any(e => e.Code == "myapp.guest_missing")) {
    Fail("CodeNamespace=myapp did not reach the generator");
}

// ...and never the fixed vocabulary, which is what lets a client switch on a code.
if (!errors.Any(e => e.Field == "postal_code" && e.Code == "required")) {
    Fail("CodeNamespace prefixed a built-in code");
}

// ValidationModules_DataAnnotations=Ignore. The constraint is skipped rather than compiled.
if (errors.Any(e => e.Field == "ignored")) {
    Fail("DataAnnotations=Ignore did not reach the generator");
}

// ValidationModules_CaptureValues=Disabled. The emitter passes nothing, so no error carries one.
if (errors.Any(e => e.Value is not null)) {
    Fail("CaptureValues=Disabled did not reach the generator");
}

Console.WriteLine("Build knobs verified");

namespace Knobs {
    public sealed record Account {
        [Required] public string? PostalCode { get; init; }
        [Required(Code = "guest_missing")] public string? Guest { get; init; }
        [DataAnnotations.Required] public string? Ignored { get; init; }

        // Reports VM1201, which the .editorconfig check below suppresses.
        [Required] public int Age { get; init; }
    }
}
PROGRAM

# Without an .editorconfig the diagnostic must appear, or the second half proves nothing.
KNOBS_BUILD="$(dotnet build "${KNOBS}/Knobs.csproj" -c Release --nologo --no-incremental 2>&1)" || {
    printf '%s\n' "${KNOBS_BUILD}"
    echo "FAILED: the knobs project does not build"
    exit 1
}
expect "${KNOBS_BUILD}" "VM1201" \
    "VM1201 was not reported, so the .editorconfig check below would pass vacuously"

# The reference tells consumers to silence a diagnostic with exactly this line. Nothing had ever
# established that a generator-reported VM#### honours analyzer config at all.
cat > "${KNOBS}/.editorconfig" <<'EOF'
root = true

[*.cs]
dotnet_diagnostic.VM1201.severity = none
EOF

KNOBS_SUPPRESSED="$(dotnet build "${KNOBS}/Knobs.csproj" -c Release --nologo --no-incremental 2>&1)" || {
    printf '%s\n' "${KNOBS_SUPPRESSED}"
    echo "FAILED: the knobs project does not build with an .editorconfig"
    exit 1
}
reject "${KNOBS_SUPPRESSED}" "VM1201" \
    "dotnet_diagnostic.VM1201.severity = none did not suppress it, and the reference says it does"

# The bulk form does not reach a generator-reported diagnostic. dotnet_analyzer_diagnostic.* is
# applied by the analyzer driver, and 60 of the 61 descriptors are reported by the generator rather
# than by an analyzer - VM5003 is the only one from ValidateCallAnalyzer. The reference used to
# offer the category line as the way to reach the whole set, which silently reached almost none of
# it. Pinned in the direction it actually behaves, so that a Roslyn release closing the gap shows up
# here as a failure to go and delete a caveat from the documentation.
cat > "${KNOBS}/.editorconfig" <<'EOF'
root = true

[*.cs]
dotnet_analyzer_diagnostic.category-ValidationModules.Usage.severity = none
EOF

KNOBS_CATEGORY="$(dotnet build "${KNOBS}/Knobs.csproj" -c Release --nologo --no-incremental 2>&1)" || {
    printf '%s\n' "${KNOBS_CATEGORY}"
    echo "FAILED: the knobs project does not build with a category .editorconfig"
    exit 1
}
expect "${KNOBS_CATEGORY}" "VM1201" \
    "the category rule now reaches generator diagnostics; drop the caveat in reference/diagnostics.md"

rm "${KNOBS}/.editorconfig"

dotnet run --project "${KNOBS}/Knobs.csproj" -c Release --nologo --no-build
