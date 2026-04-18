<#
.SYNOPSIS
    Seeds infrastructure images into the local Docker registry (localhost:5000).

.DESCRIPTION
    Pulls each infrastructure image from Docker Hub (with retry), then tags and
    pushes it to localhost:5000 so that Deploy-FeatBitClusters.ps1 can route all
    infra image pulls through the local registry instead of Docker Hub.

    Run this script once per machine before running Deploy-FeatBitClusters.ps1 with
    a blank deployment.env. The deploy script automatically detects seeded images and
    routes docker-compose and Kubernetes infra pulls through localhost:5000 /
    host.minikube.internal:5000, avoiding Docker Hub rate limits entirely.

.PARAMETER RepositoryRoot
    Root of the featbit repository. Defaults to the parent of the script's directory.

.PARAMETER InfraImageMapFile
    Path to the infra-image-map JSON file. Defaults to kubernetes/infra-image-map.json
    under RepositoryRoot.

.PARAMETER Force
    Re-pull and re-push images even if they are already present in localhost:5000.

.EXAMPLE
    .\Seed-LocalRegistry.ps1

.EXAMPLE
    .\Seed-LocalRegistry.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot    = (Split-Path -Parent $PSScriptRoot),
    [string]$InfraImageMapFile = "",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $InfraImageMapFile) {
    $InfraImageMapFile = Join-Path $RepositoryRoot "kubernetes\infra-image-map.json"
}

# ── helpers ────────────────────────────────────────────────────────────────────

function Write-Step    { param([string]$Message) Write-Host "`n=== $Message ===" -ForegroundColor Cyan }
function Write-Info    { param([string]$Message) Write-Host "  $Message" -ForegroundColor Gray }
function Write-Success { param([string]$Message) Write-Host "  ✓ $Message" -ForegroundColor Green }
function Write-Warn    { param([string]$Message) Write-Host "  ⚠ $Message" -ForegroundColor Yellow }
function Write-Err     { param([string]$Message) Write-Host "  ✗ $Message" -ForegroundColor Red }

function Invoke-DockerPull {
    param(
        [string]$Image,
        [int]$MaxAttempts  = 3,
        [int]$DelaySeconds = 15
    )
    for ($i = 1; $i -le $MaxAttempts; $i++) {
        docker pull $Image
        if ($LASTEXITCODE -eq 0) { return }
        if ($i -lt $MaxAttempts) {
            Write-Warn "Pull attempt $i/$MaxAttempts failed for '$Image'. Retrying in ${DelaySeconds}s..."
            Start-Sleep -Seconds $DelaySeconds
        }
    }
    throw "All $MaxAttempts pull attempts failed for '$Image'."
}

# ── registry ───────────────────────────────────────────────────────────────────

Write-Step "Ensuring Local Registry"

$registryRunning = docker ps --filter "name=registry" --filter "status=running" --format "{{.Names}}" |
                   Select-String -Pattern "^registry$"
if (-not $registryRunning) {
    Write-Info "Starting registry container..."
    try {
        docker start registry 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Info "Creating new registry container..."
            docker run -d --restart=always --name registry -p 5000:5000 registry:2
        }
        Start-Sleep -Seconds 3
        Write-Success "Registry started"
    }
    catch {
        Write-Err "Failed to start registry: $_"
        exit 1
    }
}
else {
    Write-Success "Registry is already running"
}

# ── load image map ─────────────────────────────────────────────────────────────

Write-Step "Loading Image Map"

if (-not (Test-Path $InfraImageMapFile)) {
    Write-Err "Infra image map not found: $InfraImageMapFile"
    exit 1
}

$mapJson  = Get-Content $InfraImageMapFile -Raw | ConvertFrom-Json
$imageMap = @{}
foreach ($key in $mapJson.images.PSObject.Properties.Name) {
    $imageMap[$key] = $mapJson.images.$key
}
Write-Success "Loaded $($imageMap.Count) images from $(Split-Path $InfraImageMapFile -Leaf)"

# ── seed images ────────────────────────────────────────────────────────────────

Write-Step "Seeding Images into localhost:5000"

$seeded  = [System.Collections.Generic.List[string]]::new()
$skipped = [System.Collections.Generic.List[string]]::new()
$failed  = [System.Collections.Generic.List[string]]::new()

foreach ($sourceImage in $imageMap.Keys) {
    $localTag = "localhost:5000/$sourceImage"

    if (-not $Force) {
        $alreadyPresent = docker images --format "{{.Repository}}:{{.Tag}}" |
                          Select-String -SimpleMatch $localTag
        if ($alreadyPresent) {
            Write-Info "Already present, skipping: $localTag"
            $skipped.Add($sourceImage)
            continue
        }
    }

    Write-Info "Pulling  $sourceImage ..."
    try {
        Invoke-DockerPull -Image $sourceImage
    }
    catch {
        Write-Warn "Could not pull '$sourceImage': $_"
        $failed.Add($sourceImage)
        continue
    }

    docker tag $sourceImage $localTag
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "Failed to tag $sourceImage → $localTag"
        $failed.Add($sourceImage)
        continue
    }

    Write-Info "Pushing  $localTag ..."
    docker push $localTag
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "Failed to push $localTag"
        $failed.Add($sourceImage)
        continue
    }

    Write-Success $localTag
    $seeded.Add($sourceImage)
}

# ── summary ────────────────────────────────────────────────────────────────────

Write-Step "Summary"
Write-Info "Seeded : $($seeded.Count)   Skipped: $($skipped.Count)   Failed: $($failed.Count)"

if ($failed.Count -gt 0) {
    Write-Err "The following images could not be seeded:"
    foreach ($img in $failed) { Write-Info "  - $img" }
    exit 1
}

Write-Success "All infra images available in localhost:5000"
Write-Info ""
Write-Info "Run Deploy-FeatBitClusters.ps1 as usual — it will automatically route infra"
Write-Info "image pulls through the local registry when no CustomImageRegistry is set."
