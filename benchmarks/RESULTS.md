# Comparative benchmark results

Generated from `BenchmarkDotNet.Artifacts/results` by the run described below. Committed so
that a number quoted on the website has a source someone else can check, and so that the next
run can be diffed against this one rather than against memory.

**Run date:** 2026-08-16 · **Commit:** see git history for this file

```
BenchmarkDotNet v0.15.4, macOS 26.5.2 (25F84) [Darwin 25.5.0]
Apple M3 Pro, 1 CPU, 11 logical and 11 physical cores
.NET SDK 10.0.302
  [Host] : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
  jit    : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
```

Method, and the four choices made in FluentValidation's favour: [`benchmarks/README.md`](README.md).

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

| Method                                           | Mean        | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------------- |------------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| &#39;ValidationModules - clean&#39;                      |    26.36 ns |  1.022 ns |  0.956 ns |  1.00 |    0.05 | 0.0047 |      40 B |        1.00 |
| &#39;FluentValidation - clean&#39;                       |   196.03 ns |  4.108 ns |  3.843 ns |  7.45 |    0.29 | 0.0779 |     664 B |       16.60 |
| &#39;DataAnnotations - clean&#39;                        | 1,030.71 ns | 10.986 ns | 10.276 ns | 39.15 |    1.37 | 0.3166 |    2696 B |       67.40 |
| &#39;ValidationModules - 5 failures&#39;                 |   158.85 ns |  2.822 ns |  2.640 ns |  6.03 |    0.23 | 0.1118 |     936 B |       23.40 |
| &#39;FluentValidation - 5 failures&#39;                  | 2,537.56 ns | 58.037 ns | 54.287 ns | 96.38 |    3.81 | 1.1621 |    9904 B |      247.60 |
| &#39;DataAnnotations - 5 failures&#39;                   | 1,699.73 ns | 23.724 ns | 22.191 ns | 64.55 |    2.32 | 0.4880 |    4136 B |      103.40 |
| &#39;ValidationModules - clean, pooled collector&#39;    |    23.40 ns |  0.189 ns |  0.177 ns |  0.89 |    0.03 |      - |         - |        0.00 |
| &#39;ValidationModules - clean, materialized result&#39; |    28.33 ns |  0.269 ns |  0.251 ns |  1.08 |    0.04 | 0.0045 |      40 B |        1.00 |
| &#39;FluentValidation - clean, materialized result&#39;  |   192.26 ns |  3.215 ns |  3.007 ns |  7.30 |    0.27 | 0.0785 |     664 B |       16.60 |

## Nested validation

`Job=jit Runtime=.NET 10.0 IterationCount=15 IterationTime=100ms WarmupCount=5 Categories=nested`

| Method                                                       | Mean       | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------------------------- |-----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| &#39;ValidationModules - clean&#39;                                  |   104.2 ns |  1.24 ns |  1.10 ns |  1.00 |    0.01 | 0.0042 |      40 B |        1.00 |
| &#39;FluentValidation - clean&#39;                                   | 1,803.7 ns | 19.39 ns | 16.19 ns | 17.32 |    0.23 | 0.6073 |    5224 B |      130.60 |
| &#39;DataAnnotations - clean, TOP LEVEL ONLY (does not descend)&#39; |   588.0 ns |  7.58 ns |  7.09 ns |  5.65 |    0.09 | 0.2159 |    1824 B |       45.60 |
| &#39;ValidationModules - clean, pooled collector&#39;                |   119.3 ns |  4.48 ns |  4.19 ns |  1.15 |    0.04 |      - |         - |        0.00 |
| &#39;ValidationModules - 1 failure per level&#39;                    |   263.4 ns |  4.47 ns |  3.96 ns |  2.53 |    0.04 | 0.1241 |    1056 B |       26.40 |
| &#39;FluentValidation - 1 failure per level&#39;                     | 3,973.5 ns | 73.60 ns | 65.24 ns | 38.15 |    0.72 | 1.5303 |   13080 B |      327.00 |
| &#39;DataAnnotations - failing, TOP LEVEL ONLY (finds 1 of 4)&#39;   |   706.3 ns | 10.53 ns |  9.85 ns |  6.78 |    0.11 | 0.2352 |    2016 B |       50.40 |

## Collection scaling

`Job=jit Runtime=.NET 10.0 IterationCount=15 IterationTime=100ms WarmupCount=5 Categories=collection`

| Method                                 | Elements | Mean          | Error        | StdDev       | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|--------------------------------------- |--------- |--------------:|-------------:|-------------:|------:|--------:|--------:|----------:|------------:|
| ValidationModules                      | 1        |      18.39 ns |     0.208 ns |     0.185 ns |  1.00 |    0.01 |  0.0047 |      40 B |        1.00 |
| &#39;ValidationModules - pooled collector&#39; | 1        |      16.13 ns |     0.190 ns |     0.177 ns |  0.88 |    0.01 |       - |         - |        0.00 |
| FluentValidation                       | 1        |     432.59 ns |     8.500 ns |     7.950 ns | 23.53 |    0.48 |  0.2198 |    1856 B |       46.40 |
|                                        |          |               |              |              |       |         |         |           |             |
| ValidationModules                      | 10       |     128.73 ns |     1.782 ns |     1.580 ns |  1.00 |    0.02 |  0.0040 |      40 B |        1.00 |
| &#39;ValidationModules - pooled collector&#39; | 10       |     128.60 ns |     1.648 ns |     1.542 ns |  1.00 |    0.02 |       - |         - |        0.00 |
| FluentValidation                       | 10       |   2,667.94 ns |    40.305 ns |    37.701 ns | 20.73 |    0.37 |  1.0711 |    9056 B |      226.40 |
|                                        |          |               |              |              |       |         |         |           |             |
| ValidationModules                      | 100      |   1,274.39 ns |     4.955 ns |     4.138 ns |  1.00 |    0.00 |       - |      40 B |        1.00 |
| &#39;ValidationModules - pooled collector&#39; | 100      |   1,232.77 ns |    14.549 ns |    13.609 ns |  0.97 |    0.01 |       - |         - |        0.00 |
| FluentValidation                       | 100      |  25,644.46 ns |   504.530 ns |   471.938 ns | 20.12 |    0.36 |  9.6154 |   81776 B |    2,044.40 |
|                                        |          |               |              |              |       |         |         |           |             |
| ValidationModules                      | 1000     |  11,970.31 ns |    84.952 ns |    70.939 ns |  1.00 |    0.01 |       - |      40 B |        1.00 |
| &#39;ValidationModules - pooled collector&#39; | 1000     |  12,473.42 ns |   203.520 ns |   190.373 ns |  1.04 |    0.02 |       - |         - |        0.00 |
| FluentValidation                       | 1000     | 250,119.47 ns | 6,895.225 ns | 5,757.825 ns | 20.90 |    0.48 | 93.7500 |  845776 B |   21,144.40 |

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

