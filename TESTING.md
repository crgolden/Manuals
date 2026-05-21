# Testing

The Manuals test suite uses xUnit v3 and is split into two tiers: **unit tests** that run on every push with no external dependencies, and **integration tests** that exercise real Azure Redis and Azure OpenAI on every push to `main`.

For the `.NET 10 SDK xUnit caveat` (why `dotnet test` doesn't work) and `ASPNETCORE_ENVIRONMENT` discipline, see the workspace-level [TESTING.md](../TESTING.md).

Unit test coding standards (MockBehavior.Strict, argument verification, SetupSequence, no control-flow in tests, etc.) are in the workspace-level [Unit Test Standards](../TESTING.md#unit-test-standards).

## Test Tiers

| Tier | Trait | Requires Azure? | Runs in CI |
|------|-------|-----------------|------------|
| Unit | `Category=Unit` | No | Every push/PR |
| Integration | `Category=Integration` | Yes — real Redis + Azure OpenAI (API key from User Secrets; no Azure credentials) | Push to `main` only |

## Running Tests Locally

### Unit Tests

```powershell
dotnet build Manuals.Tests --configuration Debug
.\Manuals.Tests\bin\Debug\net10.0\Manuals.Tests.exe -trait "Category=Unit" -showLiveOutput
```

### Integration Tests

Requires a running Redis instance and an Azure OpenAI endpoint. No `az login` needed — in non-production, `Program.cs` uses `ApiKeyCredential` (from `OpenAIApiKey` User Secret) and User Secrets for Redis. Azure credentials (`DefaultAzureCredential`) are only constructed inside `IsProduction()`.

1. Set `ASPNETCORE_ENVIRONMENT=Development` so the non-production branch of `Program.cs` runs and User Secrets load.
2. Ensure User Secrets include: `RedisHost`, `RedisPort`, `RedisSsl`, `RedisPassword`, `OpenAIEndpoint`, `OpenAIModel`, `OpenAIInstructions`, `OpenAIMaxOutputTokenCount`, `OpenAIApiKey`, `OidcAuthority`.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet build Manuals.Tests --configuration Debug
.\Manuals.Tests\bin\Debug\net10.0\Manuals.Tests.exe -trait "Category=Integration" -showLiveOutput

# Redirect output for in-flight inspection
cmd /c "Manuals.Tests\bin\Debug\net10.0\Manuals.Tests.exe -trait ""Category=Integration"" -showLiveOutput > C:\temp\manuals-integration.txt 2>&1"
```

## Test Infrastructure

### `ManualsWebApplicationFactory`

`WebApplicationFactory<Program>` used by integration tests. Starts the full `Program.cs` with `ASPNETCORE_ENVIRONMENT=Development`, which selects the non-production branch: `ApiKeyCredential` for OpenAI, User Secrets for Redis, ephemeral Data Protection, no Azure credentials. The only replacement is the authentication scheme — `IntegrationAuthHandler` always authenticates as `sub = ManualsWebApplicationFactory.TestUserId` (`"integration-user-id"`), bypassing JWT validation.

### Data Isolation

Integration tests write to real Redis using the key prefix `user:integration-user-id:chats` and clean up in `IAsyncDisposable.DisposeAsync`. Cleanup covers both key namespaces:

- **Primary store:** `chat:{chatId:N}:meta`, `chat:{chatId:N}:messages`, and the `user:integration-user-id:chats` sorted set.
- **HybridCache L2:** `manuals:hc:messages:{chatId:N}` per chat, and `manuals:hc:chats:integration-user-id` for the list. These are serialized C# objects written by `IDistributedCache` and will serve stale data on the next test run if not deleted.

Concurrent runs against the same Redis instance are not supported.

`DisposeAsync` must use `TestUserId` — never a hardcoded email string. If `TestUserId` ever migrates, grep all of `Manuals.Tests/` before declaring complete (prior `email→sub` migration left stale cleanup code that leaked state across runs).

### `IntegrationCollection` / `IntegrationChatsTests`

A single xUnit collection fixture (`ICollectionFixture<ManualsWebApplicationFactory>`) wrapping all integration tests. `parallelizeTestCollections: false` is set in `xunit.runner.json`.

---

## Local SonarCloud analysis

Generate coverage first (unit + integration), then run from `Manuals/`:

```powershell
$env:SONAR_TOKEN = "<token>"
& "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\bin\sonar-scanner.bat" `
  "-Dsonar.projectKey=crgolden_Manuals" `
  "-Dsonar.organization=crgolden" `
  "-Dsonar.sources=Manuals" `
  "-Dsonar.tests=Manuals.Tests" `
  "-Dsonar.exclusions=**/bin/**,**/obj/**" `
  "-Dsonar.cs.vscoveragexml.reportsPaths=coverage.xml,coverage-integration.xml"
```

Required coverage files: `coverage.xml`, `coverage-integration.xml`.
