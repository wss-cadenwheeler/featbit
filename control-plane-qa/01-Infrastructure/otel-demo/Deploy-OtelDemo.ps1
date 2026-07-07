<#
.SYNOPSIS
    Deploy the FeatBit-instrumented OpenTelemetry Demo to the test clusters.

.DESCRIPTION
    Installs the upstream otel-demo Helm chart (pinned to the chart version whose
    appVersion matches our custom images) with two overlays:
      values-min.yaml      - minimal-footprint resource caps (laptop capacity)
      values-featbit.yaml  - custom Harbor images + FeatBit eval URLs
    The FeatBit env SECRET is injected at deploy time (--set-string), never stored
    in git.

    For each FeatBit-instrumented component, a hostAlias is added so the pod
    resolves the eval hostname (featbit-eval.127.0.0.1.sslip.io) to the cluster's
    host gateway, where the host proxy load-balances to the eval-servers
    (west primary / east failover — active/passive, west active). This avoids
    modifying shared cluster DNS (CoreDNS).

.PARAMETER FeatBitSecret
    The environment SERVER SDK secret (from Provision-FeatBitFlags.py). Defaults to
    the FEATBIT_ENV_SECRET environment variable.

.PARAMETER Contexts        kube contexts to deploy to (default west, east).
.PARAMETER Namespace       target namespace (default otel-demo).
.PARAMETER ChartVersion    otel-demo chart version (default 0.40.9 == appVersion 2.2.0).
.PARAMETER FeatBitComponents  components wired to FeatBit needing the eval hostAlias.
.PARAMETER EvalHost        eval hostname the SDK URLs use (must match values-featbit.yaml).

.EXAMPLE
    pwsh ./Deploy-OtelDemo.ps1 -FeatBitSecret nBAn8sn86Uq-...
    pwsh ./Deploy-OtelDemo.ps1 -Contexts west -FeatBitSecret $env:FEATBIT_ENV_SECRET
#>
[CmdletBinding()]
param(
    # JSON emitted by Provision-FeatBitFlags.py: [{component, server_secret, ...}].
    # Each component is its own FeatBit project with its own server secret.
    [string]$ProvisionFile,
    [string[]]$Contexts = @("west", "east"),
    [string]$Namespace = "otel-demo",
    [string]$ChartVersion = "0.40.9",
    [string[]]$FeatBitComponents = @("recommendation", "product-catalog", "cart", "ad", "payment",
                                     "accounting", "checkout", "fraud-detection", "llm", "frontend"),
    [string]$EvalHost = "featbit-eval.127.0.0.1.sslip.io"
)

$ErrorActionPreference = "Stop"
function Write-Step { param([string]$M) Write-Host "`n=== $M ===" -ForegroundColor Cyan }
function Write-Ok   { param([string]$M) Write-Host "✓ $M" -ForegroundColor Green }
function Write-Info { param([string]$M) Write-Host "  $M" -ForegroundColor Gray }
function Write-Fail { param([string]$M) Write-Host "✗ $M" -ForegroundColor Red }

# Tracks whether any per-component step failed so the script's own exit code
# reflects the whole run, not just the last kubectl call (a failure earlier in
# the per-component loop must not be silently overwritten/masked by later
# successful calls).
$script:hadFailure = $false

$root       = $PSScriptRoot
$valuesMin  = Join-Path $root "values-min.yaml"
$valuesFb   = Join-Path $root "values-featbit.yaml"
foreach ($f in @($valuesMin, $valuesFb)) { if (-not (Test-Path $f)) { Write-Fail "Missing $f"; exit 1 } }

# Map component -> server secret from the provision file.
if (-not $ProvisionFile) { $ProvisionFile = Join-Path $root "build/featbit-otel-provision.json" }
if (-not (Test-Path $ProvisionFile)) {
    Write-Fail "Provision file not found: $ProvisionFile"
    Write-Info "Run: python3 Provision-FeatBitFlags.py --api http://featbit-api-west.127.0.0.1.sslip.io:8080 > $ProvisionFile"
    exit 1
}
$secretByComp = @{}
foreach ($r in (Get-Content $ProvisionFile -Raw | ConvertFrom-Json)) { $secretByComp[$r.component] = $r.server_secret }
foreach ($comp in $FeatBitComponents) {
    if (-not $secretByComp[$comp]) { Write-Fail "No server secret for '$comp' in $ProvisionFile (provision it first)"; exit 1 }
}

Write-Step "Helm repo"
& helm repo add open-telemetry https://open-telemetry.github.io/opentelemetry-helm-charts *> $null
& helm repo update open-telemetry *> $null
Write-Ok "open-telemetry/opentelemetry-demo $ChartVersion"

function Get-HostGatewayIp([string]$ctx) {
    # CoreDNS maps host.minikube.internal -> the cluster's host gateway IP.
    $corefile = & kubectl --context $ctx -n kube-system get cm coredns -o jsonpath='{.data.Corefile}'
    foreach ($line in ($corefile -split "`n")) {
        if ($line -match '^\s*([0-9.]+)\s+host\.minikube\.internal\s*$') { return $Matches[1] }
    }
    throw "Could not resolve host gateway IP (host.minikube.internal) for context $ctx"
}

foreach ($ctx in $Contexts) {
    Write-Step "Deploying otel-demo to '$ctx'"

    # Each FeatBit component's OWN secret, appended as its envOverrides[2]
    # (values-featbit.yaml supplies [0]=EVENT_URL, [1]=STREAMING_URL).
    $secretArgs = @()
    foreach ($comp in $FeatBitComponents) {
        $secretArgs += @(
            "--set-string", "components.$comp.envOverrides[2].name=FEATBIT_ENV_SECRET",
            "--set-string", "components.$comp.envOverrides[2].value=$($secretByComp[$comp])"
        )
    }

    & helm upgrade --install otel-demo open-telemetry/opentelemetry-demo `
        --version $ChartVersion --kube-context $ctx `
        --namespace $Namespace --create-namespace `
        -f $valuesMin -f $valuesFb @secretArgs --timeout 10m
    if ($LASTEXITCODE -ne 0) { Write-Fail "helm upgrade failed for $ctx"; exit 1 }
    Write-Ok "helm release applied to $ctx"

    # hostAlias so FeatBit-wired pods resolve the eval hostname to the host proxy.
    $gw = Get-HostGatewayIp $ctx
    Write-Info "host gateway for ${ctx}: $gw  ($EvalHost -> $gw)"
    foreach ($comp in $FeatBitComponents) {
        $patch = @{ spec = @{ template = @{ spec = @{ hostAliases = @(@{ ip = $gw; hostnames = @($EvalHost) }) } } } } | ConvertTo-Json -Depth 10 -Compress
        & kubectl --context $ctx -n $Namespace patch deployment $comp --type merge -p $patch *> $null
        if ($LASTEXITCODE -eq 0) { Write-Ok "hostAlias patched on $comp ($ctx)" }
        else { Write-Fail "hostAlias patch failed on $comp ($ctx)"; $script:hadFailure = $true }
        & kubectl --context $ctx -n $Namespace rollout status deployment/$comp --timeout=180s *> $null
        if ($LASTEXITCODE -eq 0) { Write-Ok "rollout ready: $comp ($ctx)" }
        else { Write-Fail "rollout not ready: $comp ($ctx)"; $script:hadFailure = $true }
    }
    Write-Ok "$ctx deploy complete"
}

Write-Step "Done"
Write-Info "Access (host proxy): http://featbit.127.0.0.1.sslip.io:8080"
Write-Info "Recommendation flags: otel-recommendation project in FeatBit"

if ($script:hadFailure) {
    Write-Fail "One or more components failed to patch/roll out (see ✗ lines above) — exiting non-zero."
    exit 1
}
