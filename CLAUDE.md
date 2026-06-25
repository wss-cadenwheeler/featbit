# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Architecture Overview

FeatBit is a self-hosted feature flag management platform with a microservices architecture. Five independent services communicate via pluggable message queues:

```
UI (Angular 19) → API Server (.NET 8) → Message Queue → Evaluation Server (.NET 8)
                                       ↓
                             Data Analytics (Python/Flask)

Control Plane (.NET 8) — optional broker between API and Evaluation Servers
```

### Service Locations
| Service | Path | Port |
|---|---|---|
| Frontend UI | `modules/front-end/` | 4200 (dev) |
| API Server | `modules/back-end/` | 5000 |
| Evaluation Server | `modules/evaluation-server/` | 5100 |
| Control Plane | `modules/control-plane/` | — |
| Data Analytics | `modules/data-analytics/` | 8200 |

### Pluggable Providers
All backend services select infrastructure via environment variables:
- `DbProvider` — `Postgres` or `MongoDB`
- `MqProvider` — `Postgres`, `Redis`, or `Kafka`
- `CacheProvider` — `Redis` or `None`
- `DB_PROVIDER` (analytics) — `PostgreSQL`, `MongoDB`, or `ClickHouse`

Standard edition uses Postgres for everything. Professional edition adds Kafka and ClickHouse for high-throughput analytics.

### Message Queue Topics
Flag/segment changes flow from API → MQ → Evaluation Server via these topics:
- `featbit-feature-flag-change`, `featbit-segment-change`
- `featbit-endusers`, `featbit-insights`
- `featbit-control-plane-*` (when Control Plane is enabled)

### .NET Service Architecture
API Server, Evaluation Server, and Control Plane all use Clean Architecture:
`Api → Application → Domain → Infrastructure`

Health endpoints on all .NET services: `/health/liveness`, `/health/readiness`

---

## Commands

### Frontend (modules/front-end/)
```bash
npm install
npm run start          # Dev server (English) at localhost:4200
npm run start:zh       # Dev server (Chinese) at localhost:4201
npm run build:prod     # Production build
npm run test           # Unit tests (Jasmine/Karma)
npm run test-coverage  # Coverage report
npm run i18n           # Extract i18n strings and validate translations
```

### .NET Services (back-end / evaluation-server / control-plane)
```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --verbosity normal

# Run a single test
dotnet test --filter "FullyQualifiedName~TestClassName"

# Build Docker image (from module root)
docker build --progress plain -f ./deploy/Dockerfile -t featbit/api:local .
```

### Local Development (Docker Compose)
```bash
# Infrastructure only (MongoDB + Redis) — then run services locally
docker compose --project-directory . -f ./docker/composes/docker-compose-infra.yml up -d

# Full dev stack (MongoDB)
docker compose --project-directory . -f ./docker/composes/docker-compose-dev.yml up -d

# Full dev stack (Postgres)
docker compose --project-directory . -f ./docker/composes/docker-compose-dev-postgres.yml up -d

# Standard edition (all services, Postgres + Redis)
docker compose -f docker-compose-standard.yml up -d

# Professional edition (Kafka + ClickHouse)
docker compose -f docker-compose-pro.yml up -d
```

---

## Key Configuration

### Backend Environment Variables (appsettings.json / env)
- `ConnectionStrings__Mongo` / `ConnectionStrings__Postgres`
- `Redis__ConnectionString`
- `JWT__Issuer`, `JWT__Audience`, `JWT__Key`
- `Kafka__BootstrapServers` (pro edition)
- `SSOEnabled`, `WorkspaceId`, `OAuthConfig__*`

### Frontend Environment
Config at `modules/front-end/src/environments/`. API base URL is set via `environment.ts` (dev) or injected at container startup via `config.js`.

### Evaluation Server Rate Limiting
Configurable per-endpoint rate limiting via `RateLimit__*` env vars.

---

## CI/CD (ONLY APPLIES to the github.com/featbit/featbit repo)

GitHub Actions workflows run on push/PR to `main` for path-filtered changes:
- `build-and-test-api.yml` — dotnet restore → build → test
- `build-and-test-els.yml` — same for Evaluation Server
- `build-and-test-control-plane.yml` — same for Control Plane
- `ui-change-validations.yml` — npm ci → i18n → build
- `publish-docker-images.yml` — manual trigger; builds multi-platform (amd64/arm64) images to Docker Hub

Kubernetes manifests are in `kubernetes/` (standard, pro, demo, minikube variants).

---

## Working agreement

- **Verification-first.** Before starting any task, state up front how you will
  verify it — the concrete check: the exact command to run, the test to add/run,
  the endpoint to hit, the output or metric to inspect. After finishing, actually
  run that verification and report the results, including failures, skipped steps,
  and partial outcomes. "Done" means *verified*, not "should work" — if you can't
  verify, say so explicitly and explain why.
