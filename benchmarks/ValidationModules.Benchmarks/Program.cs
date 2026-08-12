using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using ValidationModules.Benchmarks;

// Native AOT is the target runtime and the one the emission-shape question turns on: whether ILC
// inlines the runtime helpers back into the caller. The JIT job is kept alongside it because a
// difference that appears under one and not the other is worth knowing about.
//
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks
//   dotnet run -c Release --project benchmarks/ValidationModules.Benchmarks -- --job short
var config = DefaultConfig.Instance
    .AddJob(Job.Default.WithRuntime(CoreRuntime.Core10_0).WithId("jit").WithWarmupCount(5).WithIterationCount(15))
    .AddJob(Job.Default.WithRuntime(NativeAotRuntime.Net10_0).WithId("aot").WithWarmupCount(5).WithIterationCount(15));

BenchmarkSwitcher.FromTypes([typeof(EmissionShapeBenchmarks)]).Run(args, config);
