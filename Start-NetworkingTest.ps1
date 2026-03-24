<#
.SYNOPSIS
    Launches the WSL2 networking test from Windows.

.DESCRIPTION
    This script starts the WSL2 networking test and waits for you to
    test Windows connectivity to the LoadBalancer IP.

.EXAMPLE
    .\Start-NetworkingTest.ps1
    Launches the test in WSL2.

.NOTES
    Author: GitHub Copilot
    Date: 2026-03-04
#>

[CmdletBinding()]
param()

Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  WSL2 + Rancher Desktop + Minikube Network Test" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

Write-Host "This test will:" -ForegroundColor Yellow
Write-Host "  1. Create a test Minikube cluster in WSL2" -ForegroundColor Gray
Write-Host "  2. Deploy MetalLB LoadBalancer" -ForegroundColor Gray
Write-Host "  3. Deploy a test nginx service" -ForegroundColor Gray
Write-Host "  4. Test if Windows can access the LoadBalancer IP" -ForegroundColor Gray
Write-Host ""

Write-Host "⏱️  This will take 3-5 minutes..." -ForegroundColor Yellow
Write-Host ""

# Stop existing clusters first
Write-Host "Stopping existing Minikube clusters (if any)..." -ForegroundColor Yellow
wsl -d $ubuntuDistro bash -c "minikube stop -p east 2>/dev/null || true"
wsl -d $ubuntuDistro bash -c "minikube stop -p west 2>/dev/null || true"
Write-Host "✓ Existing clusters stopped" -ForegroundColor Green
Write-Host ""

# Check if WSL2 is available
Write-Host "✓ WSL2 detected" -ForegroundColor Green

# Try to use Ubuntu-24.04 directly (your distro)
$ubuntuDistro = "Ubuntu-24.04"

Write-Host "✓ Using Ubuntu distribution: $ubuntuDistro" -ForegroundColor Green
Write-Host ""

# Copy test script to WSL2
$scriptPath = Join-Path $PSScriptRoot "Test-WSL2-Networking.sh"
if (-not (Test-Path $scriptPath)) {
    Write-Host "✗ Test script not found: $scriptPath" -ForegroundColor Red
    exit 1
}

Write-Host "Launching test in WSL2..." -ForegroundColor Cyan
Write-Host ""

# Convert Windows path to WSL path
$wslScriptPath = $scriptPath -replace '\\', '/' -replace 'C:', '/mnt/c'

# Run the test script in WSL2 using the detected Ubuntu distro
wsl -d $ubuntuDistro bash $wslScriptPath

Write-Host ""
Write-Host "════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ WSL2 test completed successfully" -ForegroundColor Green
    Write-Host ""
    Write-Host "Now test from Windows by running the script on your Desktop:" -ForegroundColor Yellow
    Write-Host "  ~/Desktop/test-network.ps1" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Or I can run it now. Press Enter to test..." -ForegroundColor Yellow
    $null = Read-Host
    
    $testScript = Join-Path $env:USERPROFILE "Desktop\test-network.ps1"
    if (Test-Path $testScript) {
        & $testScript
    } else {
        Write-Host "✗ Test script not found on Desktop" -ForegroundColor Red
    }
} else {
    Write-Host "✗ WSL2 test failed" -ForegroundColor Red
    Write-Host "Check the output above for errors" -ForegroundColor Yellow
}
