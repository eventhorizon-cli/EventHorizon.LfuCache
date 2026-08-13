# EventHorizon.LfuCache

[English](README.md) | [简体中文](README.zh-CN.md)

`EventHorizon.LfuCache` 是进程内 LFU 缓存。它集成 Microsoft 依赖注入，按 keyspace 隔离数据，支持 entry
独立过期，并按比例批量淘汰冷数据。

## 特性

- 每个 keyspace 一个类型化 `ConcurrentDictionary`，且每个 keyspace 只允许一个 `(TKey, TValue)` 组合。
- 常规场景使用类型化 `ILfuCache<TKey, TValue>`，动态场景使用非泛型 `ILfuCache`。
- 支持 keyed service 注册；`default` keyspace 同时支持普通的非 keyed 注入。
- per-entry 相对过期时间、读取时失效和后台增量清理。
- 批量 LFU 淘汰，同频次时以 LRU 排序作为次级规则。
- 频次衰减、新 entry 保护，以及越过 overflow 水位后的同步反压。
- 同一 key 并发调用 `GetOrAdd` / `GetOrAddAsync` 时 factory 只执行一次。
- 通过 named options 进行配置对象整体热更新。
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

通过 keyed service 解析并使用：

```csharp
var cache = serviceProvider.GetRequiredKeyedService<ILfuCache<Guid, string>>("profiles");

cache.Set(profileId, profileName);

if (cache.TryGet(profileId, out var cachedName))
{
    // 已命中；当 TValue 允许 null 时，cachedName 也可以是 null。
}
```

注册 `default` keyspace 时，无需 service key 即可使用普通注入：

```csharp
services.AddLfuCache<Guid, string>();

public sealed class ProfileReader(ILfuCache<Guid, string> cache)
{
    // 在应用方法中使用 cache。
}
```

keyspace 会被 trim 并按大小写不敏感的规则归一化。同一个 keyspace 可以重复注册同一类型组合；注册不同的
`(TKey, TValue)` 组合会立即抛出异常。类型或配置不同时应使用另一个 keyspace。

## 过期时间

API 使用与 StackExchange.Redis 类似的相对过期时间术语：

```csharp
cache.Set(key, value);                          // 使用 DefaultExpiry。
cache.Set(key, value, TimeSpan.FromMinutes(2)); // entry 独立过期时间。
cache.Set(key, value, TimeSpan.Zero);           // 永不过期。
```

方法参数为 `null` 时使用 `DefaultExpiry`；显式传入 `TimeSpan.Zero` 时，该 entry 永不过期。负数会被拒绝。
`DefaultExpiry` 本身只能为 `null` 或正数，其中 `null` 表示默认永不过期。

## 配置绑定

```csharp
services.AddLfuCache<Guid, string>(
    "profiles",
    configuration.GetSection("LfuCache:profiles"));
```

```json
{
  "LfuCache": {
    "profiles": {
      "Capacity": 10000,
      "EvictionRatio": 0.1,
      "DefaultExpiry": "00:30:00",
      "MaintenanceInterval": "00:00:10",
      "DecayInterval": "00:05:00",
      "OverflowRatio": 0.05
    }
  }
}
```

配置 reload 会整体替换一份经过校验的不可变快照。运行期非法配置会被拒绝，并继续使用上一份有效快照。

## 动态接口

只有在编译期拿不到 key/value 类型时才使用 `ILfuCache`。它只转发到同一个类型化存储，不持有独立 entry：

```csharp
var cache = serviceProvider.GetRequiredKeyedService<ILfuCache>("profiles");
cache.Set<Guid, string>(profileId, profileName, TimeSpan.FromMinutes(5));
```

方法类型参数必须与该 keyspace 唯一注册的类型组合一致，否则会抛出 `InvalidOperationException`。

## Sample

运行通用 Host sample：

```bash
dotnet run --project samples/EventHorizon.LfuCache.Sample
```

它演示 keyed DI、entry 独立 expiry、`null` 值、带击穿保护的加载、统计，以及与类型化存储共享数据的动态接口。

## 构建与测试

仓库使用 .NET 10 SDK 构建包的 `net8.0` 和 `net10.0` 目标。`global.json` 会向更新的主版本 SDK
滚动，而不是把仓库固定到某个已安装的 patch 版本。

```bash
dotnet restore EventHorizon.LfuCache.slnx
dotnet format EventHorizon.LfuCache.slnx --verify-no-changes
dotnet build EventHorizon.LfuCache.slnx -c Release --no-restore
dotnet test EventHorizon.LfuCache.slnx -c Release --no-build
```

运行 benchmark：

```bash
dotnet run --project tests/benchmarks/EventHorizon.LfuCache.Benchmarks -c Release -- --filter '*'
```

benchmark 对比类型化接口、动态接口、`ConcurrentDictionary` 与 `MemoryCache`，并包含容量压力下的淘汰负载。

并发、淘汰、后台维护、配置和可观测性细节见[设计文档](docs/design.zh-CN.md)。

## 许可证

本项目使用 [MIT License](LICENSE)。
