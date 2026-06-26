<#
.SYNOPSIS
    Full quickstart wizard for FeatBit Pro — native Ubuntu Linux.

.DESCRIPTION
    Guides you through every step to get a working two-cluster FeatBit Pro
    environment on a native Ubuntu Linux machine (not inside WSL2).

    The script is resumable: completed phases are saved to
    .quickstart-state-ubuntu.json in this directory and skipped on subsequent
    runs, so you can re-run after an interruption and pick up where you left off.

    Use -InstallK6 to install Grafana k6 as an optional prerequisite.
    It is optional; required for full coverage of cp09-pod-heartbeats scenario.

    Phases (in order):
      1. ensure-pwsh        — verify PowerShell 7+ is active on Linux
      2. system-prereqs     — install git via apt                     [root]
      3. dev-tools          — Docker Engine, Minikube, kubectl, k9s (optional)
      3b. install-k6        — optional Grafana k6 install for cp09-pod-heartbeats
      4. repo-setup         — clone repo, checkout control-plane, configure deployment.env
      4b. collect-creds     — prompt for registry credentials early (so you can walk away)
      5. build-images       — build FeatBit images and push to localhost:5000  (~10-15 min)
      6. proxy-first-run    — first run of Setup-FeatBitProxy.ps1     [root]
      7. deploy-clusters    — Deploy-FeatBitClusters.ps1 Advanced + MongoDB   (~20 min)
      7b. verify-pull-backoff — assert no ImagePullBackOff in west/east clusters
      8. proxy-second-run   — second run of Setup-FeatBitProxy.ps1    [root]
      9. port-forwards      — instructions + launch Start-PortForwards.ps1
     10. mongo-replica-set  — Initialize-MongoDBReplicaSet.ps1

    Run the wizard as your normal user (NOT sudo). Phases that need root
    (apt install, nginx, /etc/hosts) call sudo themselves. Minikube's docker
    driver refuses to run as root, so the wizard refuses too.

.PARAMETER InstallK6
    Installs Grafana k6 as an optional prerequisite; required for full
    coverage of cp09-pod-heartbeats scenario.

.PARAMETER Reset
    Clears the saved progress state and starts from the beginning.

.PARAMETER SkipOptional
    Skips optional installations (k9s).

.PARAMETER SkipRepoSetup
    Skip Phase 4 — don't switch branches, touch deployment.env, or prompt for
    clone location. Use this when you're actively developing in this repo and
    managing its state yourself. The skip is not persisted; omit the flag on
    a future run to re-enable the phase.

.EXAMPLE
    pwsh ./Quickstart-Ubuntu.ps1
    Run (or resume) the wizard. You will be prompted for sudo as needed.

.EXAMPLE
    pwsh ./Quickstart-Ubuntu.ps1 -InstallK6
    Run (or resume) the wizard and install k6 for full cp09-pod-heartbeats coverage.

.EXAMPLE
    pwsh ./Quickstart-Ubuntu.ps1 -Reset
    Wipe saved progress and start over.

.EXAMPLE
    pwsh ./Quickstart-Ubuntu.ps1 -Reset -SkipRepoSetup
    Start over, but leave the repository (branch, deployment.env) alone.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$InstallK6,
    [switch]$Reset,
    [switch]$SkipOptional,
    [switch]$SkipRepoSetup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Holds registry credentials collected by Invoke-CollectCreds for the lifetime
# of this process. Initialized so strict-mode code that references it before
# the collect-creds phase runs (e.g. when collect-creds is skipped on resume)
# doesn't error with "variable cannot be retrieved because it has not been set".
$script:CollectedRegistryCred = $null

if (-not $IsLinux) {
    Write-Host ""
    Write-Host "  ✗ Quickstart-Ubuntu.ps1 must be run on a Linux system." -ForegroundColor Red
    Write-Host ""
    if ($IsWindows) {
        Write-Host "  You appear to be on Windows. Use one of the Windows quickstart scripts instead:" -ForegroundColor Yellow
        Write-Host "    • ..\windows-wsl\Quickstart-WSL.ps1      — deploy inside WSL2" -ForegroundColor Gray
        Write-Host "    • ..\windows-hyperv\Quickstart-HyperV.ps1 — deploy with Hyper-V" -ForegroundColor Gray
    } else {
        Write-Host "  This script expects a Linux environment (\$IsLinux = true)." -ForegroundColor Gray
    }
    Write-Host ""
    exit 1
}

# Minikube's docker driver refuses root, and state written as root leaves stale
# certs in /root/.minikube and container volumes — subsequent non-root runs fail
# with "certificate signed by unknown authority". Require a normal user and
# escalate per-command via sudo.
if (((id -u) -as [int]) -eq 0) {
    Write-Host ""
    Write-Host "  ✗ Do not run this wizard as root (or via sudo)." -ForegroundColor Red
    Write-Host ""
    Write-Host "  Minikube's docker driver refuses root, and running as root writes" -ForegroundColor Gray
    Write-Host "  state to /root/.minikube that corrupts later non-root runs with" -ForegroundColor Gray
    Write-Host "  'certificate signed by unknown authority' errors." -ForegroundColor Gray
    Write-Host ""
    Write-Host "  Re-run as your normal user:" -ForegroundColor Yellow
    Write-Host "    pwsh $PSCommandPath" -ForegroundColor White
    Write-Host ""
    Write-Host "  The wizard will invoke sudo itself for phases that need it." -ForegroundColor Gray
    Write-Host ""
    exit 1
}

$script:StateFile  = Join-Path $PSScriptRoot ".quickstart-state-ubuntu.json"
$script:SiblingDir = Split-Path $PSScriptRoot -Parent  # control-plane-qa/

# ── Console helpers ────────────────────────────────────────────────────────────

function Write-Step    { param([string]$M) Write-Host "`n════════════════════════════════════════════════════════════" -ForegroundColor DarkCyan
                         Write-Host "  $M" -ForegroundColor Cyan
                         Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor DarkCyan }
function Write-Success { param([string]$M) Write-Host "  ✓ $M" -ForegroundColor Green }
function Write-Info    { param([string]$M) Write-Host "  $M" -ForegroundColor Gray }
function Write-Warn    { param([string]$M) Write-Host "  ⚠ $M" -ForegroundColor Yellow }
function Write-Fail    { param([string]$M) Write-Host "  ✗ $M" -ForegroundColor Red }
function Write-Banner  {
    Write-Host ""
    Write-Host "  ███████╗███████╗ █████╗ ████████╗██████╗ ██╗████████╗" -ForegroundColor Cyan
    Write-Host "  ██╔════╝██╔════╝██╔══██╗╚══██╔══╝██╔══██╗██║╚══██╔══╝" -ForegroundColor Cyan
    Write-Host "  █████╗  █████╗  ███████║   ██║   ██████╔╝██║   ██║   " -ForegroundColor Cyan
    Write-Host "  ██╔══╝  ██╔══╝  ██╔══██║   ██║   ██╔══██╗██║   ██║   " -ForegroundColor Cyan
    Write-Host "  ██║     ███████╗██║  ██║   ██║   ██████╔╝██║   ██║   " -ForegroundColor Cyan
    Write-Host "  ╚═╝     ╚══════╝╚═╝  ╚═╝   ╚═╝   ╚═════╝ ╚═╝   ╚═╝   " -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Quickstart Wizard — Ubuntu Linux (native)" -ForegroundColor White
    Write-Host "  This script is resumable. Re-run it to continue from the last phase." -ForegroundColor Gray
    Write-Host ""
}

# ── State management ──────────────────────────────────────────────────────────

function Get-State {
    if (Test-Path $script:StateFile) {
        return Get-Content $script:StateFile -Raw | ConvertFrom-Json
    }
    return [PSCustomObject]@{
        completedPhases = @()
        lastUpdated     = ""
    }
}

function Save-State([PSCustomObject]$State) {
    $State.lastUpdated = (Get-Date -Format "o")
    $State | ConvertTo-Json -Depth 5 | Set-Content $script:StateFile -Encoding UTF8
}

function Test-PhaseComplete([PSCustomObject]$State, [string]$Phase) {
    return $State.completedPhases -contains $Phase
}

function Complete-Phase([PSCustomObject]$State, [string]$Phase) {
    if ($State.completedPhases -notcontains $Phase) {
        $State.completedPhases = @($State.completedPhases) + $Phase
    }
    Save-State $State
}

$k6Helper = Join-Path $script:SiblingDir "Install-K6Prerequisite.ps1"
if (-not (Test-Path $k6Helper)) { throw "Install-K6Prerequisite.ps1 not found at $k6Helper" }
. $k6Helper

# ── Pause helpers ─────────────────────────────────────────────────────────────

function Wait-UserConfirm([string]$Prompt = "Press Enter to continue...") {
    Write-Host ""
    Write-Host "  ► $Prompt" -ForegroundColor Yellow -NoNewline
    $null = Read-Host
}

function Wait-UserChoice([string]$Prompt, [string[]]$Choices) {
    do {
        Write-Host "  ► $Prompt [$(($Choices | ForEach-Object { $_.ToUpper() }) -join '/')] " -ForegroundColor Yellow -NoNewline
        $r = (Read-Host).Trim().ToLower()
    } while ($r -notin $Choices)
    return $r
}

function Wait-DockerReady {
    param(
        [int]$TimeoutSeconds = 180,
        [int]$IntervalSeconds = 3
    )
    Write-Host ""
    Write-Host "  ► Waiting for Docker daemon to become ready (up to $TimeoutSeconds s)..." -ForegroundColor Yellow

    & docker info --format '{{.ServerVersion}}' *>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Docker daemon is already running." -ForegroundColor Green
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt  = 0
    while ((Get-Date) -lt $deadline) {
        $attempt++
        & docker info --format '{{.ServerVersion}}' *>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Docker daemon is ready (after $attempt poll$(if ($attempt -eq 1) { '' } else { 's' }))." -ForegroundColor Green
            return
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
    throw "Docker daemon did not become ready within $TimeoutSeconds seconds. Start the Docker daemon (e.g. 'sudo systemctl start docker' or 'sudo service docker start') and re-run this Quickstart."
}

# ── Phase implementations ─────────────────────────────────────────────────────

function Invoke-EnsurePwsh {
    Write-Step "Phase 1 — Verify PowerShell 7+"
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        Write-Fail "This script must run in PowerShell 7+.  You are running $($PSVersionTable.PSVersion)."
        Write-Info ""
        Write-Info "Install PowerShell 7 on Ubuntu:"
        Write-Info "  sudo snap install powershell --classic"
        Write-Info "Or follow Microsoft's apt-based instructions at:"
        Write-Info "  https://learn.microsoft.com/powershell/scripting/install/install-ubuntu"
        Write-Info ""
        Write-Info "Then open a new 'pwsh' session and re-run this script."
        exit 1
    }
    Write-Success "PowerShell $($PSVersionTable.PSVersion) — OK"
}

function Invoke-SystemPrereqs([PSCustomObject]$State) {
    Write-Step "Phase 2 — System Prerequisites (git)"

    $gitInstalled = Get-Command git -ErrorAction SilentlyContinue
    if ($gitInstalled) {
        Write-Success "Git is already installed ($((git --version) -replace 'git version ', ''))"
    } else {
        Write-Info "Installing git via sudo apt-get..."
        if ($PSCmdlet.ShouldProcess("git", "sudo apt-get install")) {
            & sudo apt-get update
            if ($LASTEXITCODE -ne 0) { throw "sudo apt-get update failed" }
            & sudo apt-get install -y git
            if ($LASTEXITCODE -ne 0) { throw "git installation failed" }
        }
        Write-Success "Git installed"
    }

    Complete-Phase $State "system-prereqs"
    Write-Success "System prerequisites complete"
}

function Invoke-DevTools([PSCustomObject]$State) {
    Write-Step "Phase 3 — Developer Tools (Docker, Minikube, kubectl)"

    Write-Info "Calling Install-Prerequisites.ps1 (Linux mode: apt + direct downloads)..."
    $prereqScript = Join-Path $script:SiblingDir "Install-Prerequisites.ps1"
    if (-not (Test-Path $prereqScript)) { throw "Install-Prerequisites.ps1 not found at $prereqScript" }
    & $prereqScript
    if ($LASTEXITCODE -ne 0) { throw "Install-Prerequisites.ps1 failed" }

    if (-not $SkipOptional) {
        $k9s = Get-Command k9s -ErrorAction SilentlyContinue
        if ($k9s) {
            Write-Success "k9s is already installed"
        } elseif (Get-Command snap -ErrorAction SilentlyContinue) {
            Write-Info "Installing k9s via sudo snap (optional, useful for troubleshooting)..."
            if ($PSCmdlet.ShouldProcess("k9s", "sudo snap install")) {
                & sudo snap install k9s
                if ($LASTEXITCODE -eq 0) { Write-Success "k9s installed" }
                else { Write-Warn "sudo snap install k9s exited $LASTEXITCODE — skipping" }
            }
        } else {
            Write-Warn "snap not available — skipping k9s. See https://k9scli.io/ for install options."
        }
    }

    Complete-Phase $State "dev-tools"
    Write-Success "Developer tools ready"
    Write-Warn "If Docker Engine was just installed, you must start a new shell session before Docker"
    Write-Warn "works without sudo (docker group membership takes effect on next login)."
    Wait-DockerReady
}

function Invoke-RepoSetup([PSCustomObject]$State) {
    Write-Step "Phase 4 — Repository Setup"

    # ubuntu → control-plane-qa → repo root
    $repoRoot = $null
    $candidate = Split-Path -Parent $script:SiblingDir
    if (Test-Path (Join-Path $candidate ".git")) {
        $repoRoot = $candidate
        Write-Success "Already inside repo: $repoRoot"
    } else {
        Write-Info "Repository not found at expected location."
        $defaultBase = Join-Path $HOME "source"
        Write-Info "Enter the directory where you want to clone the repository"
        Write-Info "(default: $defaultBase)"
        Write-Host "  ► Clone location: " -ForegroundColor Yellow -NoNewline
        $cloneBase = (Read-Host).Trim()
        if (-not $cloneBase) { $cloneBase = $defaultBase }
        if (-not (Test-Path $cloneBase)) { New-Item $cloneBase -ItemType Directory -Force | Out-Null }

        $repoDir = Join-Path $cloneBase "featbit"
        if (-not (Test-Path $repoDir)) {
            Write-Info "Cloning repository..."
            git clone https://github.com/wss-cadenwheeler/featbit "$repoDir"
            if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
        }
        $repoRoot = $repoDir
        Write-Success "Repository at $repoRoot"
    }

    Push-Location $repoRoot
    try {
        $currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
        if ($currentBranch -ne "control-plane") {
            Write-Info "Switching to control-plane branch..."
            git fetch origin control-plane
            git checkout control-plane
            if ($LASTEXITCODE -ne 0) { throw "Failed to checkout control-plane" }
        }
        Write-Success "On branch: control-plane"
    } finally {
        Pop-Location
    }

    $qaDir  = Join-Path $repoRoot "control-plane-qa"
    $envDir = Join-Path $qaDir "01-Infrastructure"
    $envDst = Join-Path $envDir "deployment.env"
    $envEx  = Join-Path $envDir "deployment.env.example"
    if (-not (Test-Path $envDst)) {
        if (Test-Path $envEx) {
            Copy-Item $envEx $envDst
            Write-Success "Created deployment.env from example"
        } else {
            Write-Warn "deployment.env.example not found — create deployment.env manually in $envDir"
        }
    } else {
        Write-Success "deployment.env already exists"
    }

    Write-Warn "IMPORTANT: Review and configure deployment.env before continuing."
    Write-Info "File path: $envDst"
    Write-Info ""
    Write-Info "Key settings to verify:"
    Write-Info "  • CUSTOM_IMAGE_REGISTRY  — leave blank to pull from Docker Hub"
    Write-Info "  • WEST_CPUS / WEST_MEMORY, EAST_CPUS / EAST_MEMORY  — match your machine's capacity"
    Write-Info "  • DEPLOYMENT_MODE        — set to Advanced for this quickstart"
    Write-Info "  • DATABASE_PROVIDER      — set to MongoDb for this quickstart"
    Write-Info ""
    $editor = if ($env:EDITOR) { $env:EDITOR } else { "nano" }
    Write-Info "Edit it now with your preferred editor, e.g.:"
    Write-Info "  $editor $envDst"
    Wait-UserConfirm "Press Enter when you have finished configuring deployment.env..."

    Complete-Phase $State "repo-setup"
    Write-Success "Repository configured"
}

function Invoke-CollectCreds([PSCustomObject]$State) {
    Write-Step "Phase 4b — Collect Registry Credentials"
    Write-Info "Asking for any required credentials NOW so the long deploy phase can run unattended."
    Write-Info ""

    $importScript = Join-Path $script:SiblingDir "Import-DeploymentEnv.ps1"
    if (-not (Test-Path $importScript)) {
        Write-Warn "Import-DeploymentEnv.ps1 not found at $importScript — skipping credential pre-flight."
        return
    }

    try { $envParams = & $importScript } catch {
        Write-Warn "Import-DeploymentEnv.ps1 failed: $_"
        return
    }

    $registry = $envParams['CustomImageRegistry']
    $insecure = $envParams['InsecureCustomRegistry']
    $cred     = $envParams['CustomRegistryCredential']

    if (-not $registry) {
        Write-Info "No CUSTOM_IMAGE_REGISTRY configured — credential prompt skipped."
    }
    elseif ($insecure) {
        Write-Info "INSECURE_CUSTOM_REGISTRY=true — TLS bypass enabled, credential prompt skipped."
    }
    elseif ($cred) {
        Write-Info "Credentials present in deployment.env — no prompt needed."
    }
    else {
        Write-Info "Prompting for '$registry' credentials. Press Enter at both prompts to skip if the registry is anonymous."
        $script:CollectedRegistryCred = Get-Credential -Message "Registry credentials for $registry (Enter to skip)"
        if ($script:CollectedRegistryCred -and -not [string]::IsNullOrWhiteSpace($script:CollectedRegistryCred.UserName)) {
            Write-Success "Captured credentials for $($script:CollectedRegistryCred.UserName)@$registry — you can walk away now."
        } else {
            $script:CollectedRegistryCred = $null
            Write-Warn "No credentials provided. Pods may fail with 'unauthorized' if '$registry' requires login."
        }
    }
}

function Invoke-BuildImages([PSCustomObject]$State) {
    Write-Step "Phase 5 — Build and Push FeatBit Images  (~10-15 minutes)"
    Write-Info "This builds all 5 FeatBit service images from source and pushes them"
    Write-Info "to the local Docker registry at localhost:5000."
    Write-Info ""

    $initScript = Join-Path $script:SiblingDir "Initialize-LocalRegistry.ps1"
    if (-not (Test-Path $initScript)) { throw "Initialize-LocalRegistry.ps1 not found at $initScript" }

    if ($PSCmdlet.ShouldProcess("FeatBit images", "Build and push to localhost:5000")) {
        & $initScript
        if ($LASTEXITCODE -ne 0) { throw "Initialize-LocalRegistry.ps1 failed" }
    }

    Complete-Phase $State "build-images"
    Write-Success "All FeatBit images built and pushed to localhost:5000"
}

function Invoke-ProxyFirstRun([PSCustomObject]$State) {
    Write-Step "Phase 6 — Nginx Proxy First Run"

    Write-Info "Running Setup-FeatBitProxy.ps1 via sudo pwsh (first run)."
    Write-Warn "Some failures on the first run are expected — that is normal."
    Write-Info "The proxy will be fully configured after the second run (Phase 8)."
    Write-Info ""

    $proxyScript = Join-Path $script:SiblingDir "Setup-FeatBitProxy.ps1"
    if (-not (Test-Path $proxyScript)) { throw "Setup-FeatBitProxy.ps1 not found at $proxyScript" }

    if ($PSCmdlet.ShouldProcess("nginx proxy", "First run setup")) {
        & sudo pwsh -File $proxyScript
        if ($LASTEXITCODE -ne 0) {
            Write-Warn "Setup-FeatBitProxy.ps1 exited with code $LASTEXITCODE (expected on first run)"
        }
    }

    Complete-Phase $State "proxy-first-run"
    Write-Success "Proxy first run complete"
}

function Repair-ClusterNetwork {
    # Defensive: Initialize-LocalRegistry.ps1 historically created featbit-cluster-network
    # without --subnet, causing Docker to auto-assign one (e.g. 172.18.0.0/16).
    # Deploy-FeatBitClusters.ps1 hard-codes 172.31.0.10 / 172.31.0.20 for the clusters,
    # so an auto-assigned subnet breaks cluster attachment. Detect & fix before deploying.
    $name       = "featbit-cluster-network"
    $wantSubnet = "172.31.0.0/16"

    $exists = & docker network ls --filter "name=^$name$" --format "{{.Name}}" 2>$null
    if (-not $exists) {
        Write-Info "Network '$name' does not exist yet — the deploy script will create it."
        return
    }

    $currentSubnet = ((& docker network inspect $name --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}' 2>$null) | Out-String).Trim()
    if ($currentSubnet -eq $wantSubnet) {
        Write-Success "Network '$name' already has correct subnet $wantSubnet"
        return
    }

    Write-Warn "Network '$name' has subnet '$currentSubnet' — expected '$wantSubnet'. Recreating..."

    $attached = ((& docker network inspect $name --format '{{range .Containers}}{{.Name}} {{end}}' 2>$null) | Out-String).Trim()
    $containers = if ($attached) { $attached -split '\s+' | Where-Object { $_ } } else { @() }

    foreach ($c in $containers) {
        Write-Info "  Disconnecting '$c' from '$name'..."
        & docker network disconnect $name $c 2>$null | Out-Null
    }

    Write-Info "  Removing network '$name'..."
    & docker network rm $name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to remove network '$name' — manual cleanup required" }

    Write-Info "  Creating network '$name' with subnet $wantSubnet..."
    & docker network create --driver bridge --subnet $wantSubnet $name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to create network '$name' with subnet $wantSubnet" }

    foreach ($c in $containers) {
        Write-Info "  Reconnecting '$c' to '$name'..."
        & docker network connect $name $c 2>$null | Out-Null
    }

    # Disconnect/reconnect leaves docker's published-port proxy in a wedged state
    # (listener bound, packets don't reach the container). Restart affected
    # containers so port forwarding is re-initialized.
    foreach ($c in $containers) {
        Write-Info "  Restarting '$c' to refresh port forwarding..."
        & docker restart $c 2>$null | Out-Null
    }

    Write-Success "Network '$name' recreated with subnet $wantSubnet"
}

function Show-DeploymentEnvSummary {
    # Surface the deployment.env values Deploy-FeatBitClusters.ps1 will pick up,
    # so the user can confirm their edits are in effect before a 20-min deploy.
    $envFile = Join-Path $script:SiblingDir "deployment.env"
    if (-not (Test-Path $envFile)) {
        Write-Warn "deployment.env not found at $envFile — script defaults will be used."
        return
    }

    $keys = @{}
    foreach ($line in Get-Content $envFile) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith("#")) { continue }
        $i = $t.IndexOf("=")
        if ($i -lt 1) { continue }
        $keys[$t.Substring(0, $i).Trim()] = $t.Substring($i + 1).Trim()
    }

    Write-Info "Effective deployment.env settings ($envFile):"
    foreach ($k in @("DEPLOYMENT_MODE","DATABASE_PROVIDER","CUSTOM_IMAGE_REGISTRY","MINIKUBE_BASE_IMAGE","WEST_CPUS","WEST_MEMORY","EAST_CPUS","EAST_MEMORY")) {
        if ($keys.ContainsKey($k)) {
            Write-Info ("  {0,-24} = {1}" -f $k, $keys[$k])
        } else {
            Write-Info ("  {0,-24} = (default — not set in deployment.env)" -f $k)
        }
    }
    Write-Info ""
}

function Invoke-DeployClusters([PSCustomObject]$State) {
    Write-Step "Phase 7 — Deploy FeatBit Clusters  (~20 minutes)"
    Write-Info "Creates the west and east Minikube clusters, then deploys FeatBit."
    Write-Info "Deployment mode, database, CPU/memory, and image registry are all"
    Write-Info "driven by deployment.env (no overrides from this wizard)."
    Write-Info ""
    Write-Warn "This is the longest phase. Do not interrupt it."
    Write-Info ""

    Show-DeploymentEnvSummary
    Repair-ClusterNetwork

    $deployScript = Join-Path $script:SiblingDir "Deploy-FeatBitClusters.ps1"
    if (-not (Test-Path $deployScript)) { throw "Deploy-FeatBitClusters.ps1 not found at $deployScript" }

    if ($PSCmdlet.ShouldProcess("west + east clusters", "Deploy FeatBit per deployment.env")) {
        $deployArgs = @{ RecreateClusters = $true }
        if ($script:CollectedRegistryCred) { $deployArgs['CustomRegistryCredential'] = $script:CollectedRegistryCred }
        & $deployScript @deployArgs
        if ($LASTEXITCODE -ne 0) { throw "Deploy-FeatBitClusters.ps1 failed with exit code $LASTEXITCODE" }
    }

    Complete-Phase $State "deploy-clusters"
    Write-Success "Clusters deployed successfully"
}

function Invoke-VerifyPullBackoff([PSCustomObject]$State) {
    Write-Step "Phase 7b — Verify cluster health (no ImagePullBackOff)"
    Write-Info "Polls both clusters for pods stuck in ImagePullBackOff or ErrImagePull."
    Write-Info "This is a belt-and-suspenders check: the deploy script already runs this"
    Write-Info "assertion, but re-running here catches infra drift after a resume."
    Write-Info ""

    $assertScript = Join-Path $script:SiblingDir "Assert-NoImagePullBackoff.ps1"
    if (-not (Test-Path $assertScript)) { throw "Assert-NoImagePullBackoff.ps1 not found at $assertScript" }

    & $assertScript -Contexts @("west","east") -Namespaces @("featbit") -TimeoutSeconds 120 -IntervalSeconds 5
    if ($LASTEXITCODE -ne 0) {
        throw "Pull-backoff verification failed. See Assert-NoImagePullBackoff output above. Re-run this Quickstart after resolving the registry trust / credentials issue."
    }

    Complete-Phase $State "verify-pull-backoff"
    Write-Success "All pods healthy in both clusters — no ImagePullBackOff detected."
}

function Invoke-ProxySecondRun([PSCustomObject]$State) {
    Write-Step "Phase 8 — Nginx Proxy Second Run"

    Write-Info "Running Setup-FeatBitProxy.ps1 via sudo pwsh a second time to apply"
    Write-Info "the cluster endpoints that were created in the previous phase."
    Write-Info ""

    $proxyScript = Join-Path $script:SiblingDir "Setup-FeatBitProxy.ps1"
    if ($PSCmdlet.ShouldProcess("nginx proxy", "Second run setup")) {
        & sudo pwsh -File $proxyScript
        if ($LASTEXITCODE -ne 0) { throw "Setup-FeatBitProxy.ps1 failed on second run" }
    }

    Complete-Phase $State "proxy-second-run"
    Write-Success "Proxy fully configured"
}

function Stop-StalePortForwards {
    # Clears any leftover Start-PortForwards.ps1 supervisors and their kubectl
    # port-forward workers. They're usually root-owned (Setup-FeatBitProxy.ps1
    # starts some under sudo), so we use sudo pkill.
    # Parents must die before children, otherwise the supervisor respawns them.
    $svcCount = @(& pgrep -f 'Start-PortForwards\.ps1' 2>$null).Count
    $pfCount  = @(& pgrep -f 'kubectl.*port-forward' 2>$null).Count

    if ($svcCount -eq 0 -and $pfCount -eq 0) {
        Write-Success "No stale port-forward processes found."
        return
    }

    Write-Info "Cleaning up stale port-forward processes..."
    if ($svcCount -gt 0) {
        Write-Info "  Stopping $svcCount Start-PortForwards supervisor(s) (sudo)..."
        & sudo pkill -f 'Start-PortForwards\.ps1' 2>$null | Out-Null
        Start-Sleep -Seconds 2
    }

    $pfCount = @(& pgrep -f 'kubectl.*port-forward' 2>$null).Count
    if ($pfCount -gt 0) {
        Write-Info "  Stopping $pfCount kubectl port-forward worker(s) (sudo)..."
        & sudo pkill -f 'kubectl.*port-forward' 2>$null | Out-Null
        Start-Sleep -Seconds 1
    }

    $remaining = @(& pgrep -f 'kubectl.*port-forward|Start-PortForwards\.ps1' 2>$null)
    if ($remaining.Count -gt 0) {
        Write-Warn "  $($remaining.Count) process(es) still running after cleanup — may need manual kill: $($remaining -join ', ')"
    } else {
        Write-Success "All stale port-forwards cleared."
    }
}

function Invoke-PortForwards([PSCustomObject]$State) {
    Write-Step "Phase 9 — Start Port Forwards"
    Write-Info "Port forwards must run in a separate terminal and stay open while"
    Write-Info "you are using FeatBit."
    Write-Info ""

    Stop-StalePortForwards

    Write-Info ""
    Write-Info "Port mappings:"
    Write-Info "  UI:          http://localhost:8081 (west)   http://localhost:8082 (east)"
    Write-Info "  API:         http://localhost:15000 (west)  http://localhost:15001 (east)"
    Write-Info "  Evaluation:  http://localhost:5100 (west)   http://localhost:5101 (east)"
    Write-Info "  Kafka UI:    http://localhost:18080 (west)  http://localhost:18081 (east)"
    Write-Info ""

    $pfScript = Join-Path $script:SiblingDir "Start-PortForwards.ps1"
    Write-Info "Open a second terminal and run exactly ONE instance:"
    Write-Info "  pwsh $pfScript"
    Write-Info ""
    Wait-UserConfirm "Press Enter once port forwards are running in another terminal..."

    Complete-Phase $State "port-forwards"
}

function Invoke-MongoReplicaSet([PSCustomObject]$State) {
    Write-Step "Phase 10 — Initialize MongoDB Replica Set"
    Write-Info "Configuring the MongoDB replica set that spans the west and east clusters."
    Write-Info "This is required for FeatBit Pro data replication between clusters."
    Write-Info ""

    $mongoScript = Join-Path $script:SiblingDir "Initialize-MongoDBReplicaSet.ps1"
    if (-not (Test-Path $mongoScript)) { throw "Initialize-MongoDBReplicaSet.ps1 not found at $mongoScript" }

    if ($PSCmdlet.ShouldProcess("MongoDB", "Initialize replica set")) {
        & $mongoScript
        if ($LASTEXITCODE -ne 0) { throw "Initialize-MongoDBReplicaSet.ps1 failed" }
    }

    Complete-Phase $State "mongo-replica-set"
    Write-Success "MongoDB replica set initialized"
}

function Invoke-Done {
    Write-Host ""
    Write-Host "  ╔══════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "  ║          FeatBit Pro is ready!                          ║" -ForegroundColor Green
    Write-Host "  ╚══════════════════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Access URLs (port forwards must be running):" -ForegroundColor White
    Write-Host "    West cluster UI  →  http://localhost:8081" -ForegroundColor Cyan
    Write-Host "    East cluster UI  →  http://localhost:8082" -ForegroundColor Cyan
    Write-Host "    West API         →  http://localhost:15000" -ForegroundColor Cyan
    Write-Host "    East API         →  http://localhost:15001" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Default credentials:" -ForegroundColor White
    Write-Host "    Email:    test@featbit.com" -ForegroundColor Gray
    Write-Host "    Password: (set during first login)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  Useful commands:" -ForegroundColor White
    Write-Host "    pwsh ../Start-PortForwards.ps1          — restart port forwards" -ForegroundColor Gray
    Write-Host "    k9s --context west                      — inspect west cluster" -ForegroundColor Gray
    Write-Host "    k9s --context east                      — inspect east cluster" -ForegroundColor Gray
    Write-Host "    pwsh ../Deploy-FeatBitClusters.ps1 -SkipClusterCreation  — redeploy apps only" -ForegroundColor Gray
    Write-Host ""
}

# ── Main ──────────────────────────────────────────────────────────────────────

Write-Banner

if ($Reset) {
    if (Test-Path $script:StateFile) {
        Remove-Item $script:StateFile -Force
        Write-Warn "Progress state cleared. Starting from the beginning."
    }
}

$state = Get-State

$allPhases = @(
    @{ Key = "ensure-pwsh";       Fn = { Invoke-EnsurePwsh } }
    @{ Key = "system-prereqs";    Fn = { Invoke-SystemPrereqs $state } }
    @{ Key = "dev-tools";         Fn = { Invoke-DevTools $state } }
    @{ Key = "install-k6";        Fn = { Invoke-InstallK6 $state -HostPlatform Ubuntu } }
    @{ Key = "repo-setup";        Fn = { Invoke-RepoSetup $state } }
    @{ Key = "collect-creds";     Fn = { Invoke-CollectCreds $state } }
    @{ Key = "build-images";      Fn = { Invoke-BuildImages $state } }
    @{ Key = "proxy-first-run";   Fn = { Invoke-ProxyFirstRun $state } }
    @{ Key = "deploy-clusters";    Fn = { Invoke-DeployClusters $state } }
    @{ Key = "verify-pull-backoff"; Fn = { Invoke-VerifyPullBackoff $state } }
    @{ Key = "proxy-second-run";  Fn = { Invoke-ProxySecondRun $state } }
    @{ Key = "port-forwards";     Fn = { Invoke-PortForwards $state } }
    @{ Key = "mongo-replica-set"; Fn = { Invoke-MongoReplicaSet $state } }
)

$allComplete = $true
foreach ($phase in $allPhases) {
    # 'collect-creds' holds in-memory credentials only and must always re-run;
    # all other phases honor the saved completion state for resumability.
    # Runtime skip — does not write to state, so the phase re-enables
    # automatically on a future run that omits the flag.
    if ($phase.Key -eq "install-k6" -and -not $InstallK6) {
        continue
    }
    if ($phase.Key -ne "collect-creds" -and (Test-PhaseComplete $state $phase.Key)) {
        Write-Host "  ✓ $($phase.Key) — already complete" -ForegroundColor DarkGreen
        continue
    }
    # Runtime skip — does not write to state, so the phase re-enables
    # automatically on a future run that omits the flag.
    if ($phase.Key -eq "repo-setup" -and $SkipRepoSetup) {
        Write-Host "  → $($phase.Key) — skipped by -SkipRepoSetup" -ForegroundColor DarkYellow
        continue
    }
    $allComplete = $false
    try {
        & $phase.Fn
        # 'collect-creds' is intentionally never persisted to state — credentials
        # only exist for the lifetime of this process, so the phase must re-run
        # every invocation.
        if ($phase.Key -notin @("ensure-pwsh", "collect-creds")) { Complete-Phase $state $phase.Key }
    } catch {
        Write-Fail "Phase '$($phase.Key)' failed: $_"
        Write-Info ""
        Write-Info "Fix the issue above, then re-run this script to resume from this phase."
        exit 1
    }
}

if ($allComplete) {
    Write-Host ""
    Write-Host "  All phases already complete." -ForegroundColor DarkGreen
    Write-Info "Run with -Reset to start over, or run individual scripts manually."
    Write-Host ""
} else {
    Invoke-Done
}
