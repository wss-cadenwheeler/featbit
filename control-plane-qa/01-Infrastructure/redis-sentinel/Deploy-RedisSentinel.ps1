<#
.SYNOPSIS
    Deploy a per-cluster Redis + Sentinel and point that cluster's FeatBit at it.

.DESCRIPTION
    Gives each cluster (west, east) its OWN HA Redis (1 master + 2 replicas, 3
    Sentinels) in-cluster, and repoints that cluster's FeatBit services
    (api-server, evaluation-server, control-plane) at its own Sentinel via the
    connection string:

        Redis__ConnectionString = featbit-redis:26379,serviceName=mymaster

    No shared redis between DCs. FeatBit needs NO code change — StackExchange.Redis
    2.13.1 resolves the Sentinel master from the `serviceName=` connection string
    (verified: api/eval connect to the Sentinel-elected master and serve flags).

    The api-server repopulates its cluster's redis from MongoDB on startup, so the
    new (empty) redis is filled automatically when api-server rolls.

    Idempotent: re-running upgrades the chart and re-applies the env.

.PARAMETER Contexts        kube contexts (default west, east)
.PARAMETER Namespace       featbit namespace (default featbit)
.PARAMETER ChartVersion    bitnami/redis chart version (default 23.2.12, appVersion 8.2.3)

.NOTES
    Cross-cluster control-plane redis (Redis__Instances__1, the OTHER DC) is left
    as-is here; in BestEffort consistency mode it is non-critical. Wiring it to the
    peer cluster's Sentinel requires cross-cluster exposure (NodePort/LB) and is a
    follow-up. The local instance (Redis__Instances__0) IS pointed at the local
    Sentinel.
#>
[CmdletBinding()]
param(
    [string[]]$Contexts = @("west", "east"),
    [string]$Namespace = "featbit",
    [string]$ChartVersion = "23.2.12"
)
$ErrorActionPreference = "Stop"
function Write-Step { param([string]$M) Write-Host "`n=== $M ===" -ForegroundColor Cyan }
function Write-Ok   { param([string]$M) Write-Host "✓ $M" -ForegroundColor Green }
function Write-Info { param([string]$M) Write-Host "  $M" -ForegroundColor Gray }
function Write-Fail { param([string]$M) Write-Host "✗ $M" -ForegroundColor Red }

$valuesFile = Join-Path $PSScriptRoot "values.yaml"
if (-not (Test-Path $valuesFile)) { Write-Fail "values.yaml not found next to this script"; exit 1 }
$sentinelConn = "featbit-redis:26379,serviceName=mymaster"

& helm repo add bitnami https://charts.bitnami.com/bitnami *> $null
& helm repo update bitnami *> $null

foreach ($ctx in $Contexts) {
    Write-Step "Deploying Redis+Sentinel to '$ctx'"
    & helm upgrade --install featbit-redis bitnami/redis --version $ChartVersion `
        --kube-context $ctx --namespace $Namespace -f $valuesFile --timeout 6m
    if ($LASTEXITCODE -ne 0) { Write-Fail "helm install failed on $ctx"; exit 1 }
    & kubectl --context $ctx -n $Namespace rollout status statefulset/featbit-redis-node --timeout=300s
    if ($LASTEXITCODE -ne 0) { Write-Fail "redis nodes not ready on $ctx"; exit 1 }
    Write-Ok "redis+sentinel ready on $ctx (3 nodes)"

    Write-Step "Pointing $ctx FeatBit at its Sentinel"
    # api-server first: it repopulates redis from MongoDB on startup.
    & kubectl --context $ctx -n $Namespace set env deploy/api-server "Redis__ConnectionString=$sentinelConn" | Out-Null
    & kubectl --context $ctx -n $Namespace set env deploy/control-plane "Redis__Instances__0__ConnectionString=$sentinelConn" | Out-Null
    & kubectl --context $ctx -n $Namespace rollout status deploy/api-server --timeout=180s | Out-Null
    & kubectl --context $ctx -n $Namespace set env deploy/evaluation-server "Redis__ConnectionString=$sentinelConn" | Out-Null
    & kubectl --context $ctx -n $Namespace rollout status deploy/evaluation-server --timeout=180s | Out-Null
    & kubectl --context $ctx -n $Namespace rollout status deploy/control-plane --timeout=180s | Out-Null
    Write-Ok "$ctx api/eval/control-plane -> $sentinelConn"
}

Write-Step "Done"
Write-Info "Each cluster now has its own Redis+Sentinel; no shared redis."
Write-Info "Verify: kubectl --context <c> -n featbit exec featbit-redis-node-0 -c sentinel -- redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster"
