# Testing

The Manuals test suite uses xUnit v3 and is split into two tiers: **unit tests** that run on every push with no external dependencies, and **integration tests** that exercise real Azure Redis and Azure OpenAI on every push to `main`.

For running tests (`dotnet test` from the repo root — never the workspace root) and `ASPNETCORE_ENVIRONMENT` discipline, see the workspace-level [TESTING.md](../AGENTS/TESTING.md).

Unit test coding standards (MockBehavior.Strict, argument verification, SetupSequence, no control-flow in tests, etc.) are in the workspace-level [Unit Test Standards](../AGENTS/TESTING.md#unit-test-standards).

## Test Tiers

| Tier | Trait | Requires Azure? | Runs in CI |
|------|-------|-----------------|------------|
| Unit | `Category=Unit` | No | Every push/PR |
| Integration | `Category=Integration` | Yes — real Redis + Azure OpenAI (API key from User Secrets; no Azure credentials) | Push to `main` only |

`ChatsControllerTests.PostChatAsync_ReturnsCreatedAtActionWithChat` (unit) verifies the shape of the returned `IActionResult` only — calling the action directly bypasses the full HTTP middleware pipeline, so it never exercises `CreatedAtActionResult`'s own route-URL generation. `IntegrationChatsTests`'s `CreateChatAsync` helper is the one place that does: it asserts the `Location` header against a live HTTP response through the real pipeline.

## Running Tests Locally

### Unit Tests

```powershell
dotnet build Manuals.Tests.Unit --configuration Debug
.\Manuals.Tests.Unit\bin\Debug\net10.0\Manuals.Tests.Unit.exe -trait "Category=Unit" -showLiveOutput
```

### Integration Tests

Requires a running Redis instance and nothing else outside the service boundary: Azure OpenAI is mocked. Local Redis runs in WSL 2, which stops its VM, and Redis with it, shortly after the last WSL session closes: keep a WSL terminal open for the whole run. The factory replaces the `ResponsesClient` singleton with one whose `HttpClientPipelineTransport` sends to `TestSupport/OpenAIResponsesStub`, which reads each request as the SDK's own `CreateResponseOptions`, records it, and answers with SDK-serialized `ResponseResult` JSON or `response.output_text.delta` server-sent events. The tests assert what Manuals controls: the reply it relays, the deltas it streams, and the conversation history it sends with the next message. `OpenAIEndpoint` and `OpenAIApiKey` are supplied by the factory through host configuration (`CreateHost`), because `Program.cs` reads them before the host is built; no OpenAI key is needed locally or in CI, and no `az login`, since Azure credentials (`DefaultAzureCredential`) are only constructed inside `IsProduction()`.

1. Set `ASPNETCORE_ENVIRONMENT=Development` so the non-production branch of `Program.cs` runs and User Secrets load.
2. Ensure User Secrets include: `RedisHost`, `RedisPort`, `RedisSsl`, `RedisPassword`, `OpenAIModel`, `OpenAIInstructions`, `OpenAIMaxOutputTokenCount`, `OidcAuthority`.
3. The prompts the tests send come from the `IntegrationPrompts` configuration section, never from test code. `TestSupport/IntegrationPrompts.From` binds it and fails, naming the key, when a value is missing or blank.

   | Key | Local source | CI source (repo variable) | Shape |
   |---|---|---|---|
   | `IntegrationPrompts:ManualRequestFormat` | `Manuals/appsettings.Development.json` | `INTEGRATION_PROMPTS_MANUAL_REQUEST_FORMAT` | Composite format; `{0}` receives the generated product model |
   | `IntegrationPrompts:RecallProduct` | `Manuals/appsettings.Development.json` | `INTEGRATION_PROMPTS_RECALL_PRODUCT` | Plain prompt asking the model to repeat the product it was told |

   CI maps each variable onto the key through the integration step's environment (`IntegrationPrompts__ManualRequestFormat`, `IntegrationPrompts__RecallProduct`). Change a prompt in both places together: the local file and the repo variable are independent copies, and nothing checks that they agree.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet build Manuals.Tests.Integration --configuration Debug
.\Manuals.Tests.Integration\bin\Debug\net10.0\Manuals.Tests.Integration.exe -trait "Category=Integration" -showLiveOutput

# Redirect output for in-flight inspection
cmd /c "Manuals.Tests.Integration\bin\Debug\net10.0\Manuals.Tests.Integration.exe -trait ""Category=Integration"" -showLiveOutput > C:\temp\manuals-integration.txt 2>&1"
```

### In the local gate

`gate.ps1` holds WSL open itself. Before the integration tier it starts a hidden `wsl.exe --exec sleep infinity` session in the default distribution, waits up to 60 seconds for `RedisHost`:`RedisPort` from `Manuals/appsettings.Development.json` to accept a connection, and kills the session when the tier ends, however it ends. When `RedisHost` is already in the environment, as in the alert-triage gate, it opens no WSL session and waits for that host and `RedisPort` instead, failing the row if nothing answers. The `Local Redis (WSL) for the integration tier` row decides the tier:

| What happened | Redis row | Integration tier |
|---|---|---|
| WSL started and Redis accepted a connection | `PASS` | runs |
| `wsl.exe` exited, so WSL could not start (a boot with no virtualization) | `SKIPPED`, with the `wsl.exe` exit code | `SKIPPED` |
| WSL is running but nothing accepted a connection within 60 seconds | `FAIL`, which stops the gate | not reached |
| The tier carried its verdict from an earlier run on the same inputs | `NOT RUN` | `CARRIED` |

## Test Infrastructure

### `ManualsWebApplicationFactory`

`WebApplicationFactory<Program>` used by integration tests. Starts the full `Program.cs` with `ASPNETCORE_ENVIRONMENT=Development`, which selects the non-production branch: `ApiKeyCredential` for OpenAI, User Secrets for Redis, ephemeral Data Protection, no Azure credentials. Besides console logging, it replaces two things: the `ResponsesClient` (above) and the authentication scheme: `IntegrationAuthHandler` always authenticates as `sub = ManualsWebApplicationFactory.TestUserId`, a `Guid` generated per run, bypassing JWT validation. The `Manuals` policy under test is `Program.cs`'s own.

### Data Isolation

Integration tests write to real Redis on database 1 (`TestDatabaseContractConstants.TestDatabase`), and the factory refuses any other number at start. The alert-triage agent's gate runs use database 1 of a separate triage Redis instance, chosen by `RedisHost`/`RedisPort`/`RedisPassword` in its environment, never of the instance production shares. `ManualsWebApplicationFactory` deletes every key in the connected database in `InitializeAsync` before the first test and in `DisposeAsync` after the last, each time after checking that its number is 1, so a run that crashed before its own `DisposeAsync` leaves nothing for the next one; the sweep which covers both namespaces the tier writes: the primary `user:*` / `chat:*` keys and the HybridCache L2 `manuals:hc:*` entries, serialized objects that would otherwise serve stale data to the next run. No test cleans up after itself.

Concurrent runs against the same Redis instance are not supported.

### `IntegrationCollection` / `IntegrationChatsTests`

A single xUnit collection fixture (`ICollectionFixture<ManualsWebApplicationFactory>`) wrapping all integration tests. `parallelizeTestCollections: false` is set in `xunit.runner.json`.

---

## Local SonarCloud analysis

Generate coverage first, then run from `Manuals/`. Unit coverage is OpenCover (branch-bearing, via
`coverlet.console` pinned in `dotnet-tools.json` — restore with `dotnet tool restore`; see the workspace
`TESTING.md` for the command rationale); integration coverage stays VS Coverage XML. SonarCloud unions both.

```powershell
# Unit (OpenCover, carries branch/condition coverage)
dotnet build Manuals.Tests.Unit --configuration Release
dotnet tool restore
dotnet coverlet Manuals.Tests.Unit\bin\Release\net10.0 `
  --target "dotnet" `
  --targetargs "test --project Manuals.Tests.Unit --no-build --configuration Release -- --filter-trait Category=Unit" `
  --format opencover --output "coverage.opencover.xml" `
  --skipautoprops --exclude-by-attribute GeneratedCodeAttribute `
  --exclude-by-file "**/obj/**" --exclude-by-file "**/Program.cs" `
  --does-not-return-attribute DoesNotReturnAttribute --include "[Manuals]*"

# Integration (VS Coverage XML, line-only) — see CI for the dotnet-coverage collect command → coverage-integration.xml

$env:SONAR_TOKEN = "<token>"
& "$env:SystemDrive\sonar-scanner-8.0.1.6346-windows-x64\bin\sonar-scanner.bat" `
  "-Dsonar.projectKey=crgolden_Manuals" `
  "-Dsonar.organization=crgolden" `
  "-Dsonar.sources=Manuals" `
  "-Dsonar.tests=Manuals.Tests.Unit" `
  "-Dsonar.exclusions=**/bin/**,**/obj/**" `
  "-Dsonar.cs.opencover.reportsPaths=coverage.opencover.xml" `
  "-Dsonar.cs.vscoveragexml.reportsPaths=coverage-integration.xml"
```

Required coverage files: `coverage.opencover.xml` (unit, OpenCover), `coverage-integration.xml` (integration, VS Coverage).

### When to build a truth table

The coverage **score is read from SonarCloud, never hand-maintained** here. Build a per-method table in the workspace `COVERAGE/Manuals.md` only when SonarCloud flags a method with **cognitive complexity > 15 AND uncovered conditions > 0**: the table is escalation for the gnarly few, not a per-class deliverable. See the workspace `COVERAGE/METHOD.md`.
