# Manuals

[![Build and deploy ASP.Net Core app to Azure Web App - crgolden-manuals](https://github.com/crgolden/Manuals/actions/workflows/main_crgolden-manuals.yml/badge.svg)](https://github.com/crgolden/Manuals/actions/workflows/main_crgolden-manuals.yml)

[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=crgolden_Manuals&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=crgolden_Manuals)

An ASP.NET Core 10 API that proxies requests to Azure OpenAI, providing REST endpoints for managing AI chat sessions with full message history stored in Redis.

## Architecture

```
Client
  ├── GET/POST/PATCH/DELETE /api/chats     → ChatsController → RedisChatsService → Redis
  ├── GET /api/chats/{id}/messages         → ChatsController → RedisChatsService → Redis
  ├── POST /api/chats/{id}/messages        → ChatsController → RedisChatsService → ResponsesClient → Azure OpenAI
  └── POST /api/chats/{id}/messages/stream → ChatsController → RedisChatsService → ResponsesClient (SSE) → Azure OpenAI
```

**Client authentication:** all endpoints require a JWT Bearer token with the `manuals` scope (issued by Identity, forwarded by the Experience BFF).

**Azure service authentication:** `DefaultAzureCredential` — Managed Identity in Azure, `az login` locally — used to call Azure OpenAI. No secrets are stored in code or config.

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core 10 (Minimal API) |
| AI | Azure OpenAI Responses API (`OpenAI.Responses`) |
| Persistence | Azure Cache for Redis |
| Auth | JWT Bearer (`manuals` scope) |
| Observability | Azure Monitor, OpenTelemetry, Serilog, Elasticsearch |
| Hosting | Azure App Service (Linux, .NET 10) |
| Azure Identity | `DefaultAzureCredential` (Managed Identity in production) |

## Prerequisites

| Tool | Notes |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | |
| [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) | `az login` for local dev |
| Azure OpenAI resource | Deployed model (e.g., `gpt-4o`) |
| Redis instance | Azure Cache for Redis or local |

## Getting Started

### 1. Authenticate with Azure

```bash
az login
```

### 2. Configure User Secrets

```bash
dotnet user-secrets set "OpenAIEndpoint" "https://<your-resource>.openai.azure.com/" --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIModel" "gpt-4o" --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIMaxOutputTokenCount" "4096" --project Manuals/Manuals.csproj
dotnet user-secrets set "RedisHost" "<your-redis-host>" --project Manuals/Manuals.csproj
dotnet user-secrets set "RedisPort" "6380" --project Manuals/Manuals.csproj
```

### 3. Run

```bash
dotnet run --project Manuals/Manuals.csproj
```

App starts at `https://localhost:7099`. The OpenAPI spec is available at `GET /openapi/v1.json` (development only).

## API Reference

All endpoints require a valid JWT Bearer token with the `manuals` scope. Full request/response schemas are available via the OpenAPI spec.

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/chats` | All chats for the authenticated user, newest first |
| `GET` | `/api/chats/{chatId}` | Single chat by ID |
| `GET` | `/api/chats/{chatId}/messages` | Full message history for a chat |
| `POST` | `/api/chats` | Create a new chat — returns `201 Created` with `Location` header |
| `PATCH` | `/api/chats/{chatId}` | Update chat title (`Content-Type: application/merge-patch+json`) |
| `DELETE` | `/api/chats/{chatId}` | Delete a chat and all its stored messages |
| `POST` | `/api/chats/{chatId}/messages` | Send a message, receive a complete response |
| `POST` | `/api/chats/{chatId}/messages/stream` | Send a message, receive a streaming SSE response |

On the first message sent to a chat, the title is auto-set to the first 60 characters of the user's input.

## Project Structure

```
Manuals/               # ASP.NET Core 10 API — chat CRUD, OpenAI streaming, Redis persistence
Manuals.Tests/         # xUnit v3 — unit tests (Moq) and nightly real-service tests
```

## Commands

> **Shell note:** commands that set environment variables inline use bash syntax. On Windows, use Git Bash, WSL, or set the variables separately before running the `dotnet` command.

```bash
# Build
dotnet build

# Unit tests only (no Azure required)
dotnet test --project Manuals.Tests --configuration Release -- --filter-trait "Category=Unit"

# Nightly tests (requires live Azure Redis + Azure OpenAI — az login required)
ASPNETCORE_ENVIRONMENT=Development dotnet test --project Manuals.Tests --configuration Release -- --filter-trait "Category=Nightly"

# Publish web app
dotnet publish Manuals -c Release -o ./publish
```

See [TESTING.md](TESTING.md) for the full testing guide — unit tests, nightly real-service tests, local prerequisites, and CI pipeline details.

## Known SDK Caveat

`RedisChatsService` uses `ResponseItem.CreateAssistantMessageItem(string text)` to reconstruct assistant turns when sending full conversation history to the Responses API. This experimental factory method **may not be present in OpenAI SDK v2.10.0**.

If the project fails to compile with a missing-method error, see the workaround in [`CLAUDE.md` → SDK caveat](CLAUDE.md#sdk-caveat--responseitemcreateassistantmessageitem).

## Deployment

The GitHub Actions workflow triggers on pushes to `main` and pull requests. Pull requests run build and test only.

**Build job** — runs on every trigger:
1. Builds the solution (`dotnet build --configuration Release`)
2. Runs unit tests with coverage
3. Logs in to Azure via OIDC and runs nightly tests (on `schedule` or `workflow_dispatch` only)
4. Publishes the web app and uploads the artifact

**Deploy job** — runs after a successful build on `main`:
1. Deploys the web app to **Azure App Service** via Azure OIDC

### Azure Resources

| Resource | Notes |
|---|---|
| Azure OpenAI | Requires a deployed model (e.g. `gpt-4o`) |
| Azure Cache for Redis | SSL enabled, port 6380 |
| Azure App Service (Linux, .NET 10) | Standard or higher plan |
| Managed Identity (system-assigned) | Assign **Cognitive Services OpenAI User** role on the OpenAI resource |
| App Registration (for GitHub Actions) | Add federated credential for `repo:<org>/Manuals:ref:refs/heads/main` |

### App Service Application Settings

| Key | Value |
|---|---|
| `OpenAIEndpoint` | `https://<your-resource>.openai.azure.com/` |
| `OpenAIModel` | `gpt-4o` |
| `OpenAIMaxOutputTokenCount` | `4096` |
| `RedisHost` | `<your-redis>.redis.cache.windows.net` |
| `RedisPort` | `6380` |

### GitHub Actions Secrets

| Secret | Description |
|---|---|
| `AZURE_CLIENT_ID` | App Registration client ID |
| `AZURE_TENANT_ID` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_WEBAPP_NAME` | App Service name |
