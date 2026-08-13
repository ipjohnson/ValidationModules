using System.Reflection;
using BenchmarkDotNet.Running;
using ValidationModules.Benchmarks;
using ValidationModules.Benchmarks.Comparative;

// The opt-in suite: ValidationModules against FluentValidation and DataAnnotations.
//
// Separate from the default suite on purpose. Running it means restoring and loading competitor
// packages, and a comparison is a different question from "did this change make the library
// slower" - which is what the default suite is for and what CI should watch.
//
//   ./scripts/benchmark.sh --comparative
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks.Comparative
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks.Comparative -- --anyCategories=flat
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks.Comparative -- --runtime aot
//
// JIT by default here, unlike the default suite. The AOT job publishes every benchmark assembly
// through ILC, and doing that to a third-party engine on every run is a poor trade for a number
// that only moves when that engine does - so --runtime aot asks for it. It is worth asking for at
// least once: it is the reading behind the claim in §1 of the plan that FluentValidation's
// Expression.Compile falls back to the LINQ interpreter under Native AOT rather than throwing.
var (config, forwarded) = BenchmarkArguments.Parse(args, defaultToJitOnly: true);

// Before anything is measured: the rules are declared three times and nothing in the compiler
// relates them, so this refuses to run a comparison whose engines have drifted apart.
EngineParity.Verify();

BenchmarkSwitcher
    .FromAssembly(Assembly.GetExecutingAssembly())
    .Run(forwarded, config);
