<#
.SYNOPSIS
    Manages all FeatBit port forwards in a single process with auto-restart.

.DESCRIPTION
    This script runs all kubectl port-forward commands in a single PowerShell window
    with automatic restart if any connection fails. Much cleaner than multiple terminals.
    
    Port Forwards:
    - West UI: 8081 -> ui:8081
    - East UI: 8082 -> ui:8081
    - West API: 15000 -> api-server:5000
    - East API: 15001 -> api-server:5000
    - West Eval: 5100 -> evaluation-server:5100
    - East Eval: 5101 -> evaluation-server:5100
    - Kafka: 29092 -> kafka:29092 (West, external listener)
    - West Kafka UI: 18080 -> kafka-ui:8080
    - East Kafka UI: 18081 -> kafka-ui:8080
    - West Redis: 6379 -> redis:6379
    - East Redis: 6380 -> redis:6379
    - MongoDB-0: 27017 -> mongodb-0-lb:27017 (West)
    - MongoDB-1: 27018 -> mongodb-1-lb:27017 (West)
    - MongoDB-2: 27019 -> mongodb-2-lb:27017 (East)

.EXAMPLE
    .\Start-PortForwards.ps1
    Starts all port forwards in a minimized window.

.NOTES
    Author: GitHub Copilot
    Date: 2026-03-04
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Continue"

$logFile = Join-Path $PSScriptRoot "port-forwards.log"

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logMessage = "[$timestamp] [$Level] $Message"
    Write-Host $logMessage
    Add-Content -Path $logFile -Value $logMessage -ErrorAction SilentlyContinue
}

function Start-PortForward {
    param(
        [string]$Name,
        [string]$Context,
        [string]$Namespace,
        [string]$Service,
        [string]$Pod,
        [string]$LocalPort,
        [string]$RemotePort
    )
    
    while ($true) {
        $target = if ($Pod) { "pod/$Pod" } else { "svc/$Service" }
        $displayName = if ($Pod) { $Pod } else { $Service }
        
        Write-Log "Starting $Name (${Context}/${Namespace}/${displayName} ${LocalPort}:${RemotePort})"
        
        try {
            $process = Start-Process kubectl `
                -ArgumentList @(
                    "--context", $Context,
                    "port-forward",
                    "-n", $Namespace,
                    $target,
                    "${LocalPort}:${RemotePort}"
                ) `
                -NoNewWindow `
                -PassThru `
                -RedirectStandardOutput "$PSScriptRoot\${Name}-stdout.log" `
                -RedirectStandardError "$PSScriptRoot\${Name}-stderr.log"
            
            Write-Log "$Name started (PID: $($process.Id))"
            
            # Wait for process to exit
            $process.WaitForExit()
            
            Write-Log "$Name exited with code $($process.ExitCode)" -Level "WARN"
            
        } catch {
            Write-Log "Error starting ${Name}: $($_.Exception.Message)" -Level "ERROR"
        }
        
        # Wait before restarting
        Write-Log "Restarting $Name in 5 seconds..." -Level "WARN"
        Start-Sleep -Seconds 5
    }
}

function Test-PodExists {
    param(
        [string]$Context,
        [string]$Namespace,
        [string]$PodName
    )

    kubectl --context $Context get pod $PodName -n $Namespace -o name 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

Write-Host "=======================================" -ForegroundColor Cyan
Write-Host "  FeatBit Port Forward Manager" -ForegroundColor Cyan
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Starting all port forwards..." -ForegroundColor Yellow
Write-Host "  This window will stay open and manage all forwards" -ForegroundColor Gray
Write-Host "  Press Ctrl+C to stop all port forwards" -ForegroundColor Gray
Write-Host "  Logs: $logFile" -ForegroundColor Gray
Write-Host ""

# Clear old log
if (Test-Path $logFile) {
    try {
        Remove-Item $logFile -Force -ErrorAction Stop
    } catch {
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $logFile = Join-Path $PSScriptRoot "port-forwards-$timestamp.log"
        Write-Host "Using alternate log file: $logFile" -ForegroundColor Yellow
    }
}

Write-Log "Port Forward Manager Started"

# Create jobs for each port forward
$jobs = @(
    @{Name="west-ui"; Context="west"; Namespace="featbit"; Service="ui"; LocalPort="8081"; RemotePort="8081"}
    @{Name="east-ui"; Context="east"; Namespace="featbit"; Service="ui"; LocalPort="8082"; RemotePort="8081"}
    @{Name="west-api"; Context="west"; Namespace="featbit"; Service="api-server"; LocalPort="15000"; RemotePort="5000"}
    @{Name="east-api"; Context="east"; Namespace="featbit"; Service="api-server"; LocalPort="15001"; RemotePort="5000"}
    @{Name="west-eval"; Context="west"; Namespace="featbit"; Service="evaluation-server"; LocalPort="5100"; RemotePort="5100"}
    @{Name="east-eval"; Context="east"; Namespace="featbit"; Service="evaluation-server"; LocalPort="5101"; RemotePort="5100"}
    @{Name="kafka"; Context="west"; Namespace="featbit"; Service="kafka"; LocalPort="29092"; RemotePort="29092"}
    @{Name="west-kafka-ui"; Context="west"; Namespace="featbit"; Service="kafka-ui"; LocalPort="18080"; RemotePort="8080"}
    @{Name="east-kafka-ui"; Context="east"; Namespace="featbit"; Service="kafka-ui"; LocalPort="18081"; RemotePort="8080"}
    @{Name="west-redis"; Context="west"; Namespace="featbit"; Service="redis"; LocalPort="6379"; RemotePort="6379"}
    @{Name="east-redis"; Context="east"; Namespace="featbit"; Service="redis"; LocalPort="6380"; RemotePort="6379"}
)

# Advanced mode: MongoDB runs as a StatefulSet — pods are mongodb-west-0/1 and mongodb-east-0.
# Basic mode: MongoDB runs on host Docker — no pods in the cluster.
$mongoAdvanced = (Test-PodExists -Context "west" -Namespace "featbit" -PodName "mongodb-west-0") -and
                 (Test-PodExists -Context "east" -Namespace "featbit" -PodName "mongodb-east-0")
$mongoBasic    = -not $mongoAdvanced -and
                 (Test-PodExists -Context "west" -Namespace "featbit" -PodName "mongodb-0") -and
                 (Test-PodExists -Context "east" -Namespace "featbit" -PodName "mongodb-0")

if ($mongoAdvanced) {
    $jobs += @(
        @{Name="mongodb-0"; Context="west"; Namespace="featbit"; Pod="mongodb-west-0"; LocalPort="27017"; RemotePort="27017"}
        @{Name="mongodb-1"; Context="west"; Namespace="featbit"; Pod="mongodb-west-1"; LocalPort="27018"; RemotePort="27017"}
        @{Name="mongodb-2"; Context="east"; Namespace="featbit"; Pod="mongodb-east-0"; LocalPort="27019"; RemotePort="27017"}
    )
}
elseif ($mongoBasic) {
    $jobs += @(
        @{Name="mongodb-0"; Context="west"; Namespace="featbit"; Pod="mongodb-0"; LocalPort="27017"; RemotePort="27017"}
        @{Name="mongodb-1"; Context="east"; Namespace="featbit"; Pod="mongodb-0"; LocalPort="27018"; RemotePort="27017"}
    )
}
else {
    Write-Log "Skipping MongoDB pod port-forwards (no MongoDB pods found — host MongoDB mode or pods not ready)." -Level "WARN"
}

# Start each port forward in a background job
$runspaceJobs = @()
foreach ($job in $jobs) {
    $scriptBlock = {
        param($Name, $Context, $Namespace, $Service, $Pod, $LocalPort, $RemotePort, $LogFile, $ScriptRoot)
        
        function Write-Log {
            param([string]$Message, [string]$Level = "INFO")
            $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
            $logMessage = "[$timestamp] [$Level] [$Name] $Message"
            Add-Content -Path $LogFile -Value $logMessage -ErrorAction SilentlyContinue
        }
        
        while ($true) {
            Write-Log "Starting port forward ${LocalPort}:${RemotePort}"
            
            try {
                if ($Pod) {
                    kubectl --context $Context port-forward -n $Namespace "pod/$Pod" "${LocalPort}:${RemotePort}" 2>&1 | Out-Null
                } else {
                    kubectl --context $Context port-forward -n $Namespace "svc/$Service" "${LocalPort}:${RemotePort}" 2>&1 | Out-Null
                }
                Write-Log "Port forward exited" -Level "WARN"
            } catch {
                Write-Log "Error: $($_.Exception.Message)" -Level "ERROR"
            }
            
            Start-Sleep -Seconds 5
        }
    }
    
    $powerShell = [PowerShell]::Create()
    $powerShell.AddScript($scriptBlock).
        AddArgument($job.Name).
        AddArgument($job.Context).
        AddArgument($job.Namespace).
        AddArgument($job.Service).
        AddArgument($job.Pod).
        AddArgument($job.LocalPort).
        AddArgument($job.RemotePort).
        AddArgument($logFile).
        AddArgument($PSScriptRoot) | Out-Null
    
    $handle = $powerShell.BeginInvoke()
    
    $runspaceJobs += @{
        PowerShell = $powerShell
        Handle = $handle
        Name = $job.Name
    }
    
    Write-Log "Started background job for $($job.Name)"
}

Write-Host "✓ All port forwards started" -ForegroundColor Green
Write-Host ""
Write-Host "Port Mappings:" -ForegroundColor Cyan
Write-Host "  Application Services:" -ForegroundColor Yellow
Write-Host "    localhost:8081 → West UI" -ForegroundColor Gray
Write-Host "    localhost:8082 → East UI" -ForegroundColor Gray
Write-Host "    localhost:15000 → West API" -ForegroundColor Gray
Write-Host "    localhost:15001 → East API" -ForegroundColor Gray
Write-Host "    localhost:5100 → West Evaluation" -ForegroundColor Gray
Write-Host "    localhost:5101 → East Evaluation" -ForegroundColor Gray
Write-Host ""
Write-Host "  Infrastructure Services:" -ForegroundColor Yellow
Write-Host "    localhost:29092 → Kafka (West, external listener)" -ForegroundColor Gray
    Write-Host "    localhost:18080 → West Kafka UI (featbit-kafka.west.local)" -ForegroundColor Gray
    Write-Host "    localhost:18081 → East Kafka UI (featbit-kafka.east.local)" -ForegroundColor Gray
    Write-Host "    localhost:6379  → West Redis (redis.west.local)" -ForegroundColor Gray
    Write-Host "    localhost:6380  → East Redis (redis.east.local)" -ForegroundColor Gray
Write-Host ""
Write-Host "  MongoDB Replica Set:" -ForegroundColor Yellow
if ($mongoAdvanced) {
    Write-Host "    localhost:27017 → mongodb-west-0 (West)" -ForegroundColor Gray
    Write-Host "    localhost:27018 → mongodb-west-1 (West)" -ForegroundColor Gray
    Write-Host "    localhost:27019 → mongodb-east-0 (East)" -ForegroundColor Gray
}
elseif ($mongoBasic) {
    Write-Host "    localhost:27017 → MongoDB-0 (West)" -ForegroundColor Gray
    Write-Host "    localhost:27018 → MongoDB-0 (East)" -ForegroundColor Gray
}
else {
    Write-Host "    Skipped (no MongoDB pods found — host MongoDB mode or pods not ready)" -ForegroundColor Gray
}
Write-Host ""
Write-Host "Press Ctrl+C to stop..." -ForegroundColor Yellow

# Keep script running and monitor
try {
    while ($true) {
        Start-Sleep -Seconds 10
        
        # Check if any jobs failed
        $anyFailed = $false
        foreach ($job in $runspaceJobs) {
            if ($job.Handle.IsCompleted) {
                $anyFailed = $true
                Write-Log "Job $($job.Name) completed unexpectedly" -Level "ERROR"
            }
        }
        
        if ($anyFailed) {
            Write-Log "Some jobs completed, but auto-restart should handle it" -Level "WARN"
        }
    }
} finally {
    Write-Log "Shutting down all port forwards..."
    foreach ($job in $runspaceJobs) {
        $job.PowerShell.Stop()
        $job.PowerShell.Dispose()
    }
    Write-Log "Port Forward Manager Stopped"
}
