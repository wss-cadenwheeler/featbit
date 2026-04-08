<#
.SYNOPSIS
    Installs and configures all prerequisites required to run the FeatBit QA deployment scripts.

.DESCRIPTION
    Checks for the presence of each prerequisite and installs it if missing.

    Prerequisites managed:
    - PowerShell 7.6 or later  (check only — cannot self-upgrade)
    - Docker Desktop            (installed via winget)
    - Minikube                  (installed via winget)
    - kubectl                   (installed via winget)
    - Chocolatey                (optional, required by Setup-FeatBitProxy.ps1)

    The script is idempotent: tools that are already present and meet the minimum
    version requirement are skipped without modification.

    Winget (Windows Package Manager) must be available. It ships with Windows 10 1709+
    and can be installed from https://aka.ms/getwinget if missing.

.PARAMETER SkipChocolatey
    Skip the optional Chocolatey installation step.
    Chocolatey is only required if you intend to run Setup-FeatBitProxy.ps1.

.PARAMETER Force
    Reinstall tools that are already present, regardless of their current version.

.PARAMETER WhatIf
    Dry-run mode. Reports what would be installed or upgraded without making any changes.

.EXAMPLE
    .\Install-Prerequisites.ps1
    Checks and installs all prerequisites including Chocolatey.

.EXAMPLE
    .\Install-Prerequisites.ps1 -SkipChocolatey
    Checks and installs core prerequisites only (Docker Desktop, Minikube, kubectl).

.EXAMPLE
    .\Install-Prerequisites.ps1 -WhatIf
    Reports which prerequisites are missing or outdated without installing anything.

.NOTES
    Docker Desktop installation requires you to log out and back in (or restart)
    before the Docker daemon is available. The script will warn you when this is
    the case.

    Run this script from an elevated (Administrator) PowerShell session when
    installing Docker Desktop or Chocolatey, as both require elevated privileges.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$SkipChocolatey,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Set-StrictMode -Version Latest

# ── Minimum version requirements ─────────────────────────────────────────────
$minimumPowerShellMajor = 7
$minimumPowerShellMinor = 6
$minimumMinikubeVersion = [Version]"1.32.0"
$minimumKubectlVersion  = [Version]"1.28.0"

# ── Console helpers ───────────────────────────────────────────────────────────

function Write-Step
{
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Write-Success
{
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Info
{
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Gray
}

function Write-Warn
{
    param([string]$Message)
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

function Write-Fail
{
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

# ── Privilege check ───────────────────────────────────────────────────────────

function Test-Administrator
{
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal   = New-Object Security.Principal.WindowsPrincipal($currentUser)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ── Winget availability ───────────────────────────────────────────────────────

function Assert-Winget
{
    if (-not (Get-Command winget -ErrorAction SilentlyContinue))
    {
        Write-Fail "winget (Windows Package Manager) is not available."
        Write-Info "Install it from https://aka.ms/getwinget and re-run this script."
        exit 1
    }
}

# ── Generic winget installer ──────────────────────────────────────────────────

function Install-WingetPackage
{
    param(
        [string]$PackageId,
        [string]$DisplayName
    )

    if ($WhatIfPreference)
    {
        Write-Warn "[WhatIf] Would install $DisplayName via: winget install --id $PackageId"
        return
    }

    Write-Info "Installing $DisplayName via winget..."
    winget install --id $PackageId --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0)
    {
        Write-Fail "winget reported a failure installing $DisplayName (exit code $LASTEXITCODE)."
        Write-Info "Retry manually: winget install --id $PackageId"
        exit 1
    }
    Write-Success "$DisplayName installed."
}

# ── PowerShell version ────────────────────────────────────────────────────────

function Test-PowerShellVersion
{
    Write-Step "PowerShell Version"

    $current = $PSVersionTable.PSVersion
    $meetsRequirement = ($current.Major -gt $minimumPowerShellMajor) -or
                        ($current.Major -eq $minimumPowerShellMajor -and $current.Minor -ge $minimumPowerShellMinor)

    if ($meetsRequirement)
    {
        Write-Success "PowerShell $current — OK (minimum $minimumPowerShellMajor.$minimumPowerShellMinor required)."
        return
    }

    Write-Fail "PowerShell $current is below the minimum required version ($minimumPowerShellMajor.$minimumPowerShellMinor)."
    Write-Info "Download the latest release from https://github.com/PowerShell/PowerShell/releases"
    Write-Info "Or install via winget: winget install --id Microsoft.PowerShell"
    exit 1
}

# ── Docker Desktop ────────────────────────────────────────────────────────────

function Install-DockerDesktop
{
    Write-Step "Docker Desktop"

    $dockerCmd  = Get-Command docker -ErrorAction SilentlyContinue
    $rancherCmd = Get-Command nerdctl -ErrorAction SilentlyContinue  # Rancher Desktop ships nerdctl

    if ($dockerCmd -and -not $Force)
    {
        $versionOutput = docker version --format '{{.Server.Version}}' 2>$null
        Write-Success "Docker is already available (server version: $versionOutput)."
        return
    }

    if ($rancherCmd -and -not $Force)
    {
        Write-Success "Rancher Desktop is already available — skipping Docker Desktop installation."
        return
    }

    if (-not (Test-Administrator))
    {
        Write-Fail "Installing Docker Desktop requires an Administrator session."
        Write-Info "Re-run this script from an elevated PowerShell prompt."
        exit 1
    }

    Install-WingetPackage -PackageId "Docker.DockerDesktop" -DisplayName "Docker Desktop"

    if (-not $WhatIfPreference)
    {
        Write-Warn "Docker Desktop was just installed."
        Write-Info "You must log out and back in (or restart) before the Docker daemon is available."
        Write-Info "Re-run this script after restarting to verify Docker is running."
        $script:restartRequired = $true
    }
}

# ── Minikube ──────────────────────────────────────────────────────────────────

function Install-Minikube
{
    Write-Step "Minikube"

    $minikubeCmd = Get-Command minikube -ErrorAction SilentlyContinue

    if ($minikubeCmd -and -not $Force)
    {
        $rawVersion    = minikube version --short 2>$null
        $versionString = $rawVersion -replace '^v', ''
        $parsedVersion = [Version]::new(0, 0, 0)
        [void][Version]::TryParse($versionString, [ref]$parsedVersion)

        if ($parsedVersion -ge $minimumMinikubeVersion)
        {
            Write-Success "Minikube $rawVersion — OK (minimum v$minimumMinikubeVersion required)."
            return
        }

        Write-Warn "Minikube $rawVersion is below the minimum required version (v$minimumMinikubeVersion)."
        Write-Info "Upgrading Minikube..."
    }

    Install-WingetPackage -PackageId "Kubernetes.minikube" -DisplayName "Minikube"

    # Refresh PATH so minikube is immediately available in this session.
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("PATH", "User")
}

# ── kubectl ───────────────────────────────────────────────────────────────────

function Install-Kubectl
{
    Write-Step "kubectl"

    $kubectlCmd = Get-Command kubectl -ErrorAction SilentlyContinue

    if ($kubectlCmd -and -not $Force)
    {
        $rawVersion    = kubectl version --client --output=json 2>$null | ConvertFrom-Json
        $versionString = $rawVersion.clientVersion.gitVersion -replace '^v', ''
        $parsedVersion = [Version]::new(0, 0, 0)
        [void][Version]::TryParse($versionString, [ref]$parsedVersion)

        if ($parsedVersion -ge $minimumKubectlVersion)
        {
            Write-Success "kubectl v$versionString — OK (minimum v$minimumKubectlVersion required)."
            return
        }

        Write-Warn "kubectl v$versionString is below the minimum required version (v$minimumKubectlVersion)."
        Write-Info "Upgrading kubectl..."
    }

    Install-WingetPackage -PackageId "Kubernetes.kubectl" -DisplayName "kubectl"

    # Refresh PATH.
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("PATH", "User")
}

# ── Chocolatey ────────────────────────────────────────────────────────────────

function Install-Chocolatey
{
    Write-Step "Chocolatey (optional — required for Setup-FeatBitProxy.ps1)"

    $chocoCmd = Get-Command choco -ErrorAction SilentlyContinue

    if ($chocoCmd -and -not $Force)
    {
        $chocoVersion = choco --version 2>$null
        Write-Success "Chocolatey $chocoVersion is already installed."
        return
    }

    if (-not (Test-Administrator))
    {
        Write-Warn "Installing Chocolatey requires an Administrator session — skipping."
        Write-Info "To install manually, open an elevated PowerShell and run:"
        Write-Info "  Set-ExecutionPolicy Bypass -Scope Process -Force"
        Write-Info "  [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072"
        Write-Info "  Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))"
        return
    }

    if ($WhatIfPreference)
    {
        Write-Warn "[WhatIf] Would install Chocolatey via the official install script."
        return
    }

    Write-Info "Installing Chocolatey..."

    Set-ExecutionPolicy Bypass -Scope Process -Force
    [System.Net.ServicePointManager]::SecurityProtocol =
        [System.Net.ServicePointManager]::SecurityProtocol -bor 3072

    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString(
        'https://community.chocolatey.org/install.ps1'))

    # Reload profile so choco is on PATH.
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("PATH", "User")

    $chocoCmd = Get-Command choco -ErrorAction SilentlyContinue
    if (-not $chocoCmd)
    {
        Write-Fail "Chocolatey installation appeared to succeed but 'choco' was not found on PATH."
        Write-Info "Open a new PowerShell session and run 'choco --version' to verify."
        return
    }

    Write-Success "Chocolatey installed successfully."
}

# ── Summary ───────────────────────────────────────────────────────────────────

function Write-Summary
{
    Write-Step "Summary"

    $tools = @(
        @{ Name = "PowerShell"; Command = "pwsh" },
        @{ Name = "Docker";     Command = "docker" },
        @{ Name = "Minikube";   Command = "minikube" },
        @{ Name = "kubectl";    Command = "kubectl" }
    )

    if (-not $SkipChocolatey)
    {
        $tools += @{ Name = "Chocolatey"; Command = "choco" }
    }

    foreach ($tool in $tools)
    {
        $found = Get-Command $tool.Command -ErrorAction SilentlyContinue
        if ($found)
        {
            Write-Success "$($tool.Name) is available at $($found.Source)"
        }
        else
        {
            Write-Warn "$($tool.Name) was not found on PATH — a new terminal session may be required."
        }
    }

    if ($script:restartRequired)
    {
        Write-Host ""
        Write-Warn "A restart or log-out/log-in is required before Docker Desktop is fully operational."
        Write-Info "After restarting, re-run this script to confirm all prerequisites are met."
    }
    else
    {
        Write-Host ""
        Write-Success "All prerequisites are present. You are ready to run Deploy-FeatBitClusters.ps1."
    }
}

# ── Entry point ───────────────────────────────────────────────────────────────

$script:restartRequired = $false

if ($WhatIfPreference)
{
    Write-Host "`n[WhatIf] Dry-run mode — no changes will be made.`n" -ForegroundColor Magenta
}

Assert-Winget
Test-PowerShellVersion
Install-DockerDesktop
Install-Minikube
Install-Kubectl

if (-not $SkipChocolatey)
{
    Install-Chocolatey
}

Write-Summary
