# 基准测试方法与对比报告

本报告比较 `3e80b5a` 提交时的实现与当前工作树中的优化版本。数据于 2026-09-08 采集，测试使用同一台机器：

- macOS Sequoia 15.7.9，Arm64
- .NET SDK `10.0.100`，运行时 `.NET 10.0.25.52411`
- BenchmarkDotNet `0.15.8`
- GC 模式：Concurrent workstation

基线和候选版本均以 Release 配置构建。BenchmarkDotNet 使用 `ShortRun`，包含 3 次预热和 3 次测量。命令会导出包含
误差范围的原始结果；样本量较小，仓库中的摘要数字用于趋势检查，不代表容量或 SLA 保证。

## 复现命令

在仓库根目录执行以下命令。受限开发环境下，指定临时 NuGet 缓存路径可以避免 BenchmarkDotNet 自动生成项目时的
权限问题。

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

`ScenarioReportRunner` 使用模拟的单调时钟 `TimeProvider`，覆盖延迟衰减、冷 key 突发写入场景下的热点保留，以及新 key
工厂函数的并发限制。`CapacityPressureBenchmarks` 在 `IterationSetup` 中预填充并预热可复用的候选队列，分别测量已有
key 写入和压力下的新 key 写入。每次基准测试调用执行 16 次逻辑写入，并声明 `OperationsPerInvoke = 16`，因此报告
中的耗时和分配均按单次写入统计。`ConcurrentReadBenchmarks` 使用 4 个工作线程（worker），每个执行 16,384 次读取，
覆盖不同 key、热点 key 两种访问模式，以及是否启用 `MeterListener`。

## 确定性策略场景

完整机器可读数据位于
[`docs/benchmark-results/optimization-2026-09-08.json`](benchmark-results/optimization-2026-09-08.json)。

| 场景 | 基线（`3e80b5a`） | 优化后 | 结果 |
| --- | ---: | ---: | --- |
| 60 秒后的频次分布（`capacity=10,000`，初始频次 8） | `4:1,667`、`8:8,333` | `4:10,000` | 即使维护扫描只有预算切片，逻辑衰减也能让所有 entry 完成归一化。 |
| 360 秒后的频次分布 | `4:10,000` | `1:10,000` | 首次观察时一次性补足已经经过的所有半衰期。 |
| 插入 1,000 个冷 key 后的热点保留（`capacity=1,000`） | `91/1,000`（9.1%） | `900/1,000`（90.0%） | 保护窗口只作为同频次时的次级排序规则，高频 entry 不再因“新”而被整体排除。 |
| 4 个阻塞的新 key 工厂函数（`capacity=1`） | 启动 4、pending 4、拒绝 0 | 启动 1、pending 1、拒绝 3 | `MaxInflight` 限制处于 pending 状态的新 key 操作；同一 key 的调用仍共享一个工厂函数。 |

最后一行体现了 `LfuCacheOptions.MaxInflight` 的明确行为。默认值为 `null`，继承 keyspace 的 `Capacity`；需要其他
限制时可显式设置正数。已经运行的工厂函数会持续占用名额，直到退出。

## 容量压力

以下为对应 `ShortRun` 的均值，以及每次操作的托管内存分配：

| 方法 | Capacity | 基线均值 | 优化后均值 | 基线分配 | 优化后分配 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `SetExistingKeyAfterPressure` | 1,000 | 291.646 ns | 313.438 ns | 64 B | 64 B |
| `SetNewKeyUnderPressure` | 1,000 | 39.829 µs | 10.319 µs | 625 B | 110 B |
| `SetExistingKeyAfterPressure` | 100,000 | 796.448 ns | 655.417 ns | 64 B | 64 B |
| `SetNewKeyUnderPressure` | 100,000 | 287.878 µs | 246.490 µs | 50,125 B | 110 B |

新 key 路径受益于一次字典枚举和可复用的有界候选队列。已有 key 写入不会进入淘汰，`ShortRun` 中的小幅差异属于
噪声。同步淘汰仍然是 `O(N log k)`；超过 hard limit（硬阈值）时，写入线程可能承担这部分成本。
基线 `Capacity=1,000` 的新 key 测试中出现一个较慢样本，因此 `39.829 µs` 的均值比其他行更不稳定；将它用于阈值
判断时应改用更长的测试任务（job）。

## 读取路径

本次直接类型化命中基准的结果基本不变：

| 基准测试 | 基线 | 优化后 |
| --- | ---: | ---: |
| `CacheReadBenchmarks.TypedCacheHit` | 20.108 ns，0 B | 20.131 ns，0 B |

并发基准用于观察争用和监测（instrumentation）开销。一次包含 16,384 次操作的 `ShortRun` 得到以下均值；误差范围较宽，
重新评估前应重复运行：

| 访问模式 | Listener 状态 | 基线 | 优化后 |
| --- | --- | ---: | ---: |
| 不同 key | 未启用 | 2.887 ms | 1.593 ms |
| 不同 key | 已启用 | 4.020 ms | 3.171 ms |
| 热点 key | 未启用 | 4.832 ms | 6.321 ms |
| 热点 key | 已启用 | 6.233 ms | 7.955 ms |

本次实现主要优化了计数器写入争用，并保持直接命中路径零分配。热点 key 会让所有工作线程更新同一个 entry 的打包
频次状态，是后续调优时的重要基准。

## 结论

最稳定的收益体现在淘汰策略和内存行为上：延迟维护时频次衰减仍保持正确，热点保留率显著提高，候选队列也不再按
容量设置。读取基准会继续作为回归检查；调优频次状态争用或评估生产负载时，建议使用 `--job medium` 并在多个独立
进程中重复运行。
