#!/usr/bin/env bash
#
# Publishes a throwaway console app against ValidationModules.Runtime with PublishAot=true, and
# fails on any IL trim/AOT warning.
#
# The csproj already escalates IL2026;IL2055;IL2067;IL2072;IL2075;IL2087;IL3050 to errors, which
# catches what the Roslyn analyzers see at build time. This catches what only ILC sees, when the
# whole graph is in front of it — and it proves the published binary actually runs, which is the
# claim the library exists to make.
#
# Usage: ./scripts/verify-aot.sh [runtime-identifier]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_PROJECT="${REPO_ROOT}/src/ValidationModules.Runtime/ValidationModules.Runtime.csproj"
GENERATOR_PROJECT="${REPO_ROOT}/src/ValidationModules.SourceGenerator/ValidationModules.SourceGenerator.csproj"

if [ $# -ge 1 ]; then
    RID="$1"
elif [ "$(uname -s)" = "Darwin" ]; then
    RID="osx-$([ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64)"
else
    RID="linux-x64"
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

echo "Verifying Native AOT publish for ${RID}"

cat > "${WORK_DIR}/AotProbe.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <!-- One diagnostic per site rather than a single rolled-up summary, so the log names the cause. -->
    <TrimmerSingleWarn>false</TrimmerSingleWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${RUNTIME_PROJECT}"/>
    <!--
        The generator runs here too. Hand-written validators alone cannot prove the shipped
        product is AOT-safe: what this file imitates drifts from what the emitter writes, and it
        drifted once already - see the generated-registration section of Program.cs.
    -->
    <ProjectReference Include="${GENERATOR_PROJECT}"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0"/>
  </ItemGroup>
</Project>
EOF

cat > "${WORK_DIR}/Program.cs" <<'EOF'
using Microsoft.Extensions.DependencyInjection;
using ValidationModules;

// Hand-written validators in the shape the generator emits: no static singleton, nested
// validators injected, registered by implementation type so the container constructs them.
// That last part is what this probe exists to prove is AOT-safe - MS.DI selects the constructor
// at run time, and if the trimmer could not follow it the publish would fail here.
var services = new ServiceCollection();
services.AddSingleton<IValidatorFor<Address>, AddressValidator>();
services.AddSingleton<IValidatorFor<Toy>, ToyValidator>();
services.AddSingleton<IValidatorFor<Pet>, PetValidator>();
services.AddSingleton<IAsyncValidatorFor<Pet>, PetBusinessRule>();
services.AddValidationRunner<Pet>();

var provider = services.BuildServiceProvider();

var pet = new Pet { Home = new Address(), Toys = [new Toy(), new Toy()], Ratio = 1.05 };
var result = provider.GetRequiredService<IValidatorFor<Pet>>().Validate(pet);

var fields = string.Join(", ", result.Errors.Select(e => e.Field));
Expect(fields == "name, home.postalCode, toys[0].name, toys[1].name", $"paths: {fields}");

// Structural and business rules, merged, with the context crossing an await.
var runner = provider.CreateScope().ServiceProvider.GetRequiredService<ValidationRunner<Pet>>();
var merged = await runner.ValidateAsync(
    new Pet { Name = "Rex", Toys = [new Toy { Name = "ball" }], Ratio = 1.05 });
var async = string.Join(", ", merged.Errors.Select(e => $"{e.Field}:{e.Code}"));
Expect(async == "home.postalCode:unknown", $"async: {async}");

// A clean pass over a reused collector must not allocate. The validator is held, as a container
// holds a singleton - constructing one per call would allocate the object and its nested array.
var standalone = new PetValidator();
var collector = new ValidationErrorCollector();
var clean = new Pet {
    Name = "Rex",
    Home = new Address { PostalCode = "1" },
    Toys = [new Toy { Name = "b" }],
    Codes = ["a", "b", "c"],
    Ratio = 1.05,
};
for (var i = 0; i < 100; i++) {
    collector.Reset();
    standalone.ValidateInto(collector, clean);
}
Expect(collector.Count == 0, "clean pass produced errors");

var before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 500; i++) {
    collector.Reset();
    standalone.ValidateInto(collector, clean);
}
var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Expect(allocated == 0, $"clean pass allocated {allocated} bytes");

// The generated product, registered the way the generator tells consumers to register it, for a
// type with NO IAsyncValidatorFor<T> - which is the ordinary case, since most types have no
// business rule.
//
// This is the regression test for a fault that shipped: ValidationRunner<T> took its two
// dependencies as constructor-injected IEnumerable<>, MS.DI satisfied the empty async one through
// Array.CreateInstance(Type, int), and ILC had never emitted IAsyncValidatorFor<T>[] because
// nothing named it statically. The publish reported no warning and the resolve threw
// NotSupportedException. Everything above passed throughout, because the hand-written probe
// registered an IAsyncValidatorFor<Pet> and that one registration hid it.
//
// Keep a type here that has no async rule, or this comes back.
var generated = new ServiceCollection();
generated.AddAotProbeValidators();

using var generatedProvider = generated.BuildServiceProvider();
using var generatedScope = generatedProvider.CreateScope();

var reading = generatedScope.ServiceProvider.GetRequiredService<ValidationRunner<Reading>>();
var readingResult = reading.Validate(new Reading { Label = null, Ratio = 5 });
var readingFields = string.Join(", ", readingResult.Errors.Select(e => $"{e.Field}:{e.Code}"));
Expect(readingFields == "label:required, ratio:range", $"generated runner: {readingFields}");

// The same type through IValidatorFor<T> rather than the runner, since they resolve differently.
var readingValidator = generatedScope.ServiceProvider.GetRequiredService<IValidatorFor<Reading>>();
Expect(readingValidator.Validate(new Reading { Label = "ok", Ratio = 1 }).IsValid, "generated validator");

Console.WriteLine("Native AOT publish verified: paths, async merge, zero-allocation clean pass,");
Console.WriteLine("and the generated registration for a type with no async rule.");

static void Expect(bool condition, string message) {
    if (!condition) {
        Console.Error.WriteLine($"FAILED: {message}");
        Environment.Exit(1);
    }
}

// Constraint attributes, so this one is compiled by the generator rather than written by hand.
// Deliberately has no IAsyncValidatorFor<Reading>.
//
// public, not internal: the emitter always writes "public sealed partial class …Validator", so an
// internal model yields CS0051 in generated code. Make this internal to reproduce that.
public sealed record Reading {
    [ValidationModules.Constraints.Required]
    public string? Label { get; init; }

    [ValidationModules.Constraints.Range(0, 1)]
    public int Ratio { get; init; }
}

sealed record Address {
    public string? PostalCode { get; init; }
}

sealed record Toy {
    public string? Name { get; init; }
}

sealed record Pet {
    public string? Name { get; init; }
    public Address? Home { get; init; }
    public IReadOnlyList<Toy> Toys { get; init; } = [];

    // What [UniqueItems] and [MultipleOf] compile against. Here so the zero-allocation assertion
    // and the trim analysis both cover ConstraintChecks, which is the only part of a validation
    // pass that is not a comparison.
    public IReadOnlyList<string> Codes { get; init; } = [];
    public double Ratio { get; init; }
}

sealed class AddressValidator : IValidatorFor<Address> {
    public AddressValidator() { }

    public void Validate(ref ValidationContext context, Address value) {
        if (string.IsNullOrWhiteSpace(value.PostalCode)) {
            context.Add("postalCode", "required", "postalCode is required.");
        }
    }
}

sealed class ToyValidator : IValidatorFor<Toy> {
    public ToyValidator() { }

    public void Validate(ref ValidationContext context, Toy value) {
        if (string.IsNullOrWhiteSpace(value.Name)) {
            context.Add("name", "required", "name is required.");
        }
    }
}

sealed class PetValidator : IValidatorFor<Pet> {
    private IValidatorFor<Address>[]? _home;
    private IValidatorFor<Toy>[]? _toys;

    public PetValidator(IEnumerable<IValidatorFor<Address>> home, IEnumerable<IValidatorFor<Toy>> toys) {
        _home = System.Linq.Enumerable.ToArray(home);
        _toys = System.Linq.Enumerable.ToArray(toys);
    }

    public PetValidator() { }

    private IValidatorFor<Address>[] HomeValidators =>
        _home ??= new IValidatorFor<Address>[] { new AddressValidator() };

    private IValidatorFor<Toy>[] ToysValidators =>
        _toys ??= new IValidatorFor<Toy>[] { new ToyValidator() };

    public void Validate(ref ValidationContext context, Pet value) {
        if (string.IsNullOrWhiteSpace(value.Name)) {
            context.Add("name", "required", "name is required.");
        }

        if (value.Home is { } home) {
            var nested = context.Push("home");
            var hv = HomeValidators;
            for (var v = 0; v < hv.Length; v++) hv[v].Validate(ref nested, home);
        }

        if (!ConstraintChecks.AllUnique(value.Codes)) {
            context.AddUniqueItems("codes");
        }

        if (!ConstraintChecks.IsMultipleOf(value.Ratio, 0.01m)) {
            context.AddMultipleOf("ratio", 0.01m);
        }

        for (var i = 0; i < value.Toys.Count; i++) {
            var item = context.PushIndex("toys", i);
            var tv = ToysValidators;
            for (var v = 0; v < tv.Length; v++) tv[v].Validate(ref item, value.Toys[i]);
        }
    }
}

sealed class PetBusinessRule : IAsyncValidatorFor<Pet> {
    public async ValueTask ValidateAsync(ValidationContext context, Pet value, CancellationToken cancellationToken) {
        var home = context.Push("home");

        await Task.Yield();

        home.Add("postalCode", "unknown", "postal code not recognised.");
    }
}
EOF

PUBLISH_LOG="${WORK_DIR}/publish.log"

if ! dotnet publish "${WORK_DIR}/AotProbe.csproj" -r "${RID}" -c Release --nologo > "${PUBLISH_LOG}" 2>&1; then
    echo "Native AOT publish failed:"
    cat "${PUBLISH_LOG}"
    exit 1
fi

# ILC reports trim and AOT problems as warnings, not failures, so grep is the gate.
if grep -Eq "IL[0-9]{4}" "${PUBLISH_LOG}"; then
    echo "Native AOT publish produced IL warnings:"
    grep -E "IL[0-9]{4}" "${PUBLISH_LOG}"
    exit 1
fi

BINARY="${WORK_DIR}/bin/Release/net10.0/${RID}/publish/AotProbe"

if [ ! -x "${BINARY}" ]; then
    echo "Expected a published binary at ${BINARY}"
    exit 1
fi

"${BINARY}"

echo "Binary size: $(du -h "${BINARY}" | cut -f1)"

# ---------------------------------------------------------------------------
# Second probe: minimal, and minimal on purpose.
#
# ValidationRunner<T> once resolved its two IEnumerable<> dependencies through MS.DI constructor
# injection, which builds the backing array with Array.CreateInstance(Type, int). For
# IAsyncValidatorFor<T> nothing names that array statically - most types have no business rule -
# so ILC never emitted it and the resolve threw NotSupportedException, after a publish that
# reported no warning at all.
#
# It has to be its own binary. ILC decides what to emit from the whole program, and the probe
# above does not reproduce the fault even with the fix reverted: one IAsyncValidatorFor<Pet>
# anywhere in the image is enough to bring the array type in for every other T. A probe that also
# tests paths, async merge and allocation is, for this specific fault, too rich to fail. Keep this
# one down to one type, one registration call, one resolve - anything added here can silently
# disarm it.
# ---------------------------------------------------------------------------

echo "Verifying the minimal generated-registration path"

MIN_DIR="${WORK_DIR}/minimal"
mkdir -p "${MIN_DIR}"

cat > "${MIN_DIR}/AotMinimal.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <TrimmerSingleWarn>false</TrimmerSingleWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${RUNTIME_PROJECT}"/>
    <ProjectReference Include="${GENERATOR_PROJECT}"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0"/>
  </ItemGroup>
</Project>
EOF

cat > "${MIN_DIR}/Program.cs" <<'EOF'
using Microsoft.Extensions.DependencyInjection;
using ValidationModules;
using ValidationModules.Constraints;

// Nothing here is incidental. No async validator, no second type, no other resolve.
var services = new ServiceCollection();
services.AddAotMinimalValidators();

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();

var runner = scope.ServiceProvider.GetRequiredService<ValidationRunner<Thing>>();
var result = runner.Validate(new Thing());

if (result.Errors.Count != 1) {
    Console.Error.WriteLine($"FAILED: expected 1 error, got {result.Errors.Count}");
    Environment.Exit(1);
}

Console.WriteLine("Minimal generated-registration path verified.");

public sealed record Thing {
    [Required]
    public string? Name { get; init; }
}
EOF

MIN_LOG="${MIN_DIR}/publish.log"

if ! dotnet publish "${MIN_DIR}/AotMinimal.csproj" -r "${RID}" -c Release --nologo > "${MIN_LOG}" 2>&1; then
    echo "Minimal Native AOT publish failed:"
    cat "${MIN_LOG}"
    exit 1
fi

if grep -Eq "IL[0-9]{4}" "${MIN_LOG}"; then
    echo "Minimal Native AOT publish produced IL warnings:"
    grep -E "IL[0-9]{4}" "${MIN_LOG}"
    exit 1
fi

MIN_BINARY="${MIN_DIR}/bin/Release/net10.0/${RID}/publish/AotMinimal"

if [ ! -x "${MIN_BINARY}" ]; then
    echo "Expected a published binary at ${MIN_BINARY}"
    exit 1
fi

"${MIN_BINARY}"
