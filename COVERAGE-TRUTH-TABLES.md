# Manuals — Coverage Truth Tables

Demand-driven MC/DC tables for the `Manuals` methods the coverage baseline flags. Vocabulary, the three
laws, and the status/home legend live in the workspace `DESIGN-LANGUAGE.md`; the derivation rules
(MC/DC, `tests = 1 + Σ(cases − 1)`, lossless rows, pyramid escalation, 🔧-seam vs ⬆️-escalate) live in
`TESTING-COVERAGE.md`.

Baseline (2026-06-12, unit only, generated code excluded): **68.9% line / 48.8% branch / 64.0%
blended, 38 tests.** Already healthy; the single concentrated gap is `RedisChatsService` (70 branch
points, 40 uncovered). No other class is flagged.

---

## RedisChatsService — 40 uncovered branches → ~22 tests

`public sealed : IChatsService`. Deps `ResponsesClient` (OpenAI), `IDatabase` (StackExchange.Redis),
`HybridCache` are all mockable, so **no accessibility seam is needed** — the public interface methods
reach every private helper.

**🔧 Testing note (not a production change):** the branch logic of `GetChatsAsync` and
`GetChatMessagesInternalAsync` lives *inside* the `HybridCache.GetOrCreateAsync(key, factory, ...)`
factory delegates. `HybridCache` is abstract; the Moq setup must **invoke the factory**
(`.Returns(async (string k, Func<...> f, ...) => await f(...))`) or the `foreach` / `TryParse` /
`is not null` branches never execute and the rows below are unreachable in the harness. This is a
fixture obligation, not a seam.

### Read paths

| # | Method | Condition | Expected | Branch | Status |
|---|---|---|---|---|---|
| 1 | `GetChatsAsync` | empty sorted set | `[]` | `foreach` 0 iter | ❌ |
| 2 | `GetChatsAsync` | member valid guid, title + createdAt present | `Chat` populated | `TryParse` ok, title non-empty, createdAt parsed | ❌ |
| 3 | `GetChatsAsync` | member not a guid | skipped | `!Guid.TryParse` true (`continue`) | ❌ |
| 4 | `GetChatsAsync` | member valid, title empty, createdAt unparseable | `Chat` with `null` title, `0` | `IsNullOrEmpty(title)` true, `long.TryParse` false | ❌ |
| 5 | `GetChatAsync` | not owned | `KeyNotFoundException` | `VerifyOwnership` throw | ❌ |
| 6 | `GetChatAsync` | owned, title + createdAt | `Chat` populated | title non-empty, createdAt parsed | ❌ |
| 7 | `GetChatAsync` | owned, empty title / bad createdAt | `null` title, `0` | `IsNullOrEmpty` / `TryParse` false | ❌ |
| 8 | `GetChatMessagesAsync` | not owned | `KeyNotFoundException` | `VerifyOwnership` throw | ❌ |
| 9 | `GetChatMessagesAsync` | owned, items deserialize | message list | `msg is not null` true | ❌ |
| 10 | `GetChatMessagesAsync` | owned, an item deserializes `null` | that item skipped | `msg is not null` false | ❌ |

### Write paths

| # | Method | Condition | Expected | Branch | Status |
|---|---|---|---|---|---|
| 11 | `CreateChatAsync` | always | `HashSet` + `SortedSetAdd` + cache remove, returns `Chat(null title)` | straight-line | ❌ |
| 12 | `UpdateChatTitleAsync` | not owned | `KeyNotFoundException` | `VerifyOwnership` throw | ❌ |
| 13 | `UpdateChatTitleAsync` | owned | `HashSet` title + `RemoveAsync` | happy | ❌ |
| 14 | `DeleteChatAsync` | `score is null` | `KeyNotFoundException` | not found | ❌ |
| 15 | `DeleteChatAsync` | score present | `SortedSetRemove` + `KeyDelete` + 2 cache removes | happy | ❌ |

### OpenAI paths

| # | Method | Condition | Expected | Branch | Status |
|---|---|---|---|---|---|
| 16 | `CompleteChatAsync` | blank `inputText` | `ArgumentNullException` | `IsNullOrWhiteSpace` true | ❌ |
| 17 | `CompleteChatAsync` | not owned | `KeyNotFoundException` | `VerifyOwnership` throw | ❌ |
| 18 | `CompleteChatAsync` | `response?.Value?.GetOutputText()` null | `InvalidOperationException` | `?? throw` | ❌ |
| 19 | `CompleteChatAsync` | happy, history has both user + assistant msgs | stores, re-scores, auto-titles, returns `(chatId, output)`; covers `BuildInputItems` both role sides | happy | ❌ |
| 20 | `StreamChatAsync` | blank `inputText` | `ArgumentNullException` | `IsNullOrWhiteSpace` true | ❌ |
| 21 | `StreamChatAsyncCore` | stream yields deltas | yields each, `finally` stores + auto-titles | `!IsNullOrEmpty(assistantText)` true | ❌ |
| 22 | `StreamChatAsyncCore` | stream yields nothing | `finally` skips persist | `!IsNullOrEmpty` false | ❌ |

### Folded sub-branches

| Helper | Decision | Covered by |
|---|---|---|
| `VerifyOwnershipAsync` | `score is null` throw / ok | rows 5/8/12/17 (throw) + any happy (ok) |
| `BuildInputItems` | `msg.Role == "user" ? : assistant` | row 19 (mixed-role history) |
| `SetAutoTitleIfNeededAsync` | `!existing.HasValue \|\| IsNullOrEmpty` (3 cases); `Length <= 60 ? : truncate` | rows 19/21 + one ≤60 and one >60 input |

`SetAutoTitleIfNeededAsync` warrants 2 dedicated rows for the title-truncation ternary (input ≤ 60 vs
> 60 chars → `…` suffix) and the "existing title already set → skip" case, bringing the total to ~22.

### RedisChatsService total

**40 uncovered branches → ~22 tests.** No seam, no escalation. The collapse is milder than the
Functions workers (fewer defensive-null coalesces, more genuine small guards), but every branch is
unit-reachable once the `HybridCache` factory-invoking mock is in place.

---

## Manuals roll-up

| Class | Uncovered branches | Tests | Seam |
|---|---|---|---|
| RedisChatsService | 40 | 22 | none (fixture: `HybridCache` factory mock) |
| **Total** | **40** | **22** | — |

Everything else in Manuals is already at or above the line; this one class closes the branch gap.
