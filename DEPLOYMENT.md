# FeatBit Multi-Cluster Deployment

This repository contains automated scripts for deploying FeatBit Pro to two Minikube clusters (west and east) with DNS-based access.

## Overview

The deployment consists of three main scripts that automate the entire setup process:

1. **Deploy-FeatBitClusters.ps1** - Creates clusters and deploys FeatBit
2. **Configure-FeatBitIngress.ps1** - Sets up Kubernetes ingress
3. **Setup-FeatBitProxy.ps1** - Configures nginx reverse proxy with DNS names

## Prerequisites

- Windows 10/11 or Windows Server
- Docker Desktop installed and running
- Minikube installed
- kubectl installed
- Chocolatey package manager installed
- PowerShell 5.1 or later
- Administrator privileges (for proxy setup)

## Architecture

```
Browser (DNS names)
    ↓
Nginx Reverse Proxy (Windows Host)
    ↓
Port Forwards (kubectl)
    ↓
Kubernetes Services (Minikube)
    ↓
FeatBit Pods
```

### Clusters

- **West Cluster** (`west` profile)
  - CPU: 4 cores
  - Memory: 8GB
  - Subnet: 192.168.49.x
  
- **East Cluster** (`east` profile)
  - CPU: 4 cores
  - Memory: 8GB
  - Subnet: 192.168.58.x

### DNS Names

| Service | West Cluster | East Cluster |
|---------|-------------|-------------|
| UI | http://featbit.west.local | http://featbit.east.local |
| API | http://featbit-api.west.local | http://featbit-api.east.local |
| Evaluation | http://featbit-eval.west.local | http://featbit-eval.east.local |

## Quick Start

### Step 1: Deploy Clusters and FeatBit

```powershell
cd C:\Users\<your-user>\source\wss-cadenwheeler\featbit
.\Deploy-FeatBitClusters.ps1
```

This script will:
- ✓ Check Docker registry is running
- ✓ Verify FeatBit images are available
- ✓ Create west and east Minikube clusters with insecure registry support
- ✓ Enable ingress addon
- ✓ Connect west/east minikube nodes to a shared custom Docker network (`featbit-cluster-network`)
- ✓ Deploy infrastructure (MongoDB, Redis, ClickHouse, Kafka)
- ✓ Deploy FeatBit applications (UI, API, Evaluation, Control Plane, Data Analytics)

**Estimated time:** 5-7 minutes

### Step 2: Configure Ingress

```powershell
.\Configure-FeatBitIngress.ps1 -UsePortForward
```

This script will:
- ✓ Create nginx ingress resources
- ✓ Update UI deployments with localhost URLs
- ✓ Start port forwarding for all services

**Estimated time:** 1-2 minutes

### Step 3: Setup Nginx Reverse Proxy (Requires Admin)

**Open PowerShell as Administrator**, then run:

```powershell
cd C:\Users\<your-user>\source\wss-cadenwheeler\featbit
.\Setup-FeatBitProxy.ps1
```

This script will:
- ✓ Install nginx via Chocolatey
- ✓ Configure nginx as reverse proxy with CORS support
- ✓ Add DNS entries to Windows hosts file
- ✓ Update FeatBit UI deployments with DNS URLs
- ✓ Start all port forwarding services
- ✓ Start nginx

**Estimated time:** 2-3 minutes

### Step 4: Access FeatBit

Open your browser to:
- West Cluster: http://featbit.west.local
- East Cluster: http://featbit.east.local

**Default credentials:**
- Username: `test@featbit.com`
- Password: `123456`

## Script Reference

### Deploy-FeatBitClusters.ps1

**Purpose:** Creates Minikube clusters and deploys FeatBit Pro.

**Parameters:**
- `-SkipClusterCreation` - Only deploy FeatBit (skip cluster creation)
- `-SkipImageCheck` - Skip verification of Docker images
- `-DeploymentMode` - `Basic` (default) or `Advanced`
- `-DatabaseProvider` - `MongoDb` (default) or `Postgres` (exactly one database provider per deployment)
- `-HostInfraComponents` - Host Docker infra components in `Basic` mode (`redis`, `kafka`, `clickhouse`, and only one of `mongodb` or `postgresql`)
- `-WestCpus` - CPU count for west cluster (default: 4)
- `-WestMemory` - Memory in MB for west cluster (default: 8192)
- `-EastCpus` - CPU count for east cluster (default: 4)
- `-EastMemory` - Memory in MB for east cluster (default: 8192)

**Examples:**
```powershell
# Full deployment
.\Deploy-FeatBitClusters.ps1

# Basic mode with host infrastructure (default behavior)
.\Deploy-FeatBitClusters.ps1 -DeploymentMode Basic -HostInfraComponents redis,kafka,clickhouse,mongodb

# Advanced mode (infra in both east/west clusters)
.\Deploy-FeatBitClusters.ps1 -DeploymentMode Advanced

# PostgreSQL deployment (binary DB choice: Postgres instead of MongoDb)
.\Deploy-FeatBitClusters.ps1 -DatabaseProvider Postgres -DeploymentMode Advanced

# Custom resources
.\Deploy-FeatBitClusters.ps1 -WestCpus 6 -WestMemory 16384

# Only redeploy FeatBit
.\Deploy-FeatBitClusters.ps1 -SkipClusterCreation
```

### Configure-FeatBitIngress.ps1

**Purpose:** Configures Kubernetes ingress and port forwarding.

**Parameters:**
- `-WestDomain` - Domain for west cluster (default: west.featbit.local)
- `-EastDomain` - Domain for east cluster (default: east.featbit.local)
- `-UsePortForward` - Start port forwarding automatically

**Examples:**
```powershell
# Configure ingress only
.\Configure-FeatBitIngress.ps1

# Configure and start port forwarding
.\Configure-FeatBitIngress.ps1 -UsePortForward

# Custom domains
.\Configure-FeatBitIngress.ps1 -WestDomain "west.mycompany.local"
```

### Setup-FeatBitProxy.ps1

**Purpose:** Sets up nginx reverse proxy with DNS names. **Requires Administrator.**

**Parameters:**
- `-NginxPath` - Path to nginx installation (default: C:\nginx)
- `-SkipNginxInstall` - Skip nginx installation if already present

**Examples:**
```powershell
# Full setup (requires admin)
.\Setup-FeatBitProxy.ps1

# Use existing nginx installation
.\Setup-FeatBitProxy.ps1 -SkipNginxInstall -NginxPath "C:\custom\nginx"
```

## Image Management

### Required Images

The following FeatBit images must be available in the local Docker registry at `localhost:5000/featbit/*`:

- featbit-api-server:latest
- featbit-ui:latest
- featbit-evaluation-server:latest
- featbit-control-plane:latest
- featbit-data-analytics-server:latest

### Pushing Images to Local Registry

If images are not in the local registry, tag and push them:

```powershell
# Tag images
docker tag <source-image> localhost:5000/featbit/featbit-api-server:latest
docker tag <source-image> localhost:5000/featbit/featbit-ui:latest
docker tag <source-image> localhost:5000/featbit/featbit-evaluation-server:latest
docker tag <source-image> localhost:5000/featbit/featbit-control-plane:latest
docker tag <source-image> localhost:5000/featbit/featbit-data-analytics-server:latest

# Push to local registry
docker push localhost:5000/featbit/featbit-api-server:latest
docker push localhost:5000/featbit/featbit-ui:latest
docker push localhost:5000/featbit/featbit-evaluation-server:latest
docker push localhost:5000/featbit/featbit-control-plane:latest
docker push localhost:5000/featbit/featbit-data-analytics-server:latest
```

## Troubleshooting

### can't connect to cluster with kubectl

If you get an error similar to the following
```
E0305 13:08:56.969153   36228 memcache.go:265] "Unhandled Error" err="couldn't get current server API group list: Get \"https://127.0.0.1:32771/api?timeout=32s\": read tcp 127.0.0.1:55946->127.0.0.1:32771: wsarecv: An existing connection was forcibly closed by the remote host."
```

or
```
E0305 13:17:37.244838   35172 memcache.go:265] "Unhandled Error" err="couldn't get current server API group list: Get \"https://127.0.0.1:32771/api?timeout=32s\": net/http: TLS handshake timeout"
```

Check the port numbers that have been assigned to your "cluster" pods using `docker ps` against the ports set in your %userprofile$/.kube/config file for the east and west cluster.  If needed set the correct port in your .kube/config file.

### Pods Not Starting

Check pod status:
```powershell
kubectl --context west get pods -n featbit
kubectl --context east get pods -n featbit
```

View pod logs:
```powershell
kubectl --context west logs <pod-name> -n featbit
kubectl --context east logs <pod-name> -n featbit
```

### Image Pull Errors

Verify images in local registry:
```powershell
docker images | Select-String "localhost:5000/featbit"
```

Test registry accessibility from Minikube:
```powershell
minikube -p west ssh -- "curl -I http://host.minikube.internal:5000/v2/"
minikube -p east ssh -- "curl -I http://host.minikube.internal:5000/v2/"
```

### Port Forward Issues

List active port forwards:
```powershell
Get-Process | Where-Object {$_.ProcessName -eq "kubectl"}
```

Stop all port forwards:
```powershell
Get-Process kubectl | Stop-Process
```

Restart port forwarding:
```powershell
.\Configure-FeatBitIngress.ps1 -UsePortForward
```

### Nginx Issues

Check nginx status:
```powershell
Get-Process nginx -ErrorAction SilentlyContinue
```

Test nginx configuration:
```powershell
cd C:\nginx
.\nginx.exe -t
```

View nginx logs:
```powershell
Get-Content C:\nginx\logs\error.log -Tail 50
```

Restart nginx:
```powershell
Stop-Process -Name nginx
cd C:\nginx
.\nginx.exe
```

### CORS Errors

Ensure nginx is running and configured correctly:
```powershell
Get-Process nginx
```

Verify hosts file entries:
```powershell
Get-Content C:\Windows\System32\drivers\etc\hosts | Select-String "featbit"
```

Check UI environment variables:
```powershell
kubectl --context west get deployment ui -n featbit -o yaml | Select-String "API_URL" -Context 2
```

### DNS Resolution Issues

Verify hosts file entries:
```powershell
Get-Content C:\Windows\System32\drivers\etc\hosts | Select-String "127.0.0.1"
```

Test DNS resolution:
```powershell
ping featbit.west.local
ping featbit.east.local
```

Clear browser cache and restart browser if DNS changes don't take effect.

## Management Commands

### Cluster Management

```powershell
# List Minikube profiles
minikube profile list

# Get cluster IPs
minikube -p west ip
minikube -p east ip

# Stop clusters
minikube -p west stop
minikube -p east stop

# Start clusters
minikube -p west start
minikube -p east start

# Delete clusters
minikube -p west delete
minikube -p east delete
```

### Kubernetes Commands

```powershell
# View all resources
kubectl --context west get all -n featbit
kubectl --context east get all -n featbit

# View pods
kubectl --context west get pods -n featbit
kubectl --context east get pods -n featbit

# View services
kubectl --context west get svc -n featbit
kubectl --context east get svc -n featbit

# View ingress
kubectl --context west get ingress -n featbit
kubectl --context east get ingress -n featbit

# Describe pod
kubectl --context west describe pod <pod-name> -n featbit

# View logs
kubectl --context west logs <pod-name> -n featbit -f

# Restart deployment
kubectl --context west rollout restart deployment <deployment-name> -n featbit
```

### Service Management

```powershell
# Stop nginx
Stop-Process -Name nginx

# Start nginx
cd C:\nginx
.\nginx.exe

# Reload nginx configuration
cd C:\nginx
.\nginx.exe -s reload

# Stop port forwards
Get-Process kubectl | Stop-Process

# Check Docker registry
docker ps --filter "name=registry"
```

## Complete Cleanup

To completely remove everything:

```powershell
# Stop nginx
Stop-Process -Name nginx -ErrorAction SilentlyContinue

# Stop port forwards
Get-Process kubectl | Stop-Process -ErrorAction SilentlyContinue

# Delete Minikube clusters
minikube delete -p west
minikube delete -p east

# Stop Docker registry (optional)
docker stop registry
docker rm registry

# Remove nginx (optional)
choco uninstall nginx -y
```

Then manually remove the FeatBit entries from `C:\Windows\System32\drivers\etc\hosts` (requires admin).

## Architecture Details

### Infrastructure Components

Each cluster runs:
- **MongoDB** - Primary data store
- **Redis** - Caching and session management
- **ClickHouse** - Analytics data warehouse
- **Kafka** - Message broker for event streaming
- **Kafka UI** - Web interface for Kafka management

### FeatBit Components

Each cluster runs:
- **UI** - Angular-based web interface (port 8081/8082)
- **API Server** - REST API backend (port 5000/5001)
- **Evaluation Server** - Feature flag evaluation engine (port 5100/5101)
- **Control Plane** - Administrative control services (port 5200)
- **Data Analytics Server** - Analytics processing (port 8200)

### Network Flow

1. Browser requests `http://featbit.west.local`
2. DNS resolves to `127.0.0.1` (via hosts file)
3. Nginx receives request on port 80
4. Nginx proxies to `localhost:8081` (kubectl port-forward)
5. Port forward tunnels to Kubernetes service
6. Service routes to pod
7. Pod serves response

The nginx proxy adds CORS headers automatically, eliminating cross-origin issues.

## License

See repository LICENSE file.

## Support

For issues with:
- **FeatBit functionality**: https://github.com/featbit/featbit
- **Deployment scripts**: Create issue in this repository
- **Minikube**: https://github.com/kubernetes/minikube
- **Nginx**: https://nginx.org/en/docs/

## Contributing

See CONTRIBUTING.md for guidelines.
