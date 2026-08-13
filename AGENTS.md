# Repository Guidelines

## Scope

- This repository contains one reusable in-process LFU cache package targeting .NET 8 and .NET 10.
- Production code lives in `src/EventHorizon.LfuCache`.
- Unit tests live in `tests/ut/EventHorizon.LfuCache.Tests`.
- The generic runnable sample lives in `samples/EventHorizon.LfuCache.Sample`.
- Keep samples generic and do not add application-specific integrations to this repository.

## Public API

- The public cache contracts are `ILfuCache<TKey, TValue>` and `ILfuCache`.
- `LfuCacheOptions`, `LfuCacheStats`, and service-registration extensions are the only supporting public types.
- Keep storage, maintenance, registration, metrics, and validation implementations internal.
- Add XML documentation to every public API.
- Keep one top-level C# type per file.

## Design Constraints

- Each keyspace owns exactly one typed `ConcurrentDictionary`; a keyspace cannot register multiple type pairs.
- Capacity applies to the keyspace's single store.
- The non-generic cache is a forwarding facade and must never own cache entries.
- Normalize keyspaces at registration and lookup boundaries.
- Cache option changes replace one immutable snapshot; do not add field-level mutation APIs.
- Preserve null as a valid cached value and use reference-checked physical removal.
- Keep cache-hit reads free of locks.
- Use one hosted maintenance loop for every registered store.

## Style And Verification

- Use C# 12, nullable reference types, implicit usings, and file-scoped namespaces.
- Keep production projects multi-targeted to `net8.0;net10.0`; tests target `net10.0` and include a separate .NET 8
  build validation when CI is added.
- Prefer `var` when the assigned type is apparent.
- Test names use `MemberOrBehavior_Scenario_ExpectedOutcome`.
- Run `dotnet format EventHorizon.LfuCache.slnx`, `dotnet build EventHorizon.LfuCache.slnx`, and
  `dotnet test EventHorizon.LfuCache.slnx` before handing off changes.
