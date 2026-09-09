# Benchmark 方法与对比报告

本报告比较 `3e80b5a` 提交时的实现与本次优化后的工作树。数据采集日期为 2026-09-08，使用同一台机器：

- macOS Sequoia 15.7.9，Arm64
- .NET SDK `10.0.100`，运行时 `.NET 10.0.25.52411`
- BenchmarkDotNet `0.15.8`
- Concurrent workstation GC

基线和候选版本均以 Release 构建。BenchmarkDotNet 使用 `ShortRun`，包含 3 次预热和 3 次测量。命令会导出包含
误差范围的原始结果；样本量较小，仓库中的摘要数字用于趋势检查，不代表容量或 SLA 保证。

## 复现命令

在仓库根目录执行以下命令。受限开发环境下，临时 NuGet cache 路径可避免 BenchmarkDotNet 自动生成项目的权限
问题。

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

`ScenarioReportRunner` 使用假的单调 `TimeProvider`，覆盖延迟衰减、冷 key 突发写入时的热点保留和新 key factory
限流。`CapacityPressureBenchmarks` 在 `IterationSetup` 中预填充并预热可复用候选队列，分别测量已有 key 写入和
压力下的新 key 写入。每次 benchmark invocation 执行 16 次逻辑写入，并声明 `OperationsPerInvoke = 16`，因此报告
中的耗时和分配均按单次写入统计。`ConcurrentReadBenchmarks` 使用 4 个 worker、每个 16,384 次读取，覆盖不同 key、
热点 key 以及是否启用 `MeterListener`。

## 确定性策略场景

完整机器可读数据位于
[`docs/benchmark-results/optimization-2026-09-08.json`](benchmark-results/optimization-2026-09-08.json)。

| 场景 | 基线（`3e80b5a`） | 优化后 | 结果 |
| --- | ---: | ---: | --- |
| 60 秒后的频次分布（`capacity=10,000`，初始频次 8） | `4:1,667`、`8:8,333` | `4:10,000` | 即使维护扫描只有预算切片，逻辑衰减也能让所有 entry 完成归一。 |
| 360 秒后的频次分布 | `4:10,000` | `1:10,000` | 首次观察时一次性补足所有已经经过的 half-life。 |
| 插入 1,000 个冷 key 后的热点保留（`capacity=1,000`） | `91/1,000`（9.1%） | `900/1,000`（90.0%） | 保护窗口只作为同频次 tie-breaker，高频 entry 不再因“新”而被整体排除。 |
| 4 个阻塞的新 key factory（`capacity=1`） | 启动 4、pending 4、拒绝 0 | 启动 1、pending 1、拒绝 3 | `MaxInflight` 限制 pending 新 key 工作；同 key 调用仍共享一个 factory。 |

最后一行是 `LfuCacheOptions.MaxInflight` 暴露的明确行为。默认值为 `null`，继承 keyspace 的 `Capacity`；需要其他
限制时可显式设置正数。已经运行的 factory 会一直保留名额到退出。

## 容量压力

以下是对应 `ShortRun` 的均值和每次操作托管内存分配：

| 方法 | Capacity | 基线均值 | 优化后均值 | 基线分配 | 优化后分配 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `SetExistingKeyAfterPressure` | 1,000 | 291.646 ns | 313.438 ns | 64 B | 64 B |
| `SetNewKeyUnderPressure` | 1,000 | 39.829 µs | 10.319 µs | 625 B | 110 B |
| `SetExistingKeyAfterPressure` | 100,000 | 796.448 ns | 655.417 ns | 64 B | 64 B |
| `SetNewKeyUnderPressure` | 100,000 | 287.878 µs | 246.490 µs | 50,125 B | 110 B |

新 key 路径受益于一次字典枚举和可复用的有界候选队列。已有 key 写入不会进入淘汰，短跑中的小幅差异属于
噪声。同步淘汰仍然是 `O(N log k)`，跨过 hard watermark 时写入线程可能承担该成本。
基线 `Capacity=1,000` 的新 key 运行中有一个较慢样本，因此 `39.829 us` 均值比其他行更不稳定；将它用于阈值判断
时应改用更长的 job。

## 读取路径

本次直接类型化命中基准基本不变：

| Benchmark | 基线 | 优化后 |
| --- | ---: | ---: |
| `CacheReadBenchmarks.TypedCacheHit` | 20.108 ns，0 B | 20.131 ns，0 B |

并发 benchmark 用于暴露争用和 instrumentation 成本。一次匹配的 16,384 操作 `ShortRun` 得到以下均值；误差范围
较宽，重新评估前应重复运行：

| 访问模式 | Listener | 基线 | 优化后 |
| --- | --- | ---: | ---: |
| 不同 key | 关 | 2.887 ms | 1.593 ms |
| 不同 key | 开 | 4.020 ms | 3.171 ms |
| 热点 key | 关 | 4.832 ms | 6.321 ms |
| 热点 key | 开 | 6.233 ms | 7.955 ms |

本次实现主要针对计数器写入争用，并保持直接命中路径零分配。热点 key 会让所有 worker 更新同一个 entry 的打包
频次状态，是后续调优的重要基准。

## 结论

最稳定的收益在策略和内存行为：延迟衰减语义正确，热点保留显著提高，淘汰不再按容量分配候选队列。读取基准会
继续作为回归护栏；调优频次状态争用或评估生产负载时，建议使用 `--job medium` 和多次独立进程运行。
