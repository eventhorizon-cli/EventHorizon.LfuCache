# LFU Cache 组件设计

[English](design.md) | [简体中文](design.zh-CN.md)

## 1. 目标与边界

本组件提供进程内 LFU 缓存，通过 `AddLfuCache` 注册到依赖注入容器。

当前实现包含：

- keyed service 注入，service key 即 keyspace；未指定时使用 `default`。
- 每个 keyspace 一个独立 `ConcurrentDictionary<TKey, CacheEntry<TValue>>`。
- 类型化接口，以及采用方法泛型的非泛型接口；两者转发到同一个字典。
- per-entry expiry、读取时失效和后台增量清理。
- 按比例批量 LFU 淘汰，以 LRU 作为同频次时的次级顺序。
- 容量 overflow 水位和同步淘汰反压。
- 频次衰减和新 entry 保护。
- named options 整体热更新。
- 单一后台维护循环、统计、指标和结构化日志。
- 单 key `GetOrAdd` / `GetOrAddAsync` 击穿保护。

不包含分布式一致性、持久化、按字节计量或业务接入逻辑。仓库内的通用 Console sample 只演示组件 API。

## 2. keyspace 与类型约束

keyspace 同时是配置边界、DI 边界和存储边界。每个 keyspace 只能注册一个
`(TKey, TValue)` 组合，并只创建一个对应的 `ConcurrentDictionary<TKey, CacheEntry<TValue>>`。

```text
keyspace-a -> ConcurrentDictionary<TKeyA, CacheEntry<TValueA>>
keyspace-b -> ConcurrentDictionary<TKeyB, CacheEntry<TValueB>>
```

同一 keyspace 重复注册相同类型组合是幂等的；注册不同类型组合时立即抛出
`InvalidOperationException`。需要缓存另一组类型时必须使用另一个 keyspace。

该约束保证：

- `Capacity` 是整个 keyspace 的容量，不需要按类型组合累加。
- LFU 排序覆盖 keyspace 内的全部 entry，是精确 LFU。
- key 和 value 始终保留实际泛型类型，不使用 `object` 底座，不引入值类型装箱。
- 动态入口只能转发到 keyspace 已注册的类型组合，不能按调用惰性创建字典。

keyspace 统一归一化：

- `null`、空字符串和全空白字符串归一化为 `default`。
- 非空值执行 `Trim()` 后转换为小写不变量形式。
- 注册、keyed DI 和 registry 使用同一归一化结果。

## 3. 公共 API

对外只有两个缓存接口，以及 options、stats 和注册扩展方法。存储、维护、注册索引、校验和指标实现均为
`internal`。

### 3.1 类型化接口

```csharp
public interface ILfuCache<TKey, TValue> where TKey : notnull
{
    string Keyspace { get; }
    int Count { get; }

    bool TryGet(TKey key, out TValue value);
    void Set(TKey key, TValue value, TimeSpan? expiry = null);

    TValue GetOrAdd(
        TKey key,
        Func<TKey, TValue> factory,
        TimeSpan? expiry = null);

    ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken ct = default);

    bool Remove(TKey key);
    void Clear();
    LfuCacheStats GetStats();
}
```

类型化接口是默认入口。

### 3.2 动态接口

```csharp
public interface ILfuCache
{
    string Keyspace { get; }

    bool TryGet<TKey, TValue>(TKey key, out TValue value) where TKey : notnull;

    void Set<TKey, TValue>(TKey key, TValue value, TimeSpan? expiry = null)
        where TKey : notnull;

    TValue GetOrAdd<TKey, TValue>(
        TKey key,
        Func<TKey, TValue> factory,
        TimeSpan? expiry = null)
        where TKey : notnull;

    ValueTask<TValue> GetOrAddAsync<TKey, TValue>(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken ct = default)
        where TKey : notnull;

    bool Remove<TKey, TValue>(TKey key) where TKey : notnull;
    void Clear();
    LfuCacheStats GetStats();
}
```

`DynamicLfuCache` 不保存 entry。类型化方法先按 keyspace 从 registry 读取唯一 cache handle，验证
`TKey` 和 `TValue`，然后转发到 `ILfuCache<TKey, TValue>`。单次调用只增加一次字典查找和一次接口转换，
不执行 DI 解析，也不装箱 key 或 value。

若调用类型与 keyspace 注册类型不一致，动态入口抛出 `InvalidOperationException`，异常消息包含 keyspace、
实际类型和正确的 `AddLfuCache<TKey, TValue>` 注册方式。

`Clear()` 和 `GetStats()` 直接操作 keyspace 的唯一存储，不做跨类型聚合。

### 3.3 stats

```csharp
public readonly record struct LfuCacheStats(
    long Hits,
    long Misses,
    long Evictions,
    long Expirations,
    long EvictionBatches,
    int Count,
    int Capacity);
```

`Count` 表示物理 entry 数，可能短暂包含尚未被后台扫描回收的过期 entry。

### 3.4 `null` value

引用类型的 `null` 是合法缓存值。调用方必须使用 `TryGet` 的布尔返回值区分命中 `null` 与 miss；
内部不得以 `value is null` 判断 entry 是否存在。

缓存保存对象引用，不做防御性复制。调用方应将缓存值视为不可变对象。

## 4. 配置

每个 keyspace 对应一份 named `LfuCacheOptions`：

```csharp
public sealed class LfuCacheOptions
{
    public int Capacity { get; set; } = 10_000;
    public double EvictionRatio { get; set; } = 0.1;
    public TimeSpan? DefaultExpiry { get; set; }
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan DecayInterval { get; set; } = TimeSpan.FromMinutes(1);
    public double OverflowRatio { get; set; } = 0.05;
}
```

| 参数 | 含义 |
| --- | --- |
| `Capacity` | keyspace 的 entry 容量 |
| `EvictionRatio` | 一批淘汰占容量的比例 |
| `DefaultExpiry` | 未传 entry expiry 时使用的相对过期时间；`null` 表示默认不过期 |
| `MaintenanceInterval` | 后台增量过期扫描周期 |
| `DecayInterval` | 访问频次半衰期 |
| `OverflowRatio` | 后台淘汰期间允许瞬时超出容量的比例 |

### 4.1 校验

- `Capacity >= 1`
- `0 < EvictionRatio <= 0.5`
- `0 <= OverflowRatio <= 0.5`
- `DefaultExpiry` 未设置或大于零
- `1s <= MaintenanceInterval <= 1h`
- `1s <= DecayInterval <= 24h`

维护服务在宿主启动时 eager 解析每个 cache；cache 构造阶段校验完整 named options，非法配置使宿主启动失败。
运行期非法配置不替换当前快照，并记录 Warning。

### 4.2 整体热更新

内部将 options 转换为不可变 `OptionsSnapshot`，并提前计算 target、hard limit、扫描预算和时间戳间隔。
运行时通过一个 volatile 引用整体替换快照；每次操作只读取一次快照。

`OnChange` 应先逐字段比较。值未改变时不替换快照、不唤醒维护循环，也不写配置变更日志。

| 变化 | 行为 |
| --- | --- |
| `Capacity` 增大 | 更新水位，无额外动作 |
| `Capacity` 减小 | 唤醒维护循环，按 `EvictionRatio` 分批收缩 |
| `EvictionRatio` / `OverflowRatio` | 后续淘汰使用新水位 |
| `DefaultExpiry` | 只影响后续写入，不修改已有 entry |
| `MaintenanceInterval` / `DecayInterval` | 从变更时刻重算下一次执行时间并唤醒维护循环 |

单次扫描预算按 `Capacity`、`DefaultExpiry` 和 `MaintenanceInterval` 推导，目标是在
`min(DefaultExpiry, 1min)` 内完成一轮全扫；未配置 expiry 时使用 1 分钟窗口。

## 5. DI 与生命周期

注册扩展提供：

```csharp
services.AddLfuCache<TKey, TValue>();
services.AddLfuCache<TKey, TValue>(keyspace);
services.AddLfuCache<TKey, TValue>(keyspace, configureOptions);
services.AddLfuCache<TKey, TValue>(keyspace, configurationSection);
```

每次注册执行以下步骤：

1. 幂等注册 catalog、registry、metrics、`TimeProvider.System` 和维护服务。
2. 校验该 keyspace 尚未绑定其他类型组合。
3. 以归一化 keyspace 作为 options name 注册配置。
4. 注册闭合的 keyed singleton `ILfuCache<TKey, TValue>`。
5. 为 keyspace 幂等注册一个 keyed singleton `ILfuCache`。
6. keyspace 为 `default` 时额外注册普通 singleton；普通服务转发到 keyed 服务，二者是同一实例。

不注册 keyed open generic。

keyed singleton 默认延迟创建。`LfuCacheMaintenanceService.StartAsync` 遍历 catalog 并主动解析全部 typed cache，
使 registry 在宿主启动完成前包含每个 keyspace 的唯一存储，配置错误也在启动阶段暴露。

## 6. 数据结构与并发

每个 keyspace 的存储结构如下：

```text
LfuCache<TKey, TValue>
├── ConcurrentDictionary<TKey, CacheEntry<TValue>> entries
├── long count
├── int evictionGate
├── expiration scan cursor
└── decay scan cursor
```

`count` 使用 `Interlocked` 维护，热路径不读取 `ConcurrentDictionary.Count`。
`evictionGate` 使用 CAS，保证同一 keyspace 同时只有一个淘汰执行者；竞争失败的触发者直接返回。

### 6.1 entry

已完成 entry 保存：

```csharp
internal sealed class CacheEntry<TValue>
{
    public TValue Value;
    public long Frequency;
    public long LastAccessTicks;
    public long CreatedTicks;
    public long ExpiresAtTicks;
}
```

`Frequency` 初始为 1，命中时原子递增。`LastAccessTicks` 用作同频次时的 LRU 次级顺序，
`CreatedTicks` 用于新 entry 保护，`long.MaxValue` 表示永不过期。

击穿保护状态也保存在同一个主字典中，不创建第二个 entry 字典。`TryGet` 不把未完成 factory 当作命中；
并发 `GetOrAdd` 调用则共享该 entry 内的单次执行状态。

### 6.2 引用比对删除

过期、淘汰和 factory 失败路径必须比较 entry 引用：

```csharp
((ICollection<KeyValuePair<TKey, CacheEntry<TValue>>>)entries)
    .Remove(new(key, observedEntry));
```

不能在这些路径上仅按 key 删除，否则旧读取可能删除同一 key 后来写入的新 entry。

### 6.3 时间源

expiry、维护周期、衰减周期和保护窗口统一使用注入的 `TimeProvider`。
内部使用 `GetTimestamp()`，并按 `TimestampFrequency` 换算 duration，不能把单调时间戳当作
`DateTime.UtcNow.Ticks`。

## 7. 读写路径

### 7.1 `TryGet`

1. `TryGetValue`；不存在或 entry 尚未完成时记录 miss。
2. 若 `now >= ExpiresAtTicks`，引用比对删除，成功时记录 expiration，并按 miss 返回。
3. 原子增加 `Frequency`，更新 `LastAccessTicks`。
4. 记录 hit，返回 `Value`；该值可以为 `null`。

命中路径不获取显式锁。

### 7.2 `Set`

1. 显式 expiry 优先，否则读取当前快照的 `DefaultExpiry`；参数为 `null` 时使用默认值，显式 `TimeSpan.Zero`
   表示该 entry 永不过期，负值被拒绝。
2. 使用 CAS 循环新增或替换 entry。
3. 替换时继承旧 entry 的频次；新增时增加 count。
4. count 超过 capacity 时唤醒后台维护；超过 hard limit 时写入线程尝试同步淘汰。

### 7.3 `GetOrAdd`

同一个 key 的并发调用只执行一次 factory。factory 成功后将原 entry 原子发布为已完成状态；
失败或取消时引用比对移除 entry，使后续调用可以重试。

普通 `Set` 可以替换正在执行 factory 的 entry。此时 factory 的调用者仍收到自己的结果，但 factory 完成后不能覆盖
`Set` 写入的新 entry。

### 7.4 `Remove` 与 `Clear`

`Remove` 删除调用时观察到的 entry并减少 count。`Clear` 原子替换内部 store state，避免
`dictionary.Clear()` 与并发新增造成 count 漂移；已获取旧 state 的操作不会影响新的字典。

## 8. 过期与维护循环

过期 entry 有三条回收路径：读取时失效、后台增量扫描、淘汰扫描顺带回收。
读取时检查保证不会返回过期值；后台扫描只负责容量回收。

组件只有一个 `LfuCacheMaintenanceService`。每个 cache handle 暴露 `NextDueTicks` 和
`RunMaintenance(nowTicks)`。维护循环：

1. 读取 registry 中所有 keyspace 的最小 `NextDueTicks`。
2. 使用 `TimeProvider` 等待到该时刻，或被水位和配置变更信号提前唤醒。
3. 只运行已经到期或待淘汰的 keyspace。
4. 每个实例独立判断是否执行过期扫描、衰减或一个淘汰批次。

过期和衰减各自保存弱一致枚举游标，每次按预算推进，下次从上一位置继续。所有物理删除仍使用引用比对。

## 9. 批量淘汰

keyspace 使用三条水位：

| 水位 | 计算 | 行为 |
| --- | --- | --- |
| soft limit | `Capacity` | count 超过后通知后台维护 |
| target | `Capacity - ceil(Capacity * EvictionRatio)` | 单批淘汰后的目标水位 |
| hard limit | `floor(Capacity * (1 + OverflowRatio))` | count 超过后写入线程尝试同步淘汰 |

淘汰流程：

1. 全量枚举唯一字典，先引用比对删除过期 entry。
2. 根据当前 count 计算降到 target 还需删除的数量 `k`。
3. 使用容量为 `k` 的大顶堆选择 `(Frequency, LastAccessTicks)` 最小的候选，避免全量排序。
4. 引用比对删除候选，更新 eviction 和 batch 统计。

创建不足 1 秒的 entry 第一轮不参与候选选择；若其他候选不足，再纳入这些 entry，保证淘汰可以推进。

每过一个 `DecayInterval`，增量扫描将频次右移一位，下限为 1。

| 操作 | 复杂度 |
| --- | --- |
| 类型化 `TryGet` | 平均 `O(1)` |
| 动态 `TryGet` | 平均 `O(1)`，增加一次 registry 查找 |
| `Set` | 平均 `O(1)`；越过 hard limit 时可能同步淘汰 |
| 淘汰批次 | `O(N log k)` |
| 维护扫描 | `O(scan budget)` |

## 10. 可观测性

使用 `System.Diagnostics.Metrics` 发布：

- Counter：`lfu_cache.hits`、`lfu_cache.misses`、`lfu_cache.evictions`、
  `lfu_cache.expirations`、`lfu_cache.eviction.batches`、`lfu_cache.eviction.synchronous`。
- ObservableGauge：`lfu_cache.entries`、`lfu_cache.capacity`。
- Histogram：`lfu_cache.eviction.duration`。

指标带 `keyspace` 和 `value_type` tag。

日志级别：

- Information：配置整体替换和淘汰批次摘要。
- Warning：运行期非法配置被拒绝和同步淘汰。
- Debug：维护唤醒与各 keyspace 的清理、衰减结果。

## 11. 验收

核心行为：

- LFU 顺序正确；同频次时使用 LRU 次级顺序。
- `null` 命中与 miss 可区分。
- 显式 expiry 覆盖默认 expiry；读时和后台过期均正确。
- 替换值保留已有频次。
- 越过 capacity 后一次淘汰到 target，连续写入 target 与 capacity 之间不触发新批次。
- hard limit 触发同步淘汰。
- 新 entry 保护和候选不足时的退化路径均能推进。
- 频次按周期折半且下限为 1。
- 配置整体热更新生效；非法配置保留旧快照。
- 引用比对删除不会删除后来写入的新值。
- 并发 `GetOrAdd` factory 单次执行，失败后可以重试。

DI 与动态入口：

- `default` 的 keyed 与普通解析返回同一 typed cache 和同一 dynamic cache。
- keyspace 大小写与首尾空白归一化。
- 同一 keyspace 重复注册相同类型组合幂等，注册不同组合立即失败。
- 动态入口写入的值可从类型化接口读取，证明两者共享唯一字典。
- 动态调用使用错误类型时抛出明确异常。
- 宿主启动后，尚未被业务解析的 cache 也已进入 registry。

并发验收：

- 混合 get、set、remove 和维护操作无死锁或未处理异常。
- 操作结束并完成维护后，内部 count 与唯一字典的实际 entry 数一致。
- count 越过 hard limit 后能被同步或后台淘汰拉回目标水位。
