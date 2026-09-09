# Benchmark Methodology and Results

This report compares the implementation at commit `3e80b5a` with the optimized working tree used for this change.
The measurements were collected on 2026-09-08 on the same machine:

- macOS Sequoia 15.7.9, Arm64
- .NET SDK `10.0.100`, .NET `10.0.25.52411`
- BenchmarkDotNet `0.15.8`
- Concurrent workstation GC

The baseline and candidate were built in Release mode. BenchmarkDotNet used `ShortRun` (3 warmup and 3 measured
iterations). The commands export raw results with confidence intervals; the small number of iterations means the
checked-in summary is suitable for trend checks, not a capacity or SLA guarantee.

## Reproduce

Run these commands from the repository root. The temporary NuGet cache path is writable by BenchmarkDotNet's generated
projects in restricted development environments.

```bash
NUGET_HTTP_CACHE_PATH=/private/tmp/lfu-nuget-http \
  dotnet restore tests/EventHorizon.LfuCache.Benchmarks/EventHorizon.LfuCache.Benchmarks.csproj

NUGET_HTTP_CACHE_PATH=/private/tmp/lfu-nuget-http \
  dotnet build tests/EventHorizon.LfuCache.Benchmarks/EventHorizon.LfuCache.Benchmarks.csproj \
  -c Release --no-restore

NUGET_HTTP_CACHE_PATH=/private/tmp/lfu-nuget-http \
  dotnet run --project tests/EventHorizon.LfuCache.Benchmarks/EventHorizon.LfuCache.Benchmarks.csproj \
  -c Release --no-build -- \
  --scenario-report /private/tmp/lfu-scenario-report.json

NUGET_HTTP_CACHE_PATH=/private/tmp/lfu-nuget-http \
  dotnet run --project tests/EventHorizon.LfuCache.Benchmarks/EventHorizon.LfuCache.Benchmarks.csproj \
  -c Release --no-build -- \
  --job short --filter '*CacheReadBenchmarks.TypedCacheHit*' \
  --exporters json markdown csv --artifacts /private/tmp/lfu-bdn-read

NUGET_HTTP_CACHE_PATH=/private/tmp/lfu-nuget-http \
  dotnet run --project tests/EventHorizon.LfuCache.Benchmarks/EventHorizon.LfuCache.Benchmarks.csproj \
  -c Release --no-build -- \
  --job short --filter '*CapacityPressureBenchmarks*' \
  --exporters json markdown csv --artifacts /private/tmp/lfu-bdn-capacity

NUGET_HTTP_CACHE_PATH=/private/tmp/lfu-nuget-http \
  dotnet run --project tests/EventHorizon.LfuCache.Benchmarks/EventHorizon.LfuCache.Benchmarks.csproj \
  -c Release --no-build -- \
  --job short --filter '*ConcurrentReadBenchmarks*' \
  --exporters json markdown csv --artifacts /private/tmp/lfu-bdn-concurrent
```

`ScenarioReportRunner` is deterministic and uses a fake monotonic `TimeProvider`. It covers delayed decay, hot-key
retention during a cold-key burst, and the new-key factory limit. `CapacityPressureBenchmarks` pre-fills the cache in
`IterationSetup`, primes the reusable candidate queue, and measures both an existing-key write and a new-key write.
Each benchmark invocation performs 16 logical writes and declares `OperationsPerInvoke = 16`, so the reported time and
allocation are per write. `ConcurrentReadBenchmarks` uses four workers and 16,384 reads per worker, with different-key
and hot-key access patterns and an optional `MeterListener`.

## Deterministic policy scenarios

The complete machine-readable report is checked in at
[`docs/benchmark-results/optimization-2026-09-08.json`](benchmark-results/optimization-2026-09-08.json).

| Scenario | Baseline (`3e80b5a`) | Optimized tree | Effect |
| --- | ---: | ---: | --- |
| Frequency distribution after 60 seconds (`capacity=10,000`, initial frequency 8) | `4:1,667`, `8:8,333` | `4:10,000` | Logical decay normalizes every entry even when maintenance scans only a budgeted slice. |
| Frequency distribution after 360 seconds | `4:10,000` | `1:10,000` | All elapsed half-lives are applied on first observation. |
| Hot keys retained after inserting 1,000 cold keys (`capacity=1,000`) | `91/1,000` (9.1%) | `900/1,000` (90.0%) | Protection is a same-frequency tie-breaker, so frequently used entries remain eligible on their frequency. |
| Four blocked new-key factories (`capacity=1`) | 4 started, 4 pending, 0 rejected | 1 started, 1 pending, 3 rejected | `MaxInflight` bounds pending new-key work; same-key callers still share one factory. |

The last row is an intentional behavior exposed by `LfuCacheOptions.MaxInflight`. Its default is `null`, which inherits
the keyspace `Capacity`; set a positive value explicitly when a different bound is required. Existing running factories
keep their slots until they exit.

## Capacity pressure

Means and managed allocation per operation from the matching `ShortRun` runs:

| Method | Capacity | Baseline mean | Optimized mean | Baseline allocation | Optimized allocation |
| --- | ---: | ---: | ---: | ---: | ---: |
| `SetExistingKeyAfterPressure` | 1,000 | 291.646 ns | 313.438 ns | 64 B | 64 B |
| `SetNewKeyUnderPressure` | 1,000 | 39.829 µs | 10.319 µs | 625 B | 110 B |
| `SetExistingKeyAfterPressure` | 100,000 | 796.448 ns | 655.417 ns | 64 B | 64 B |
| `SetNewKeyUnderPressure` | 100,000 | 287.878 µs | 246.490 µs | 50,125 B | 110 B |

The new-key path benefits from one dictionary enumeration and a reusable bounded candidate queue. Existing-key writes do
not enter eviction, so their small differences are within the short-run noise. Synchronous eviction still has an
`O(N log k)` cost and can be paid by the writer when the hard watermark is crossed.
The baseline `Capacity=1,000` new-key run contained one slow sample, so its `39.829 us` mean is less stable than the
other rows; use a longer job when using that value for a threshold.

## Read path

The direct typed-hit benchmark was effectively unchanged in this run:

| Benchmark | Baseline | Optimized |
| --- | ---: | ---: |
| `CacheReadBenchmarks.TypedCacheHit` | 20.108 ns, 0 B | 20.131 ns, 0 B |

The concurrent benchmark is intended to expose contention and instrumentation cost. One matching 16,384-operation
ShortRun produced the following means; its broad confidence intervals show why these values should be rerun before
making a deployment decision:

| Access pattern | Listener | Baseline | Optimized |
| --- | --- | ---: | ---: |
| different keys | off | 2.887 ms | 1.593 ms |
| different keys | on | 4.020 ms | 3.171 ms |
| hot key | off | 4.832 ms | 6.321 ms |
| hot key | on | 6.233 ms | 7.955 ms |

The implementation change targeted counter write contention and kept the direct hit path allocation-free. The hot-key
case remains a useful follow-up benchmark because all workers update one entry's packed frequency state.

## Interpretation

The strongest repeatable improvements are policy and memory behavior: delayed decay is correct, hot-key retention is
substantially better, and eviction no longer allocates a candidate queue proportional to capacity. The read benchmark
is retained as a guardrail; use a longer job (for example `--job medium`) and several process repetitions when tuning
frequency-state contention or evaluating a production workload.
