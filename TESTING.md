# Testing

The Manuals test suite uses xUnit v3 and is split into two tiers: **unit tests** that run on every push with no external dependencies, and **integration tests** that exercise real Azure Redis and Azure OpenAI on every push to `main`.

## Test tiers

| Tier | Trait | Project | Requires Azure? | Runs in CI |
|------|-------|---------|-----------------|------------|
| Unit | `Category=Unit` | `Manuals.Tests` | No | Every push/PR |
| Integration | `Category=Integration` | `Manuals.Tests` | Yes — real Redis + OpenAI | Push to `main` |

---

## Running tests locally

### Prerequisites

Unit tests require no Azure credentials. For integration tests, authenticate first and set the following environment variables:

```bash
az login   # Azure CLI — required for integration tests (real Redis + OpenAI via Key Vault)
```

| Variable | Description |
|----------|-------------|
| `ASPNETCORE_ENVIRONMENT` | Set to `CI` or `Development` |
| `KeyVaultUri` | Azure Key Vault URI |
| `RedisHost` | Redis hostname (e.g. `<name>.redis.cache.windows.net`) |
| `RedisPort` | Redis port — typically `6380` (SSL) |
| `OpenAIEndpoint` | Azure OpenAI endpoint URL |
| `OpenAIModel` | Deployed model name (e.g. `gpt-4o`) |
| `OpenAIMaxOutputTokenCount` | Max tokens per completion (e.g. `512` for integration tests, `4096` for production) |

### Unit tests

```bash
dotnet test --project Manuals.Tests --configuration Release \
  -- --filter-trait "Category=Unit"
```

### Integration tests (requires live Azure resources)

```bash
export ASPNETCORE_ENVIRONMENT=Development
export KeyVaultUri="https://<your-keyvault>.vault.azure.net/"
export RedisHost="<your-redis>.redis.cache.windows.net"
export RedisPort=6380
export OpenAIEndpoint="https://<your-resource>.openai.azure.com/"
export OpenAIModel="gpt-4o"
export OpenAIMaxOutputTokenCount=512

dotnet test --project Manuals.Tests --no-build --configuration Release \
  -- --filter-trait "Category=Integration"
```

> **Data isolation:** integration tests write to real Redis using the email `integration@test.invalid` and clean up all created keys in `IAsyncDisposable.DisposeAsync` — `user:integration@test.invalid:chats` and `chat:{chatId:N}:meta` / `chat:{chatId:N}:messages` for each created chat. Running multiple integration test runs concurrently against the same Redis instance is not supported.

### Run all tests

```bash
dotnet test Manuals.slnx --configuration Release
```

---

## Test infrastructure

### `ManualsWebApplicationFactory`

`WebApplicationFactory<Program>` used by integration tests. Starts the full application with production configuration (real Redis, real Azure OpenAI). The only replacement is the logger factory (plain console, no Elasticsearch sink). Authentication is replaced with a test scheme that always authenticates as `integration@test.invalid`.

### `IntegrationAuthHandler`

An `AuthenticationHandler` registered as the default scheme in `ManualsWebApplicationFactory`. Always succeeds and returns a principal with `email = integration@test.invalid`. This allows the integration tests to call the API without a real JWT.

### `IntegrationCollection` / `IntegrationChatsTests`

A single xUnit collection fixture (`ICollectionFixture<ManualsWebApplicationFactory>`) that wraps all integration tests. `IntegrationChatsTests` implements `IAsyncDisposable` to delete test data from Redis after each test class.

---

## Unit test coverage

### `Controllers/ChatsControllerTests.cs`

Tests `ChatsController` using a mocked `IChatsService`. Covers every action method for both success and error paths:

| Area | Tests |
|------|-------|
| `GetChatsAsync` | Returns 200 with chat list |
| `GetChatAsync` | Returns 200 on found; 404 on `KeyNotFoundException` |
| `GetChatMessagesAsync` | Returns 200 with message list; 404 on `KeyNotFoundException` |
| `PostChatAsync` | Returns 201 Created with Location header |
| `PatchChatAsync` | Returns 204 on success; 400 on blank title; 404 on `KeyNotFoundException` |
| `DeleteChatAsync` | Returns 204 on success; 404 on `KeyNotFoundException` |
| `PostMessageAsync` | Returns 200 with output; 400 on blank input; 404 on `KeyNotFoundException` |
| `PostMessageStreamAsync` | Returns SSE stream; 400 on blank input; 404 on `KeyNotFoundException` |

### `Services/RedisChatsServiceTests.cs`

Tests `RedisChatsService` using a mocked `IDatabase` (StackExchange.Redis). Covers the full Redis data model:

| Area | Tests |
|------|-------|
| `GetChatsAsync` | Sorted-set scan; returns chats ordered newest-first; skips invalid member entries |
| `GetChatAsync` | Hash field reads; throws `KeyNotFoundException` when chat is not owned by the user |
| `GetChatMessagesAsync` | List range read; deserializes message JSON; throws on ownership failure |
| `CreateChatAsync` | Generates a non-empty GUID chat ID; writes meta hash + sorted-set member |
| `UpdateChatTitleAsync` | Writes title field; throws on ownership failure |
| `DeleteChatAsync` | Deletes meta, messages, and sorted-set member; throws on ownership failure |
| `CompleteChatAsync` | Calls `ResponsesClient`; appends messages; sets auto-title on first message |
| `StreamChatAsync` | Yields string deltas; appends messages; sets auto-title on first message |

---

## Integration test coverage

### `E2E/IntegrationChatsTests.cs` — real Azure Redis + Azure OpenAI

| Test | What it verifies |
|------|-----------------|
| `RealOpenAICompletionResponds` | `POST /api/chats/{id}/messages` returns a response containing "4" for "What is 2+2?" |
| `RealOpenAIStreamingResponds` | `POST /api/chats/{id}/messages/stream` returns `text/event-stream` with at least one `data:` line and a `[DONE]` terminator |
| `ConversationHistoryIsPreserved` | Sending "My name is Alice" then "What is my name?" in the same chat returns a response containing "Alice", proving full history is passed to the model |

These tests use at most 3 real OpenAI completions per run. `OpenAIMaxOutputTokenCount` is set to 512 in CI to limit cost.

---

## CI pipeline

### Build job (every push / PR)

1. Build solution (`dotnet build --no-incremental --configuration Release`)
2. Unit tests with coverage (`dotnet-coverage collect … --filter-trait Category=Unit`)
3. Azure login (OIDC — same service principal as the deploy job)
4. Set environment variables (`KeyVaultUri`, `RedisHost`, `RedisPort`, `OpenAIEndpoint`, `OpenAIModel`, `OpenAIMaxOutputTokenCount`)
5. Integration tests with coverage (`dotnet-coverage collect … --filter-trait Category=Integration`)
6. Upload TRX artifacts (`Manuals.Tests/bin/Release/net10.0/TestResults/`)
8. Publish app + SonarCloud analysis (coverage from both unit and integration tests)

Integration tests run on every push to `main` and on `workflow_dispatch` — they are skipped on pull_request events to avoid hitting real OpenAI on unmerged changes.
