# Testing

The Manuals test suite uses xUnit v3 and is split into two tiers: **unit tests** that run on every push with no external dependencies, and **integration tests** that exercise real Azure Redis and Azure OpenAI on every push to `main`.

For the `.NET 10 SDK xUnit caveat` (why `dotnet test` doesn't work) and `ASPNETCORE_ENVIRONMENT` discipline, see the workspace-level [TESTING.md](../TESTING.md).

## Test Tiers

| Tier | Trait | Requires Azure? | Runs in CI |
|------|-------|-----------------|------------|
| Unit | `Category=Unit` | No | Every push/PR |
| Integration | `Category=Integration` | Yes — real Redis + Azure OpenAI (API key from User Secrets; no Azure credentials) | Push to `main` only |

## Running Tests Locally

### Unit Tests

```powershell
dotnet build Manuals.Tests --configuration Debug
.\Manuals.Tests\bin\Debug\net10.0\Manuals.Tests.exe --filter-trait "Category=Unit" --show-live-output on
```

### Integration Tests

Requires a running Redis instance and an Azure OpenAI endpoint. No `az login` needed — in non-production, `Program.cs` uses `ApiKeyCredential` (from `OpenAIApiKey` User Secret) and User Secrets for Redis. Azure credentials (`DefaultAzureCredential`) are only constructed inside `IsProduction()`.

1. Set `ASPNETCORE_ENVIRONMENT=Development` so the non-production branch of `Program.cs` runs and User Secrets load.
2. Ensure User Secrets include: `RedisHost`, `RedisPort`, `RedisSsl`, `RedisPassword`, `OpenAIEndpoint`, `OpenAIModel`, `OpenAIInstructions`, `OpenAIMaxOutputTokenCount`, `OpenAIApiKey`, `OidcAuthority`.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet build Manuals.Tests --configuration Debug
.\Manuals.Tests\bin\Debug\net10.0\Manuals.Tests.exe --filter-trait "Category=Integration" --show-live-output on

# Redirect output for in-flight inspection
cmd /c "Manuals.Tests\bin\Debug\net10.0\Manuals.Tests.exe --filter-trait ""Category=Integration"" --show-live-output on > C:\temp\manuals-integration.txt 2>&1"
```

## Test Infrastructure

### `ManualsWebApplicationFactory`

`WebApplicationFactory<Program>` used by integration tests. Starts the full `Program.cs` with `ASPNETCORE_ENVIRONMENT=Development`, which selects the non-production branch: `ApiKeyCredential` for OpenAI, User Secrets for Redis, ephemeral Data Protection, no Azure credentials. The only replacement is the authentication scheme — `IntegrationAuthHandler` always authenticates as `sub = ManualsWebApplicationFactory.TestUserId` (`"integration-user-id"`), bypassing JWT validation.

### Data Isolation

Integration tests write to real Redis using the key prefix `user:integration-user-id:chats` and clean up in `IAsyncDisposable.DisposeAsync` (deletes all `chat:{chatId:N}:meta` and `chat:{chatId:N}:messages` keys created during the test). Concurrent runs against the same Redis instance are not supported.

`DisposeAsync` must use `TestUserId` — never a hardcoded email string. If `TestUserId` ever migrates, grep all of `Manuals.Tests/` before declaring complete (prior `email→sub` migration left stale cleanup code that leaked state across runs).

### `IntegrationCollection` / `IntegrationChatsTests`

A single xUnit collection fixture (`ICollectionFixture<ManualsWebApplicationFactory>`) wrapping all integration tests. `parallelizeTestCollections: false` is set in `xunit.runner.json`.
