<#
.SYNOPSIS
    Full quickstart wizard for FeatBit Pro — Windows with Hyper-V.

.DESCRIPTION
    Guides you through every step to get a working two-cluster FeatBit Pro
    environment on Windows with Hyper-V enabled.  The script is resumable:
    completed phases are saved to .quickstart-state-hyperv.json and skipped
    on subsequent runs, so you can re-run after a reboot or interruption and
    pick up where you left off.

    Phases (in order):
      1. ensure-pwsh        — verify PowerShell 7+ is active
      2. system-prereqs     — enable Hyper-V, install WSL, install Git  [admin]
      3. dev-tools          — Docker Desktop, Minikube, kubectl, K9s (optional)
      4. repo-setup         — clone repo, checkout control-plane, configure deployment.env
      5. build-images       — build FeatBit images and push to localhost:5000  (~10-15 min)
      6. proxy-first-run    — first run of Setup-FeatBitProxy.ps1             [admin]
      7. deploy-clusters    — Deploy-FeatBitClusters.ps1 Advanced + MongoDB   (~20 min)
      8. proxy-second-run   — second run of Setup-FeatBitProxy.ps1            [admin]
      9. port-forwards      — instructions + launch Start-PortForwards.ps1
     10. mongo-replica-set  — Initialize-MongoDBReplicaSet.ps1

.PARAMETER Reset
    Clears the saved progress state and starts from the beginning.

.PARAMETER SkipOptional
    Skips optional installations (K9s, VS Code).

.PARAMETER SkipRepoSetup
    Skip Phase 4 — don't switch branches, touch deployment.env, or prompt for
    clone location. Use this when you're actively developing in this repo and
    managing its state yourself. The skip is not persisted; omit the flag on
    a future run to re-enable the phase.

.EXAMPLE
    .\Quickstart-HyperV.ps1
    Run (or resume) the wizard.

.EXAMPLE
    .\Quickstart-HyperV.ps1 -Reset
    Wipe saved progress and start over.

.EXAMPLE
    .\Quickstart-HyperV.ps1 -Reset -SkipRepoSetup
    Start over, but leave the repository (branch, deployment.env) alone.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Reset,
    [switch]$SkipOptional,
    [switch]$SkipRepoSetup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:StateFile  = Join-Path $PSScriptRoot ".quickstart-state-hyperv.json"
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
    Write-Host "  Quickstart Wizard — Windows + Hyper-V" -ForegroundColor White
    Write-Host "  This script is resumable. Re-run it after a reboot or interruption." -ForegroundColor Gray
    Write-Host ""
}

# ── State management ──────────────────────────────────────────────────────────

function Get-State {
    if (Test-Path $script:StateFile) {
        return Get-Content $script:StateFile -Raw | ConvertFrom-Json
    }
    return [PSCustomObject]@{
        completedPhases = @()
        rebootPending   = $false
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

# ── Elevation helpers ─────────────────────────────────────────────────────────

function Test-Administrator {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]$id
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Request-Elevation {
    param([string]$Reason)
    if (-not (Test-Administrator)) {
        Write-Warn "This phase requires administrator privileges: $Reason"
        Write-Info "Re-launching as administrator..."
        Start-Process pwsh -Verb RunAs -ArgumentList "-NoExit", "-File", "`"$PSCommandPath`""
        Write-Info "Close this window and continue in the new elevated terminal."
        exit 0
    }
}

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

# ── Phase implementations ─────────────────────────────────────────────────────

function Invoke-EnsurePwsh {
    Write-Step "Phase 1 — Verify PowerShell 7+"
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        Write-Fail "This script must run in PowerShell 7+.  You are running $($PSVersionTable.PSVersion)."
        Write-Info ""
        Write-Info "Install PowerShell 7:"
        Write-Info "  winget install --id Microsoft.PowerShell --source winget"
        Write-Info ""
        Write-Info "Then open a new 'pwsh' terminal and re-run this script."
        exit 1
    }
    Write-Success "PowerShell $($PSVersionTable.PSVersion) — OK"
}

function Invoke-SystemPrereqs([PSCustomObject]$State) {
    Write-Step "Phase 2 — System Prerequisites (Hyper-V, WSL, Git)  [requires admin]"
    Request-Elevation -Reason "Hyper-V and WSL installation"

    # Hyper-V
    $hvState = (Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction SilentlyContinue).State
    if ($hvState -eq "Enabled") {
        Write-Success "Hyper-V is already enabled"
    } else {
        Write-Info "Enabling Hyper-V (this will require a reboot)..."
        if ($PSCmdlet.ShouldProcess("Hyper-V", "Enable Windows Feature")) {
            $result = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All -NoRestart
            if ($result.RestartNeeded) {
                Write-Warn "A reboot is required to finish enabling Hyper-V."
                $State.rebootPending = $true
                Complete-Phase $State "system-prereqs"
                Write-Info ""
                Write-Info "► Reboot your machine, then open a new pwsh terminal and re-run:"
                Write-Info "    .\Quickstart-HyperV.ps1"
                Save-State $State
                exit 0
            }
        }
        Write-Success "Hyper-V enabled"
    }

    # WSL
    $wslInstalled = Get-Command wsl -ErrorAction SilentlyContinue
    if ($wslInstalled) {
        Write-Success "WSL is already installed"
    } else {
        Write-Info "Installing WSL (Windows Subsystem for Linux)..."
        if ($PSCmdlet.ShouldProcess("WSL", "Install")) {
            wsl --install --no-distribution
            if ($LASTEXITCODE -ne 0) { Write-Warn "WSL install returned non-zero — may already be installed or require a reboot" }
        }
        Write-Success "WSL installed (a reboot may still be needed on first use)"
    }

    # Git
    $gitInstalled = Get-Command git -ErrorAction SilentlyContinue
    if ($gitInstalled) {
        Write-Success "Git is already installed ($((git --version) -replace 'git version ', ''))"
    } else {
        Write-Info "Installing Git via winget..."
        if ($PSCmdlet.ShouldProcess("Git", "winget install")) {
            winget install --id Git.Git --source winget --silent --accept-package-agreements --accept-source-agreements
            if ($LASTEXITCODE -ne 0) { throw "Git installation failed" }
        }
        Write-Success "Git installed — open a new terminal to pick up the updated PATH"
    }

    Complete-Phase $State "system-prereqs"
    Write-Success "System prerequisites complete"
}

function Invoke-DevTools([PSCustomObject]$State) {
    Write-Step "Phase 3 — Developer Tools"

    Write-Info "Calling Install-Prerequisites.ps1 (Docker Desktop, Minikube, kubectl, Chocolatey)..."
    $prereqScript = Join-Path $script:SiblingDir "Install-Prerequisites.ps1"
    if (-not (Test-Path $prereqScript)) { throw "Install-Prerequisites.ps1 not found at $prereqScript" }
    & $prereqScript
    if ($LASTEXITCODE -ne 0) { throw "Install-Prerequisites.ps1 failed" }

    if (-not $SkipOptional) {
        # K9s
        $k9s = Get-Command k9s -ErrorAction SilentlyContinue
        if ($k9s) {
            Write-Success "K9s is already installed"
        } elseif (Get-Command choco -ErrorAction SilentlyContinue) {
            Write-Info "Installing K9s via Chocolatey (optional but very useful for troubleshooting)..."
            if ($PSCmdlet.ShouldProcess("k9s", "choco install")) {
                choco install k9s -y | Out-Null
            }
            Write-Success "K9s installed"
        } else {
            Write-Warn "Chocolatey not available — skipping K9s.  Install manually: choco install k9s"
        }

        # VS Code (truly optional, silent if winget unavailable)
        if (-not (Get-Command code -ErrorAction SilentlyContinue)) {
            if (Get-Command winget -ErrorAction SilentlyContinue) {
                Write-Info "Installing VS Code (optional)..."
                if ($PSCmdlet.ShouldProcess("VS Code", "winget install")) {
                    winget install --id Microsoft.VisualStudioCode --source winget --silent --accept-package-agreements --accept-source-agreements 2>$null
                }
                Write-Success "VS Code installed"
            }
        } else {
            Write-Success "VS Code is already installed"
        }
    }

    Complete-Phase $State "dev-tools"
    Write-Success "Developer tools ready"
    Write-Warn "If Docker Desktop was just installed, start it now and wait for it to be running before continuing."
    Wait-UserConfirm "Press Enter once Docker Desktop is running and ready..."
}

function Invoke-RepoSetup([PSCustomObject]$State) {
    Write-Step "Phase 4 — Repository Setup"

    # Detect if we're already inside the repo.
    # windows-hyperv → control-plane-qa → repo root
    $repoRoot = $null
    $candidate = Split-Path -Parent $script:SiblingDir
    if (Test-Path (Join-Path $candidate ".git")) {
        $repoRoot = $candidate
        Write-Success "Already inside repo: $repoRoot"
    } else {
        Write-Info "Repository not found at expected location."
        Write-Info "Enter the directory where you want to clone the repository"
        Write-Info "(e.g. C:\Users\$env:USERNAME\source):"
        Write-Host "  ► Clone location: " -ForegroundColor Yellow -NoNewline
        $cloneBase = (Read-Host).Trim()
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

    # Branch
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

    # deployment.env
    $qaDir  = Join-Path $repoRoot "control-plane-qa"
    $envDst = Join-Path $qaDir "deployment.env"
    $envEx  = Join-Path $qaDir "deployment.env.example"
    if (-not (Test-Path $envDst)) {
        if (Test-Path $envEx) {
            Copy-Item $envEx $envDst
            Write-Success "Created deployment.env from example"
        } else {
            Write-Warn "deployment.env.example not found — create deployment.env manually in $qaDir"
        }
    } else {
        Write-Success "deployment.env already exists"
    }

    Write-Warn "IMPORTANT: Review and configure deployment.env before continuing."
    Write-Info "Opening deployment.env in Notepad..."
    if (Test-Path $envDst) {
        if ($PSCmdlet.ShouldProcess($envDst, "Open in Notepad")) {
            Start-Process notepad.exe -ArgumentList $envDst
        }
    }
    Write-Info ""
    Write-Info "Key settings to verify:"
    Write-Info "  • CUSTOM_IMAGE_REGISTRY  — leave blank to pull from Docker Hub"
    Write-Info "  • WEST_CPUS / WEST_MEMORY, EAST_CPUS / EAST_MEMORY  — match your machine's capacity"
    Write-Info "  • DEPLOYMENT_MODE        — set to Advanced for this quickstart"
    Write-Info "  • DATABASE_PROVIDER      — set to MongoDb for this quickstart"
    Wait-UserConfirm "Press Enter when you have finished configuring deployment.env..."

    Complete-Phase $State "repo-setup"
    Write-Success "Repository configured"
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
    Write-Step "Phase 6 — Nginx Proxy First Run  [requires admin]"
    Request-Elevation -Reason "nginx installation and hosts file modification"

    Write-Info "Running Setup-FeatBitProxy.ps1 (first run)."
    Write-Warn "Some failures on the first run are expected — that is normal."
    Write-Info "The proxy will be fully configured after the second run (Phase 8)."
    Write-Info ""

    $proxyScript = Join-Path $script:SiblingDir "Setup-FeatBitProxy.ps1"
    if (-not (Test-Path $proxyScript)) { throw "Setup-FeatBitProxy.ps1 not found at $proxyScript" }

    if ($PSCmdlet.ShouldProcess("nginx proxy", "First run setup")) {
        & $proxyScript
        # Failures on first run are expected — do not throw on non-zero exit
        if ($LASTEXITCODE -ne 0) {
            Write-Warn "Setup-FeatBitProxy.ps1 exited with code $LASTEXITCODE (expected on first run)"
        }
    }

    Complete-Phase $State "proxy-first-run"
    Write-Success "Proxy first run complete"
}

function Invoke-DeployClusters([PSCustomObject]$State) {
    Write-Step "Phase 7 — Deploy FeatBit Clusters  (~20 minutes)"
    Write-Info "This creates the west and east Minikube clusters, deploys all FeatBit"
    Write-Info "infrastructure and applications in Advanced mode with MongoDB."
    Write-Info ""
    Write-Warn "This is the longest phase. Do not interrupt it."
    Write-Info ""

    $deployScript = Join-Path $script:SiblingDir "Deploy-FeatBitClusters.ps1"
    if (-not (Test-Path $deployScript)) { throw "Deploy-FeatBitClusters.ps1 not found at $deployScript" }

    if ($PSCmdlet.ShouldProcess("west + east clusters", "Deploy FeatBit Advanced + MongoDB")) {
        & $deployScript -RecreateClusters -DeploymentMode Advanced -DatabaseProvider MongoDb
        if ($LASTEXITCODE -ne 0) { throw "Deploy-FeatBitClusters.ps1 failed with exit code $LASTEXITCODE" }
    }

    Complete-Phase $State "deploy-clusters"
    Write-Success "Clusters deployed successfully"
}

function Invoke-ProxySecondRun([PSCustomObject]$State) {
    Write-Step "Phase 8 — Nginx Proxy Second Run  [requires admin]"
    Request-Elevation -Reason "nginx configuration update with cluster endpoints"

    Write-Info "Running Setup-FeatBitProxy.ps1 a second time to apply the cluster"
    Write-Info "endpoints that were created in the previous phase."
    Write-Info ""

    $proxyScript = Join-Path $script:SiblingDir "Setup-FeatBitProxy.ps1"
    if ($PSCmdlet.ShouldProcess("nginx proxy", "Second run setup")) {
        & $proxyScript
        if ($LASTEXITCODE -ne 0) { throw "Setup-FeatBitProxy.ps1 failed on second run" }
    }

    Complete-Phase $State "proxy-second-run"
    Write-Success "Proxy fully configured"
}

function Invoke-PortForwards([PSCustomObject]$State) {
    Write-Step "Phase 9 — Start Port Forwards"
    Write-Info "Port forwards must run in a separate terminal and stay open while"
    Write-Info "you are using FeatBit."
    Write-Info ""
    Write-Info "Port mappings:"
    Write-Info "  UI:          http://localhost:8081 (west)   http://localhost:8082 (east)"
    Write-Info "  API:         http://localhost:15000 (west)  http://localhost:15001 (east)"
    Write-Info "  Evaluation:  http://localhost:5100 (west)   http://localhost:5101 (east)"
    Write-Info "  Kafka UI:    http://localhost:18080 (west)  http://localhost:18081 (east)"
    Write-Info ""

    $pfScript = Join-Path $script:SiblingDir "Start-PortForwards.ps1"
    $choice = Wait-UserChoice "Open Start-PortForwards.ps1 in a new terminal window now?" @("y","n")
    if ($choice -eq "y") {
        if ($PSCmdlet.ShouldProcess("Start-PortForwards.ps1", "Open in new terminal")) {
            Start-Process pwsh -ArgumentList "-NoExit", "-File", "`"$pfScript`""
        }
        Write-Success "Port forwards started in a new window"
    } else {
        Write-Info "Run this manually when ready:"
        Write-Info "  .\Start-PortForwards.ps1"
    }

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
    Write-Host "    .\Start-PortForwards.ps1          — restart port forwards" -ForegroundColor Gray
    Write-Host "    k9s --context west                — inspect west cluster" -ForegroundColor Gray
    Write-Host "    k9s --context east                — inspect east cluster" -ForegroundColor Gray
    Write-Host "    .\Deploy-FeatBitClusters.ps1 -SkipClusterCreation  — redeploy apps only" -ForegroundColor Gray
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

# Handle pending reboot from a previous run
if ($state.rebootPending) {
    $state.rebootPending = $false
    Save-State $state
    Write-Success "Reboot detected — continuing from where we left off."
    Write-Info ""
}

$allPhases = @(
    @{ Key = "ensure-pwsh";       Fn = { Invoke-EnsurePwsh } }
    @{ Key = "system-prereqs";    Fn = { Invoke-SystemPrereqs $state } }
    @{ Key = "dev-tools";         Fn = { Invoke-DevTools $state } }
    @{ Key = "repo-setup";        Fn = { Invoke-RepoSetup $state } }
    @{ Key = "build-images";      Fn = { Invoke-BuildImages $state } }
    @{ Key = "proxy-first-run";   Fn = { Invoke-ProxyFirstRun $state } }
    @{ Key = "deploy-clusters";   Fn = { Invoke-DeployClusters $state } }
    @{ Key = "proxy-second-run";  Fn = { Invoke-ProxySecondRun $state } }
    @{ Key = "port-forwards";     Fn = { Invoke-PortForwards $state } }
    @{ Key = "mongo-replica-set"; Fn = { Invoke-MongoReplicaSet $state } }
)

$allComplete = $true
foreach ($phase in $allPhases) {
    if (Test-PhaseComplete $state $phase.Key) {
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
        if ($phase.Key -ne "ensure-pwsh") { Complete-Phase $state $phase.Key }
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
