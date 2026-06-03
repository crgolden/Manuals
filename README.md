# Manuals

[![Build and deploy ASP.Net Core app to Azure Web App - crgolden-manuals](https://github.com/crgolden/Manuals/actions/workflows/main_crgolden-manuals.yml/badge.svg)](https://github.com/crgolden/Manuals/actions/workflows/main_crgolden-manuals.yml)

[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=crgolden_Manuals&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=crgolden_Manuals)

An ASP.NET Core 10 API that proxies requests to Azure OpenAI, providing REST endpoints for managing AI chat sessions with full message history stored in Redis.

## Sibling Applications

Manuals is a **resource server** in a five-app system. All endpoints require a JWT Bearer token with the `manuals` scope.

| Repo | Role | How Manuals interacts |
|---|---|---|
| [Identity](https://github.com/crgolden/Identity) | OIDC Identity Provider | Issues the access tokens Manuals validates (scope `manuals`) |
| [Experience](https://github.com/crgolden/Experience) | Angular SPA + ASP.NET Core BFF | Sole client today — the BFF proxies `/manuals/api/**` with an access token |
| [Products](https://github.com/crgolden/Products) | OData v4 product catalog API | The `Product.ManualUrl` field is populated by the embedded chat panel that drives Manuals |
| [Infrastructure](https://github.com/crgolden/Infrastructure) | Health monitoring dashboard | Polls Manuals' `/health` endpoint |

## Architecture

```
Client
  ├── GET/POST/PATCH/DELETE /chats     → ChatsController → RedisChatsService → Redis
  ├── GET /chats/{id}/messages         → ChatsController → RedisChatsService → Redis
  ├── POST /chats/{id}/messages        → ChatsController → RedisChatsService → ResponsesClient → Azure OpenAI
  └── POST /chats/{id}/messages/stream → ChatsController → RedisChatsService → ResponsesClient (SSE) → Azure OpenAI
```

**Client authentication:** all endpoints require a JWT Bearer token with the `manuals` scope (issued by Identity, forwarded by the Experience BFF).

**Azure OpenAI authentication:** `DefaultAzureCredential` (Managed Identity) in production. `ApiKeyCredential` (`OpenAIApiKey` User Secret) in non-production — no `az login` needed locally.

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core 10 (Controller API) |
| AI | Azure OpenAI Responses API (`OpenAI.Responses`) |
| Persistence | Azure Cache for Redis |
| Auth | JWT Bearer (`manuals` scope) |
| Observability | Azure Monitor, OpenTelemetry, Serilog, Elasticsearch |
| Hosting | Azure App Service (Windows, .NET 10, F1 plan) |
| Cloud Identity | `DefaultAzureCredential` (Managed Identity in production) |

## Prerequisites

| Tool | Notes |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | |
| Azure OpenAI resource | Deployed model (accessible via API key in non-production) |
| Azure OpenAI resource | Deployed model (e.g., `gpt-4o`) |
| Redis instance | Azure Cache for Redis or local |

## Getting Started

### 1. Configure User Secrets

```powershell
dotnet user-secrets set "OidcAuthority" "https://localhost:7261" --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIEndpoint" "https://<your-resource>.openai.azure.com/" --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIApiKey" "<your-api-key>" --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIModel" "gpt-4o" --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIInstructions" "You are a helpful assistant." --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIMaxOutputTokenCount" "4096" --project Manuals/Manuals.csproj
dotnet user-secrets set "RedisHost" "<your-redis-host>" --project Manuals/Manuals.csproj
dotnet user-secrets set "RedisPort" "6380" --project Manuals/Manuals.csproj
dotnet user-secrets set "RedisSsl" "true" --project Manuals/Manuals.csproj
dotnet user-secrets set "RedisPassword" "<your-redis-password>" --project Manuals/Manuals.csproj
```

### 2. Run

```bash
dotnet run --project Manuals/Manuals.csproj
```

App starts at `https://localhost:7099`. The OpenAPI spec is available at `GET /openapi/v1.json` (development only).

## API Reference

All endpoints require a valid JWT Bearer token with the `manuals` scope.

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/chats` | All chats for the authenticated user, newest first |
| `GET` | `/chats/{chatId}` | Single chat by ID |
| `GET` | `/chats/{chatId}/messages` | Full message history for a chat |
| `POST` | `/chats` | Create a new chat — returns `201 Created` with `Location` header |
| `PATCH` | `/chats/{chatId}` | Update chat title (`Content-Type: application/merge-patch+json`) |
| `DELETE` | `/chats/{chatId}` | Delete a chat and all its stored messages |
| `POST` | `/chats/{chatId}/messages` | Send a message, receive a complete response |
| `POST` | `/chats/{chatId}/messages/stream` | Send a message, receive a streaming SSE response |

On the first message sent to a chat, the title is auto-set to the first 60 characters of the user's input (with `…` if truncated).

## Project Structure

```
Manuals/               # ASP.NET Core 10 API — chat CRUD, OpenAI streaming, Redis persistence
Manuals.Tests/         # xUnit v3 — unit tests (Moq) and integration tests against real Azure Redis + OpenAI
```

## Commands

```powershell
# Build
dotnet build

# Unit tests only (no Azure required)
dotnet build Manuals.Tests --configuration Debug
.\Manuals.Tests\bin\Debug\net10.0\Manuals.Tests.exe -trait "Category=Unit" -showLiveOutput

# Integration tests (requires live Redis + Azure OpenAI endpoint; no az login needed)
$env:ASPNETCORE_ENVIRONMENT = "Development"
.\Manuals.Tests\bin\Debug\net10.0\Manuals.Tests.exe -trait "Category=Integration" -showLiveOutput

# Publish
dotnet publish Manuals -c Release -r win-x86 --self-contained false -o ./publish
```

See [TESTING.md](TESTING.md) for the full testing guide.

## Deployment

The GitHub Actions workflow triggers on pushes to `main` and pull requests.

**Build job** — runs on every trigger:
1. Builds the solution (`dotnet build --configuration Release`)
2. Runs unit tests with coverage
3. Logs in to Azure via OIDC and runs integration tests (on push to `main` and `workflow_dispatch`; skipped on `pull_request`)
4. Publishes the web app (`-r win-x86 --self-contained false`) and uploads the artifact

**Deploy job** — runs after a successful build on `main`:
1. Deploys the web app to **Azure App Service (Windows, F1)** via Azure OIDC

### Azure App Service Application Settings

| Key | Notes |
|---|---|
| `OidcAuthority` | Identity server URL |
| `OpenAIEndpoint` | `https://<your-resource>.openai.azure.com/` |
| `OpenAIModel` | Deployed model name (e.g. `gpt-4o`) |
| `OpenAIInstructions` | System prompt |
| `OpenAIMaxOutputTokenCount` | Max tokens per completion |
| `RedisHost` | `<your-redis>.redis.cache.windows.net` |
| `RedisPort` | `6380` |
| `RedisSsl` | `true` |
| `KeyVaultUri` | Azure Key Vault URL |
| `BlobUri` | Azure Blob Storage URL (Data Protection keys) |
| `DataProtectionKeyIdentifier` | Key Vault key URI |
| `ElasticsearchNode` | Elasticsearch endpoint |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Azure Monitor connection string |
| `DefaultAzureCredentialOptions__ExcludeManagedIdentityCredential` | `false` |

Secrets (`RedisPassword`, `ElasticsearchUsername`, `ElasticsearchPassword`) are fetched from Azure Key Vault at startup via Managed Identity — do not set them as Application Settings.
