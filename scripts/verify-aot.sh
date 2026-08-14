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

Console.WriteLine("Native AOT publish verified: paths, async merge, and zero-allocation clean pass.");

static void Expect(bool condition, string message) {
    if (!condition) {
        Console.Error.WriteLine($"FAILED: {message}");
        Environment.Exit(1);
    }
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
