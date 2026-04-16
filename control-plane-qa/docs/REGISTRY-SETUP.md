# Registry Setup — Plain English Guide

If you have never thought about container registries before, this guide is for you.
It answers one question in plain language: **Where do the Docker images come from?**

---

## The Short Version

This deployment uses **two completely separate sets of images** that come from different places and are configured independently.

| Image set | What it contains | Configured by |
|-----------|-----------------|---------------|
| **Infrastructure images** | MongoDB, Redis, Kafka, ClickHouse, Kafka UI | `CUSTOM_IMAGE_REGISTRY` |
| **FeatBit application images** | API Server, UI, Evaluation Server, Control Plane, Data Analytics | `FEATBIT_IMAGE_REGISTRY` |

You configure each set separately because they have different sources and different options.

---

## Part 1 — Infrastructure Images (MongoDB, Redis, Kafka, etc.)

These are standard open-source images from Docker Hub. You did not write them and you do not build them.

### Option A: Pull directly from Docker Hub (simplest, but risky)

**What to do:** Leave `CUSTOM_IMAGE_REGISTRY` blank (comment it out or delete it from `deployment.env`).

```
# CUSTOM_IMAGE_REGISTRY=
```

**What happens:** When the clusters start up, each Minikube node downloads the images directly from Docker Hub over the internet.

**The risk:** Docker Hub enforces pull rate limits — typically 100 pulls per 6 hours for unauthenticated users, 200 for free accounts. A full deployment pulls roughly 8–10 distinct images. If you recreate clusters frequently, or if several developers share the same IP address (common behind a corporate NAT), you will hit the limit and see errors like:

```
toomanyrequests: You have reached your pull rate limit.
```

If this happens, wait a few hours, or switch to Option B.

---

### Option B: Pull from a private/corporate registry (recommended for teams)

**What to do:** Set `CUSTOM_IMAGE_REGISTRY` to the hostname of your private registry.

```
CUSTOM_IMAGE_REGISTRY=harbor.example.com
```

**What happens:** The deploy script rewrites every infrastructure image reference in the Kubernetes YAML before applying it, replacing the Docker Hub image names with paths under your registry. No source files are modified — the rewritten YAML is written to `kubernetes/.generated/` (which is gitignored).

**What you need first:** The images must actually exist in your registry. Mirror them from Docker Hub using your registry's proxy/mirror feature, or pull and re-push manually. The full list of required images is in `kubernetes/infra-image-map.json`.

**Namespace layout:** If your registry uses a non-standard path structure (e.g., Harbor organises mirrored images under `/dockerhub/library/`), also set:

```
INFRA_IMAGE_REPOSITORY=harbor.example.com/dockerhub/library
```

If you leave `INFRA_IMAGE_REPOSITORY` blank and `CUSTOM_IMAGE_REGISTRY` is set, it defaults to `<registry>/dockerhub/library` automatically.

**Credentials:** If your registry requires authentication, the deploy script will prompt you. The credentials are stored as a Kubernetes secret named `registry-credentials` in the `featbit` namespace of each cluster (override the name with `CUSTOM_REGISTRY_SECRET_NAME` if needed).

> **Hosts file note:** If your registry hostname is not in public DNS (e.g., it is only resolvable on your internal network via a hosts file or internal DNS server), make sure that name resolves on your developer machine before running the deploy script. If it does not, add an entry to `C:\Windows\System32\drivers\etc\hosts`:
> ```
> 10.0.0.50  harbor.example.com
> ```
> The Minikube nodes are Linux VMs — they use the same DNS as the host machine via `host.minikube.internal`, but if you have name resolution issues inside the cluster you may also need to configure CoreDNS or use the IP directly.

---

## Part 2 — FeatBit Application Images

These are the five images built from the source code in this repository:

| Image | Source module | Needs local build? |
|-------|--------------|-------------------|
| `featbit-api-server` | `modules/back-end` | Only if modifying back-end code |
| `featbit-evaluation-server` | `modules/evaluation-server` | Only if modifying evaluation-server code |
| `featbit-control-plane` | `modules/control-plane` | Only if modifying control-plane code |
| `featbit-ui` | `modules/front-end` | Rarely — published to Docker Hub |
| `featbit-data-analytics-server` | `modules/data-analytics` | Rarely — published to Docker Hub |

The UI and Data Analytics images in particular do not need to be rebuilt for control-plane QA purposes. All five images are published to Docker Hub as `featbit/featbit-<name>:latest`.

You have three options for each image. Pick **one** option and set `FEATBIT_IMAGE_REGISTRY` accordingly.

---

### Option A: Build from source and push to the local registry (default)

**Best for:** Developers actively modifying the back-end, evaluation server, or control plane.

**What to do:** Leave `FEATBIT_IMAGE_REGISTRY` blank (or comment it out).

```
# FEATBIT_IMAGE_REGISTRY=
```

Then run `Initialize-LocalRegistry.ps1` before deploying. It starts a local `registry:2` container on `localhost:5000` and builds + pushes all five images from the source in this repo.

```powershell
.\Initialize-LocalRegistry.ps1

# To build only the images you changed:
.\Initialize-LocalRegistry.ps1 -Images api-server,evaluation-server
```

**What happens at deploy time:** Each Minikube node pulls images from `host.minikube.internal:5000` — which is your local machine's port 5000, reachable from inside the VM. No TLS is required; the clusters are configured with `--insecure-registry=host.minikube.internal:5000` at creation time.

**Hosts file requirement:** None. `host.minikube.internal` is a special hostname that Minikube resolves automatically to the host machine's bridge IP. No hosts file entry is needed.

**What the local registry looks like:**

```
localhost:5000/featbit/featbit-api-server:latest
localhost:5000/featbit/featbit-ui:latest
localhost:5000/featbit/featbit-evaluation-server:latest
localhost:5000/featbit/featbit-control-plane:latest
localhost:5000/featbit/featbit-data-analytics-server:latest
```

> **Rancher Desktop users:** Rancher Desktop uses the `docker-container` buildx driver by default, which does not load built images into the local image store unless you pass `--load`. `Initialize-LocalRegistry.ps1` handles this for you — it always passes `--load` during build.

---

### Option B: Pull from Docker Hub

**Best for:** Developers who do not need to change FeatBit source code and want the simplest possible setup, and are not worried about Docker Hub rate limits.

**What to do:** Set `FEATBIT_IMAGE_REGISTRY` to `docker.io`.

```
FEATBIT_IMAGE_REGISTRY=docker.io
```

**What happens:** Images are pulled directly from `docker.io/featbit/featbit-<name>:latest`. You do not need to run `Initialize-LocalRegistry.ps1` at all.

**The risk:** Same Docker Hub rate limit risk as infrastructure images (Option A in Part 1). If you are also pulling infrastructure images from Docker Hub at the same time, your rate limit budget is consumed by both sets.

---

### Option C: Pull from a private/corporate registry

**Best for:** Teams that mirror Docker Hub images into an internal registry, or teams that build and push their own FeatBit images to a shared registry as part of a CI pipeline.

**What to do:** Set `FEATBIT_IMAGE_REGISTRY` to the registry and path prefix where your FeatBit images live.

```
FEATBIT_IMAGE_REGISTRY=harbor.example.com/featbit
```

**What happens:** Images are pulled as `harbor.example.com/featbit/featbit-<name>:latest`. The deploy script appends `/<imagename>:latest` to whatever you specify here.

**What you need first:** The images must exist in your registry. Either push them from a local build, or have CI push them there. The image names must follow the pattern `featbit-<name>:latest` under the path you specified.

**Hosts file requirement:** Same rule as infrastructure images — if the registry hostname is not in public DNS, add it to your hosts file before running the deploy script. The Minikube VMs must also be able to resolve and reach it; if the registry is on an internal network, ensure the VMs have network access (they inherit the host's network via the bridge interface in most setups).

---

## Decision Checklist

Answer these questions in order to know exactly what to set.

**Q1: Do you want to modify FeatBit source code?**

- **Yes** → Use [Part 2 Option A](#option-a-build-from-source-and-push-to-the-local-registry-default). Leave `FEATBIT_IMAGE_REGISTRY` blank. Run `Initialize-LocalRegistry.ps1` before deploying. You only need to build the specific images you are changing.
- **No** → Continue to Q2.

**Q2: Does your team use a private/internal container registry?**

- **Yes** → Use [Part 2 Option C](#option-c-pull-from-a-privatecorporate-registry) for FeatBit images and [Part 1 Option B](#option-b-pull-from-a-privatecorporate-registry-recommended-for-teams) for infra images. Set both `FEATBIT_IMAGE_REGISTRY` and `CUSTOM_IMAGE_REGISTRY`.
- **No** → Use [Part 2 Option B](#option-b-pull-from-docker-hub) and [Part 1 Option A](#option-a-pull-directly-from-docker-hub-simplest-but-risky). Leave both blank, but be aware of rate limiting if recreating clusters frequently.

**Q3 (if you have a private registry): Is the registry hostname resolvable on your machine?**

- **Yes (it's in public DNS or your corporate DNS)** → Nothing extra needed.
- **No (only accessible via IP or internal hostname)** → Add an entry to `C:\Windows\System32\drivers\etc\hosts`. Example: `10.0.0.50  harbor.example.com`.

---

## Complete deployment.env Examples

### Scenario: Everything from local build + Docker Hub infra

```bash
# CUSTOM_IMAGE_REGISTRY=          # blank = Docker Hub for infra
# FEATBIT_IMAGE_REGISTRY=         # blank = local registry for FeatBit apps
```

Run `Initialize-LocalRegistry.ps1` first. Accept Docker Hub rate limit risk for infra images.

---

### Scenario: Local build for FeatBit apps + corporate registry for infra

```bash
CUSTOM_IMAGE_REGISTRY=harbor.example.com
INFRA_IMAGE_REPOSITORY=harbor.example.com/dockerhub/library
# FEATBIT_IMAGE_REGISTRY=         # blank = local registry for FeatBit apps
```

Run `Initialize-LocalRegistry.ps1` first. No Docker Hub calls.

---

### Scenario: Everything from the corporate registry (no local builds)

```bash
CUSTOM_IMAGE_REGISTRY=harbor.example.com
INFRA_IMAGE_REPOSITORY=harbor.example.com/dockerhub/library
FEATBIT_IMAGE_REGISTRY=harbor.example.com/featbit
```

Do not run `Initialize-LocalRegistry.ps1`. FeatBit images must already exist in your registry.

---

### Scenario: Everything from Docker Hub (simplest, rate-limit risk)

```bash
CUSTOM_IMAGE_REGISTRY=
FEATBIT_IMAGE_REGISTRY=docker.io
```

Do not run `Initialize-LocalRegistry.ps1`. Fastest to get started; will hit rate limits if used heavily.
