# Architecture

## Bird's-eye view

One ASP.NET Core app, `Manuals`, exposes chat sessions over REST. A chat's metadata and full message history live in
Redis; each new message is sent to Azure OpenAI's Responses API with the whole stored history, and the reply is
stored and returned whole or streamed as server-sent events. HybridCache sits in front of the two hot reads.

## Codemap

| Part | What it does |
|---|---|
| `Manuals/Program.cs` | configuration, the Redis connection and HybridCache, the `ResponsesClient`, JWT authentication and the `Manuals` policy, health checks, and the production-only telemetry and data protection |
| `Manuals/Controllers/ChatsController.cs` | every `/chats` route, including the SSE stream |
| `Manuals/Services/RedisChatsService.cs`, `IChatsService.cs` | chat storage, ownership checks, cache invalidation, the OpenAI calls, and every custom span |
| `Manuals/Models` | the request, response and stored-message types |
| `Manuals/HealthChecks/RedisHealthCheck.cs`, `Telemetry.cs` | `/health` and the `ActivitySource` |
| `Manuals/AuthorizationPolicies.cs`, `OpenAISettingKeys.cs`, `RedisSettingKeys.cs`, `Extensions` | policy and setting names, `GetRequired<T>` |
| `Manuals.Tests.Unit`, `Manuals.Tests.Integration` | the two test tiers ([TESTING.md](TESTING.md)) |
| `.github/workflows/main_crgolden-manuals.yml` | build, inspect, test, analyze and deploy |
| `NuGet.Config`, `packages.lock.json` files, `dotnet-tools.json`, `global.json`, `coverage.settings.xml` | package sources (the `Shared` package from GitHub Packages), locked restore, pinned tools, the SDK, integration coverage settings |
| `gate.ps1` | the maintainers' local gate (README.md, "Gate") |

## Invariants

- **Redis is the store and its keys never expire**: `user:{sub}:chats` (a sorted set by creation time),
  `chat:{id}:meta` (a hash) and `chat:{id}:messages` (a list). Every key shape is a member of `RedisChatsService`,
  and nothing else spells one.
- **Every per-chat read and write first checks the caller's own chat set**, so another user's chat is 404, never 403.
- **A new chat's hash holds only `createdAt`**; the title is absent until set. The first message sets it from the
  user's input (truncated with `…`) only while the stored title is missing or blank.
- **HybridCache keys live under `manuals:hc:`**, apart from the primary keys, and every write removes the affected
  cache entry, so a read never serves data older than the last write.
- **The stream action returns `void`**: it writes `data: {"delta":{"content":"..."}}` events and a final
  `data: [DONE]`, with response buffering disabled before the first write; a 400 or 404 there is a bare status.
- **Prose sent to the model is configuration**: `OpenAIInstructions` and the integration tier's prompts come from
  settings, never from code.

## Boundaries

- Redis and Azure OpenAI are the only services the API calls. In production it authenticates to Azure OpenAI with
  `DefaultAzureCredential`; elsewhere with `OpenAIApiKey`.
- The API trusts any token from `OidcAuthority` whose `scope` includes `manuals`. `/health` and `/openapi/v1.json`
  are anonymous in every environment.

## Cross-cutting concerns

- **The OpenAI output budget must fit the model**: a reasoning model spends `OpenAIMaxOutputTokenCount` on hidden
  reasoning before any visible output, and too small a budget surfaces as "OpenAI returned no output".
- **Telemetry** is production-only: OpenTelemetry traces and metrics over OTLP, with a span per chat operation and
  OpenAI call, and Serilog to an Elasticsearch data stream.
- **Warnings are errors**, and CI also fails on any `jb inspectcode` result and any open SonarCloud issue.
