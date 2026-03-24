<#
.SYNOPSIS
    Sets up nginx reverse proxy for FeatBit with DNS-based access.

.DESCRIPTION
    This script performs the following operations:
    1. Installs nginx on Windows via Chocolatey (if not present)
    2. Configures nginx as a reverse proxy for both clusters
    3. Updates Windows hosts file with DNS entries
    4. Updates FeatBit UI deployments with correct URLs
    5. Starts port forwarding and nginx services
    
    DNS Names:
    - featbit.west.local -> West UI
    - featbit-api.west.local -> West API
    - featbit-eval.west.local -> West Evaluation
    - featbit.east.local -> East UI
    - featbit-api.east.local -> East API
    - featbit-eval.east.local -> East Evaluation
    
.PARAMETER NginxPath
    Path to nginx installation. Default: C:\nginx

.PARAMETER SkipNginxInstall
    Skip nginx installation if already installed elsewhere.

.EXAMPLE
    .\Setup-FeatBitProxy.ps1
    Installs and configures nginx with default settings.

.NOTES
    Author: GitHub Copilot
    Date: 2026-03-04
    
    This script requires administrator privileges to:
    - Install nginx via Chocolatey
    - Modify the Windows hosts file
    - Configure Windows Firewall (if needed)
#>

[CmdletBinding()]
param(
    [string]$NginxPath = "C:\nginx",
    [switch]$SkipNginxInstall
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Gray
}

function Write-Warning {
    param([string]$Message)
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

function Write-ErrorMsg {
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Test-Administrator {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    Write-ErrorMsg "This script requires administrator privileges"
    Write-Info "Please run PowerShell as Administrator and try again"
    exit 1
}

Write-Step "Pre-flight Checks"

# Auto-detect nginx installation
$detectedNginxPath = $null
$searchPaths = @(
    "C:\nginx",
    "C:\tools\nginx",
    "C:\tools\nginx-*"
)

foreach ($path in $searchPaths) {
    $resolved = Resolve-Path $path -ErrorAction SilentlyContinue
    if ($resolved) {
        foreach ($resolvedPath in $resolved) {
            if (Test-Path "$resolvedPath\nginx.exe") {
                $detectedNginxPath = $resolvedPath.Path
                break
            }
        }
    }
    if ($detectedNginxPath) { break }
}

if ($detectedNginxPath) {
    $NginxPath = $detectedNginxPath
    Write-Success "Found nginx at $NginxPath"
}

if (-not $SkipNginxInstall) {
    if (-not (Test-Path $NginxPath)) {
        Write-Info "Installing nginx via Chocolatey..."
        try {
            choco install nginx -y
            if ($LASTEXITCODE -ne 0) {
                Write-ErrorMsg "Failed to install nginx"
                exit 1
            }
            Write-Success "Nginx installed"
        } catch {
            Write-ErrorMsg "Failed to install nginx: $_"
            exit 1
        }
    } else {
        Write-Success "Nginx already installed at $NginxPath"
    }
}

Write-Step "Configuring Nginx"

$nginxConfPath = Join-Path $NginxPath "conf\nginx.conf"
$nginxConfDir = Join-Path $NginxPath "conf"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceNginxConf = Join-Path $scriptDir "nginx.conf"

if (-not (Test-Path $nginxConfDir)) {
    Write-Info "Creating nginx conf directory..."
    New-Item -ItemType Directory -Path $nginxConfDir -Force | Out-Null
}

# Create logs directory if it doesn't exist
$nginxLogsDir = Join-Path $NginxPath "logs"
if (-not (Test-Path $nginxLogsDir)) {
    Write-Info "Creating nginx logs directory..."
    New-Item -ItemType Directory -Path $nginxLogsDir -Force | Out-Null
}

if (Test-Path $sourceNginxConf) {
    Write-Info "Copying nginx.conf from script directory..."
    Copy-Item $sourceNginxConf $nginxConfPath -Force
    Write-Success "Nginx configuration copied"
} else {
    Write-ErrorMsg "Could not find nginx.conf in script directory"
    Write-Info "Expected: $sourceNginxConf"
    exit 1
}

Write-Step "Updating Windows Hosts File"

$hostsFile = "C:\Windows\System32\drivers\etc\hosts"
$hostsEntries = @(
    "127.0.0.1 featbit.west.local featbit-api.west.local featbit-eval.west.local",
    "127.0.0.1 featbit.east.local featbit-api.east.local featbit-eval.east.local",
    "127.0.0.1 mongodb-0.west.local mongodb-1.west.local mongodb-2.east.local"
)

$hostsContent = Get-Content $hostsFile -Raw

$needsUpdate = $false
foreach ($entry in $hostsEntries) {
    if ($hostsContent -notmatch [regex]::Escape($entry)) {
        $needsUpdate = $true
        break
    }
}

if ($needsUpdate) {
    Write-Info "Adding DNS entries to hosts file..."
    Add-Content -Path $hostsFile -Value "`n# FeatBit DNS Entries"
    foreach ($entry in $hostsEntries) {
        if ($hostsContent -notmatch [regex]::Escape($entry)) {
            Add-Content -Path $hostsFile -Value $entry
        }
    }
    Write-Success "Hosts file updated"
} else {
    Write-Success "Hosts file already configured"
}

Write-Step "Updating FeatBit UI Deployments"

Write-Info "Updating west UI..."

$westUIYaml = @'
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ui
  namespace: featbit
spec:
  replicas: 1
  selector:
    matchLabels:
      app: ui
  template:
    metadata:
      labels:
        app: ui
    spec:
      containers:
        - env:
            - name: API_URL
              value: http://featbit-api.west.local
            - name: DEMO_URL
              value: https://featbit-samples.vercel.app
            - name: EVALUATION_URL
              value: http://featbit-eval.west.local
          image: host.minikube.internal:5000/featbit/featbit-ui:latest
          name: ui
          ports:
            - containerPort: 80
          resources:
            requests:
              cpu: 100m
              memory: 256Mi
'@

$westUIYaml | kubectl --context west apply -f - | Out-Null
kubectl --context west -n featbit rollout restart deployment/ui | Out-Null
kubectl --context west -n featbit rollout status deployment/ui --timeout=180s | Out-Null
Write-Success "West UI updated"

Write-Info "Updating east UI..."

$eastUIYaml = @'
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ui
  namespace: featbit
spec:
  replicas: 1
  selector:
    matchLabels:
      app: ui
  template:
    metadata:
      labels:
        app: ui
    spec:
      containers:
        - env:
            - name: API_URL
              value: http://featbit-api.east.local
            - name: DEMO_URL
              value: https://featbit-samples.vercel.app
            - name: EVALUATION_URL
              value: http://featbit-eval.east.local
          image: host.minikube.internal:5000/featbit/featbit-ui:latest
          name: ui
          ports:
            - containerPort: 80
          resources:
            requests:
              cpu: 100m
              memory: 256Mi
'@

$eastUIYaml | kubectl --context east apply -f - | Out-Null
kubectl --context east -n featbit rollout restart deployment/ui | Out-Null
kubectl --context east -n featbit rollout status deployment/ui --timeout=180s | Out-Null
Write-Success "East UI updated"

Write-Info "Waiting for UI pods to restart..."
Start-Sleep -Seconds 15

Write-Step "Starting Services"

Write-Info "Starting unified port forward manager..."

# Start port forwards in a minimized window
Start-Process powershell -ArgumentList @(
    "-NoExit",
    "-WindowStyle", "Minimized",
    "-File", (Join-Path $scriptDir "Start-PortForwards.ps1")
) -WindowStyle Minimized

Start-Sleep -Seconds 10

Write-Success "Port forward manager started (check minimized window)"

Write-Info "Testing nginx configuration..."
Set-Location $NginxPath
$testResult = & .\nginx.exe -t 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Success "Nginx configuration is valid"
} else {
    Write-ErrorMsg "Nginx configuration test failed:"
    Write-Host $testResult
    exit 1
}

Write-Info "Starting nginx..."
$nginxRunning = Get-Process nginx -ErrorAction SilentlyContinue
if ($nginxRunning) {
    Write-Info "Nginx is already running, reloading configuration..."
    & "$NginxPath\nginx.exe" -s reload | Out-Null
} else {
    Start-Process -FilePath "$NginxPath\nginx.exe" -WorkingDirectory $NginxPath -WindowStyle Hidden
}

Start-Sleep -Seconds 3

$nginxRunning = Get-Process nginx -ErrorAction SilentlyContinue
if ($nginxRunning) {
    Write-Success "Nginx is running"
} else {
    Write-ErrorMsg "Failed to start or reload nginx"
    exit 1
}

Write-Step "Setup Complete"

Write-Host ""
Write-Host "Access FeatBit using DNS names:" -ForegroundColor Green
Write-Host "  West Cluster: http://featbit.west.local" -ForegroundColor Cyan
Write-Host "  East Cluster: http://featbit.east.local" -ForegroundColor Cyan

Write-Host ""
Write-Host "Direct API Access:" -ForegroundColor Yellow
Write-Host "  West API: http://featbit-api.west.local" -ForegroundColor Gray
Write-Host "  East API: http://featbit-api.east.local" -ForegroundColor Gray

Write-Host ""
Write-Host "Management Commands:" -ForegroundColor Yellow
Write-Host "  Stop nginx:    Stop-Process -Name nginx" -ForegroundColor Gray
Write-Host "  Restart nginx: cd C:\nginx; .\nginx.exe -s reload" -ForegroundColor Gray
Write-Host "  View logs:     Get-Content C:\nginx\logs\error.log -Tail 50" -ForegroundColor Gray

Write-Host ""
