# Comparative benchmark results

Generated from `BenchmarkDotNet.Artifacts/results` by the run described below. Committed so
that a number quoted on the website has a source someone else can check, and so that the next
run can be diffed against this one rather than against memory.

**Run date:** flat, nested and collection tables 2026-08-28; DI and construction tables
2026-08-16 · **Commit:** see git history for this file

```
BenchmarkDotNet v0.15.4, macOS 26.5.2 (25F84) [Darwin 25.5.0]
Apple M3 Pro, 1 CPU, 11 logical and 11 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
  jit    : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
```

Method, and the choices made in FluentValidation's favour: [`benchmarks/README.md`](README.md).

**Re-measured 2026-08-28 after re-pairing the rows.** The generated boolean fast path
(`IsValid`, 2026-08-26) post-dated this suite, which silently turned the cross-engine rows into
short-circuit-versus-full-report comparisons on the next run. Every cross-engine row now runs the
full pass and materializes a result on both sides; `IsValid` and the pooled collector are
measured as their own rows, labelled *no FV/DA equivalent*. The ValidationModules result rows
also moved from 40 B to 56 B between runs — the runtime's result object changed shape in the
interim — and the ratios below are computed against that, not against the older figure.

## Reading these

**Allocation is exact.** It is counted, not timed, so it does not move between runs and it is
the most trustworthy column here.

**The per-validation timings reproduce.** Flat, nested and collection rows landed within a few
percent of a previous independent run and carry standard deviations at or under 4%.

**The DI and construction timings do not, on this hardware.** FluentValidation's
scope-and-resolve and validator-construction benchmarks allocate ~11 KB each and are dominated
by GC, and their standard deviations run 30-45% of the mean. Between two runs of this suite the
construction figure moved from 6,029 ns to 2,995 ns. Raising iterations from 15 to 30 did not
help. Those rows are recorded here but are **not quoted as point estimates on the website** -
the allocation figures beside them are, because those are stable. A quiet, dedicated machine
would be needed to publish a number for them.

## Flat validation

`Job=jit Runtime=.NET 10.0 IterationCount=15 IterationTime=100ms WarmupCount=5 Categories=flat`

| Method                                                               | Mean        | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------------------------------------------------- |------------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| 'ValidationModules - clean'                                          |    31.92 ns |  0.284 ns |  0.266 ns |  1.00 |    0.01 | 0.0065 |      56 B |        1.00 |
| 'FluentValidation - clean'                                           |   178.93 ns |  0.770 ns |  0.721 ns |  5.61 |    0.05 | 0.0791 |     664 B |       11.86 |
| 'DataAnnotations - clean'                                            |   957.56 ns | 15.816 ns | 14.020 ns | 30.00 |    0.49 | 0.3129 |    2696 B |       48.14 |
| 'ValidationModules - 5 failures'                                     |   169.32 ns |  2.093 ns |  1.957 ns |  5.31 |    0.07 | 0.1265 |    1072 B |       19.14 |
| 'FluentValidation - 5 failures'                                      | 2,404.30 ns | 11.018 ns | 10.306 ns | 75.33 |    0.68 | 1.1676 |    9904 B |      176.86 |
| 'DataAnnotations - 5 failures'                                       | 1,582.04 ns |  9.297 ns |  7.763 ns | 49.57 |    0.46 | 0.4890 |    4136 B |       73.86 |
| 'ValidationModules - clean, pooled collector (no FV/DA equivalent)'  |    29.79 ns |  0.265 ns |  0.248 ns |  0.93 |    0.01 |      - |         - |        0.00 |
| 'ValidationModules - clean, boolean fast path (no FV/DA equivalent)' |    23.48 ns |  0.282 ns |  0.235 ns |  0.74 |    0.01 |      - |         - |        0.00 |

## Nested validation

`Job=jit Runtime=.NET 10.0 IterationCount=15 IterationTime=100ms WarmupCount=5 Categories=nested`

| Method                                                               | Mean        | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------------------------------------------------- |------------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| 'ValidationModules - clean'                                          |   110.32 ns |  1.836 ns |  1.717 ns |  1.00 |    0.02 | 0.0057 |      56 B |        1.00 |
| 'FluentValidation - clean'                                           | 1,817.27 ns | 68.309 ns | 63.896 ns | 16.48 |    0.61 | 0.6245 |    5224 B |       93.29 |
| 'DataAnnotations - clean, TOP LEVEL ONLY (does not descend)'         |   580.60 ns | 20.724 ns | 19.386 ns |  5.26 |    0.19 | 0.2153 |    1824 B |       32.57 |
| 'ValidationModules - clean, pooled collector (no FV/DA equivalent)'  |   102.71 ns |  3.785 ns |  3.541 ns |  0.93 |    0.03 |      - |         - |        0.00 |
| 'ValidationModules - clean, boolean fast path (no FV/DA equivalent)' |    84.69 ns |  1.454 ns |  1.289 ns |  0.77 |    0.02 |      - |         - |        0.00 |
| 'ValidationModules - 1 failure per level'                            |   266.05 ns |  2.983 ns |  2.790 ns |  2.41 |    0.04 | 0.1258 |    1072 B |       19.14 |
| 'FluentValidation - 1 failure per level'                             | 3,688.30 ns | 33.852 ns | 31.665 ns | 33.44 |    0.58 | 1.5553 |   13080 B |      233.57 |
| 'DataAnnotations - failing, TOP LEVEL ONLY (finds 1 of 4)'           |   665.24 ns |  3.814 ns |  2.978 ns |  6.03 |    0.09 | 0.2394 |    2016 B |       36.00 |

## Collection scaling

`Job=jit Runtime=.NET 10.0 IterationCount=15 IterationTime=100ms WarmupCount=5 Categories=collection`

| Method                                                     | Elements | Mean          | Error        | StdDev       | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|----------------------------------------------------------- |--------- |--------------:|-------------:|-------------:|------:|--------:|--------:|----------:|------------:|
| ValidationModules                                          | 1        |      25.88 ns |     0.288 ns |     0.225 ns |  1.00 |    0.01 |  0.0065 |      56 B |        1.00 |
| FluentValidation                                           | 1        |     416.74 ns |     5.065 ns |     4.490 ns | 16.10 |    0.21 |  0.2181 |    1856 B |       33.14 |
| 'ValidationModules - pooled collector (no FV equivalent)'  | 1        |      19.47 ns |     0.405 ns |     0.339 ns |  0.75 |    0.01 |       - |         - |        0.00 |
| 'ValidationModules - boolean fast path (no FV equivalent)' | 1        |      12.42 ns |     0.104 ns |     0.092 ns |  0.48 |    0.01 |       - |         - |        0.00 |
|                                                            |          |               |              |              |       |         |         |           |             |
| ValidationModules                                          | 10       |     171.42 ns |     1.778 ns |     1.663 ns |  1.00 |    0.01 |  0.0051 |      56 B |        1.00 |
| FluentValidation                                           | 10       |   2,480.17 ns |    18.168 ns |    16.106 ns | 14.47 |    0.16 |  1.0673 |    9056 B |      161.71 |
| 'ValidationModules - pooled collector (no FV equivalent)'  | 10       |     156.83 ns |     0.823 ns |     0.769 ns |  0.91 |    0.01 |       - |         - |        0.00 |
| 'ValidationModules - boolean fast path (no FV equivalent)' | 10       |     116.49 ns |     0.315 ns |     0.279 ns |  0.68 |    0.01 |       - |         - |        0.00 |
|                                                            |          |               |              |              |       |         |         |           |             |
| ValidationModules                                          | 100      |   1,568.10 ns |     7.158 ns |     6.346 ns |  1.00 |    0.01 |       - |      56 B |        1.00 |
| FluentValidation                                           | 100      |  23,962.75 ns |   174.609 ns |   163.329 ns | 15.28 |    0.12 |  9.6525 |   81776 B |    1,460.29 |
| 'ValidationModules - pooled collector (no FV equivalent)'  | 100      |   1,339.43 ns |     5.056 ns |     3.947 ns |  0.85 |    0.00 |       - |         - |        0.00 |
| 'ValidationModules - boolean fast path (no FV equivalent)' | 100      |   1,087.22 ns |     3.405 ns |     3.185 ns |  0.69 |    0.00 |       - |         - |        0.00 |
|                                                            |          |               |              |              |       |         |         |           |             |
| ValidationModules                                          | 1000     |  15,378.69 ns |    79.699 ns |    74.550 ns |  1.00 |    0.01 |       - |      56 B |        1.00 |
| FluentValidation                                           | 1000     | 236,261.66 ns | 2,313.763 ns | 2,164.296 ns | 15.36 |    0.15 | 98.2143 |  845776 B |   15,103.14 |
| 'ValidationModules - pooled collector (no FV equivalent)'  | 1000     |  14,946.94 ns |   295.846 ns |   230.977 ns |  0.97 |    0.02 |       - |         - |        0.00 |
| 'ValidationModules - boolean fast path (no FV equivalent)' | 1000     |  10,801.08 ns |    25.153 ns |    23.529 ns |  0.70 |    0.00 |       - |         - |        0.00 |

## Dependency injection

`Job=jit Runtime=.NET 10.0 IterationCount=30 IterationTime=100ms WarmupCount=10 Categories=di`

| Method                                                           | Mean         | Error      | StdDev       | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------------------------------------------------- |-------------:|-----------:|-------------:|------:|--------:|-------:|-------:|----------:|------------:|
| &#39;ValidationModules - register the generated table&#39;               | 1,073.258 ns | 146.416 ns |   209.985 ns | 1.036 |    0.28 | 0.6998 | 0.0865 |    5872 B |        1.00 |
| &#39;FluentValidation - AddValidatorsFromAssemblyContaining (scans)&#39; | 4,286.821 ns | 578.411 ns |   810.852 ns | 4.138 |    1.09 | 1.7224 | 0.0703 |   14620 B |        2.49 |
| &#39;FluentValidation - explicit registration, no scan&#39;              |   936.807 ns | 123.431 ns |   177.020 ns | 0.904 |    0.24 | 0.6763 | 0.0789 |    5696 B |        0.97 |
| &#39;ValidationModules - resolve IValidatorFor&lt;T&gt; (singleton)&#39;       |     6.087 ns |   1.527 ns |     2.238 ns | 0.006 |    0.00 |      - |      - |         - |        0.00 |
| &#39;FluentValidation - scope + resolve IValidator&lt;T&gt; (scoped)&#39;      | 4,431.614 ns | 894.064 ns | 1,310.507 ns | 4.278 |    1.49 | 1.2475 |      - |   11064 B |        1.88 |

## Validator construction

`Job=jit Runtime=.NET 10.0 IterationCount=30 IterationTime=100ms WarmupCount=10 Categories=startup`

| Method                                                               | Mean          | Error       | StdDev        | Median        | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------------------------------------------------- |--------------:|------------:|--------------:|--------------:|------:|--------:|-------:|----------:|------------:|
| &#39;ValidationModules - reach the singleton&#39;                            |     0.0000 ns |   0.0000 ns |     0.0000 ns |     0.0000 ns |     ? |       ? |      - |         - |           ? |
| &#39;FluentValidation - construct a validator&#39;                           | 2,994.6034 ns | 879.0381 ns | 1,315.7030 ns | 2,163.5531 ns |     ? |       ? | 1.2275 |   10752 B |           ? |
| &#39;FluentValidation - construct the nested order validator&#39;            | 3,092.6052 ns | 471.3852 ns |   645.2375 ns | 3,013.0960 ns |     ? |       ? | 1.2733 |   11432 B |           ? |
| &#39;ValidationModules - singleton + validate&#39;                           |    27.2019 ns |   0.4361 ns |     0.6527 ns |    26.8733 ns |     ? |       ? | 0.0046 |      40 B |           ? |
| &#39;FluentValidation - shared validator + validate (correct usage)&#39;     |   195.7705 ns |   1.7057 ns |     2.3911 ns |   195.2819 ns |     ? |       ? | 0.0789 |     664 B |           ? |
| &#39;FluentValidation - construct per call + validate (the §10.2 shape)&#39; | 3,277.7518 ns | 944.5330 ns | 1,354.6212 ns | 2,435.4554 ns |     ? |       ? | 1.2899 |   11416 B |           ? |

