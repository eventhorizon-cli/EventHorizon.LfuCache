# EventHorizon.LfuCache

[![NuGet](https://img.shields.io/nuget/v/EventHorizon.LfuCache.svg)](https://www.nuget.org/packages/EventHorizon.LfuCache)
[![Build](https://github.com/eventhorizon-cli/EventHorizon.LfuCache/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/eventhorizon-cli/EventHorizon.LfuCache/actions/workflows/dotnet-build.yml)
[![Codecov](https://codecov.io/gh/eventhorizon-cli/EventHorizon.LfuCache/graph/badge.svg)](https://codecov.io/gh/eventhorizon-cli/EventHorizon.LfuCache)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md) | [简体中文](README.zh-CN.md)

`EventHorizon.LfuCache` 是一个进程内 LFU 缓存。它集成 Microsoft 的依赖注入机制，按键空间（keyspace）隔离数据，
支持为每个缓存项（entry）单独设置过期时间，并按比例批量淘汰冷数据。

## 特性

- 每个 keyspace 拥有一个类型化 `ConcurrentDictionary`，只允许注册一种 `(TKey, TValue)` 类型组合。
- 常规场景使用类型化 `ILfuCache<TKey, TValue>`，动态场景使用非泛型 `ILfuCache`。
- 支持键控服务（keyed service）注册；`default` keyspace 同时支持不指定服务键的普通注入。
- 支持为缓存项单独设置相对过期时间，在读取时检查过期，并在后台增量清理。
- 批量 LFU 淘汰依次按访问频次、新缓存项的 1 秒保护状态、LRU 排序。
- 访问频次随时间衰减，在读取和淘汰时按需更新，后台维护也会分批更新。
- 同一 key 并发调用 `GetOrAdd` / `GetOrAddAsync` 时，工厂函数（factory）只执行一次。
- 每个 keyspace 可限制正在运行的工厂函数数量，同一 key 的调用共享同一次执行。
- 通过命名选项（named options）整体热更新配置。
- 内置统计、指标和结构化日志。
- 引用类型的 `null` 是合法缓存值。

## 安装

```bash
dotnet add package EventHorizon.LfuCache
```

## 注册

在指定 keyspace 中注册类型化缓存：

```csharp
services.AddLfuCache<Guid, string>("profiles", options =>
{
    options.Capacity = 10_000;
    options.EvictionRatio = 0.1;
    options.DefaultExpiry = TimeSpan.FromMinutes(30);
    options.DecayInterval = TimeSpan.FromMinutes(5);
});
```

通过键控服务解析并使用缓存：

```csharp
var cache = serviceProvider.GetRequiredKeyedService<ILfuCache<Guid, string>>("profiles");

cache.Set(profileId, profileName);

if (cache.TryGet(profileId, out var cachedName))
{
    // 已命中；当 TValue 允许 null 时，cachedName 也可以是 null。
}
```

注册 `default` keyspace 时，无需指定服务键即可使用普通注入：

```csharp
services.AddLfuCache<Guid, string>();

public sealed class ProfileReader(ILfuCache<Guid, string> cache)
{
    // 在应用方法中使用缓存。
}
```

keyspace 名称会去除首尾空白，再通过 `ToLowerInvariant()` 转换为小写。同一个 keyspace 可以重复注册同一类型组合；
注册不同的 `(TKey, TValue)` 组合会立即抛出异常。需要使用不同的类型或独立配置时，应使用另一个 keyspace。

## 过期时间

API 采用相对过期时间，参数含义与 StackExchange.Redis 类似：

```csharp
cache.Set(key, value);                          // 使用 DefaultExpiry。
cache.Set(key, value, TimeSpan.FromMinutes(2)); // 该缓存项单独设置过期时间。
cache.Set(key, value, TimeSpan.Zero);           // 永不过期。
```

`expiry` 参数为 `null` 时使用 `DefaultExpiry`；显式传入 `TimeSpan.Zero` 时，该缓存项永不过期。负值会被拒绝。
`DefaultExpiry` 本身只能为 `null` 或大于零的时长，其中 `null` 表示默认永不过期。

## 配置

```csharp
services.AddLfuCache<Guid, string>(
    "profiles",
    options =>
    {
        options.Capacity = 10_000;
        options.DefaultExpiry = TimeSpan.FromMinutes(30);
        options.MaintenanceInterval = TimeSpan.FromSeconds(10);
        options.MaxInflight = 2_000;
    });
```

通常在注册 keyspace 时直接配置选项。外部选项源发生变更后，缓存会先校验新配置，再用新的不可变快照整体替换旧快照。
如果新配置不合法，缓存会继续使用上一份有效快照。`MaxInflight` 默认为 `null`，表示使用 `Capacity` 作为上限；
也可以单独设置为一个正数。

`GetOrAdd` 和 `GetOrAddAsync` 只有在为新 key 启动工厂函数时才占用并发名额。同一 key 的并发调用共享已有的工厂函数执行。
当 keyspace 中运行的工厂函数数量达到 `MaxInflight` 时，针对新 key 的调用会抛出 `InvalidOperationException`；
缓存命中和 `Set` 不占用名额。即使等待者取消等待，或调用了 `Remove`、`Clear`，名额也要等工厂函数退出后才会释放。
降低上限只影响后续针对新 key 的调用，已运行的工厂函数可以继续完成。

`Capacity` 按缓存项数量设置淘汰阈值，不按字节计量，也不是绝对的内存上限。尚未完成的工厂函数或并发操作可能
使实际存储的缓存项数量暂时超过容量。

## 动态接口

只有在编译期无法确定 key/value 类型时才使用 `ILfuCache`。它将操作转发到同一份类型化存储，自身不保存缓存项：

```csharp
var cache = serviceProvider.GetRequiredKeyedService<ILfuCache>("profiles");
cache.Set<Guid, string>(profileId, profileName, TimeSpan.FromMinutes(5));
```

方法的类型参数必须与该 keyspace 注册的类型组合一致，否则会抛出 `InvalidOperationException`。

## OpenTelemetry

调用 `AddLfuCacheInstrumentation`，将名为 `EventHorizon.LfuCache` 的 Meter 接入 OpenTelemetry 指标管道：

```csharp
using EventHorizon.LfuCache;

services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddLfuCacheInstrumentation());
```

指标导出器（Exporter）由宿主应用单独配置。缓存使用 Counter、ObservableGauge 和 Histogram 分别记录计数、
当前状态和淘汰耗时，指标均带有 `keyspace` 和 `value_type` 标签。

## 示例

运行 Web API 示例：

```bash
dotnet run --project samples/EventHorizon.LfuCache.Sample -- --urls http://localhost:5057
```

示例通过 `[FromKeyedServices("sample")]` 将类型化缓存直接注入 Minimal API 处理程序。可用以下命令写入和读取：

```bash
curl -X PUT http://localhost:5057/cache/example \
  -H 'Content-Type: application/json' \
  -d '{"value":"cached"}'
curl http://localhost:5057/cache/example
```

## 构建与测试

仓库使用 .NET 10 SDK 构建面向 `net8.0` 和 `net10.0` 两个目标框架的包。`global.json` 允许选用更新主版本的 SDK，
不限定某个已安装的补丁版本。

```bash
dotnet restore EventHorizon.LfuCache.slnx
dotnet format EventHorizon.LfuCache.slnx --verify-no-changes
dotnet build EventHorizon.LfuCache.slnx -c Release --no-restore
dotnet test EventHorizon.LfuCache.slnx -c Release --no-build
```

基准测试覆盖类型化接口和动态接口，与 `ConcurrentDictionary`、`MemoryCache` 的对比，以及容量压力下的淘汰与写入、
多线程负载和确定性策略场景。运行方式和优化前后的结果见 [基准测试方法与对比报告](docs/benchmarks.zh-CN.md)。

并发、淘汰、后台维护、配置和可观测性细节见 [设计文档](docs/design.zh-CN.md)。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
