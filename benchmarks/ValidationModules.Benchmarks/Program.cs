using System.Reflection;
using BenchmarkDotNet.Running;
using ValidationModules.Benchmarks;

// The default suite: ValidationModules on its own, no competitor packages referenced. Comparisons
// against FluentValidation and DataAnnotations live in ValidationModules.Benchmarks.Comparative and
// are opted into explicitly - see benchmarks/README.md.
//
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks -- --list flat
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks -- --anyCategories=endtoend
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks -- --runtime jit --job short
//
// Both runtimes by default: Native AOT is what the library targets, so a number taken only under
// the JIT is only half an answer. --runtime jit drops the ILC publish when the question is relative
// cost rather than the published binary.
//
// Types are discovered rather than listed, so a new benchmark class is picked up by existing it.
var (config, forwarded) = BenchmarkArguments.Parse(args);

BenchmarkSwitcher
    .FromAssembly(Assembly.GetExecutingAssembly())
    .Run(forwarded, config);
