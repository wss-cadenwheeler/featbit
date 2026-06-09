# FeatBit — Project Instructions for AI Assistants

## Overview

FeatBit is an open-source feature flag management platform. It is a multi-tier, polyglot system with .NET APIs, an Angular UI, Python analytics, and Kubernetes-based multi-cluster deployment tooling.

## Repository Structure

```
modules/
  back-end/          # API server (.NET 8, C#, Clean Architecture)
  evaluation-server/  # Flag evaluation + streaming (.NET 8, C#)
  control-plane/      # Cross-DC control plane API (.NET 8, C#)
  front-end/          # Web UI (Angular 19, TypeScript)
  data-analytics/     # Analytics server (Python 3.9+, Flask)
control-plane-qa/     # QA infrastructure for multi-cluster testing
  automation-py/      # Automated test scenarios (Python, Poetry, pytest)
  manual_scripts/     # Human-readable manual test procedures
  docs/               # Deployment, architecture, and testing docs
  k8s/                # Kubernetes manifests for test clusters
  *.ps1               # PowerShell automation scripts
docker/               # Dockerfiles and compose infrastructure
kubernetes/           # Production Kubernetes manifests
infra/                # Infrastructure tooling
```

## Tech Stack

| Component | Language | Framework | Build |
|-----------|----------|-----------|-------|
| API Server | C# | .NET 8.0 | `dotnet` CLI |
| Evaluation Server | C# | .NET 8.0 | `dotnet` CLI |
| Control Plane | C# | .NET 8.0 | `dotnet` CLI |
| Web UI | TypeScript | Angular 19 (NG-ZORRO) | Angular CLI / `npm` |
| Data Analytics | Python 3.9+ | Flask | pip / requirements.txt |
| QA Automation | Python 3.9–3.11 | pytest + Poetry | Poetry |

### Infrastructure Services

- **PostgreSQL** — primary database (also message queue in standard tier)
- **Redis** — caching (pro tier)
- **Kafka** — message queue (pro tier)
- **ClickHouse** — analytics (pro tier)
- **MongoDB** — alternative database option

## Coding Conventions

### C# (.NET)

- Target .NET 8.0 with `<Nullable>enable</Nullable>` (strict null checking)
- `<ImplicitUsings>enable</ImplicitUsings>`
- XML documentation generation is enabled
- Clean Architecture: Api → Application → Domain → Infrastructure layers

### TypeScript / Angular

- 2-space indentation, single quotes, UTF-8
- NG-ZORRO (Ant Design) component library
- i18n support: English (`npm start` port 4200) and Chinese (`npm run start:zh` port 4201)

### Python

- Black formatter (100 char line limit, Python 3.9+ target)
- isort (Black-compatible profile)
- mypy type checking enabled
- flake8 linting

### PowerShell (QA scripts)

- Use approved PowerShell verbs in script names (e.g., `Deploy-`, `Initialize-`, `Start-`)
- Scripts in `control-plane-qa/` target multi-cluster Minikube deployments

## Local Development

```powershell
# Backend API
cd modules/back-end/src/Api
dotnet run

# Frontend UI
cd modules/front-end
npm install
npm start    # English, port 4200

# Infrastructure (databases, Redis, etc.)
docker compose -f docker/composes/docker-compose-infra.yml up -d
```

Default credentials: `test@featbit.com` / `123456`

## Deployment Tiers

- **Standard** (`docker-compose.yml`): PostgreSQL only — simpler, fewer services
- **Professional** (`docker-compose-pro.yml`): PostgreSQL + Redis + Kafka + ClickHouse — production-grade
- **MongoDB variant** (`docker-compose-mongodb.yml`): MongoDB instead of PostgreSQL

## CI/CD

GitHub Actions workflows in `.github/workflows/`:
- `build-and-test-api.yml` — .NET restore → build → test for back-end
- `build-and-test-els.yml` — Evaluation server tests
- `build-and-test-control-plane.yml` — Control plane tests
- `ui-change-validations.yml` — Angular validation
- `publish-docker-images.yml` — Multi-platform Docker image builds

## PR & Commit Conventions

- PR title: < 70 characters, sentence case
- Use emoji prefixes: ✨ feature, 🐛 bugfix, 🔥 P0 fix, ✅ tests, 🚀 perf, 📖 docs, 🏗 infra, 🧹 refactor
- Labels: UI, API, Evaluation Server, OLAP
- Always include a Co-authored-by trailer when AI-assisted

## Control Plane QA

The `control-plane-qa/` directory manages multi-cluster (west/east) Minikube deployments for testing cross-datacenter feature flag propagation. It is organized into three numbered subdirectories:

- **`00-Docs/`**: Architecture reference, deployment guide, testing plans.
- **`01-Infrastructure/`**: Deployment scripts, platform quickstarts (`ubuntu/`, `windows-wsl/`, `windows-hyperv/`), config files, and `extras/` for infrequently-used utilities.
- **`02-Tests/`**: Automated scenarios (`automation-py/scenarios/cp01–cp08.py`), manual procedures (`manual_scripts/`), test applications (`test-app/`, `quick-test/`), and UAT orchestration (`Run-UATTests.ps1`).
- **Artifacts are gitignored** — test output goes to `artifacts/` which is excluded from version control.
- Configuration is in `01-Infrastructure/deployment.env` (copied from `deployment.env.example`); never commit credentials.

## Key Architectural Notes

- The API publishes changes to `cp-*` Kafka topics; the Control Plane consumes, updates Redis in all DCs, then republishes to default topics for Evaluation Servers.
- Evaluation Servers maintain WebSocket connections to SDK clients and push flag updates in real time.
- MongoDB replica set spans clusters (west: 2 members, east: 1 member) with priority-based election.
- Cross-cluster networking uses LoadBalancer services + port forwards + hosts file DNS in the QA environment.
- You can use repomix-output.xml as an additional knowledgebase when exploring the code.
