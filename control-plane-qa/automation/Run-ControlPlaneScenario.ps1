[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "cp02-west-to-east",
        "cp02-east-to-west",
        "cp03-west-with-east-redis-outage",
        "cp03-east-with-west-redis-outage"
    )]
    [string]$Scenario,

    [Parameter(Mandatory = $true)]
    [Guid]$EnvId,

    [string]$ApiVersion = "1",

    [string]$WestApiBaseUrl = "http://localhost:15000",

    [string]$EastApiBaseUrl = "http://localhost:15001",

    [string]$ApiAuthorizationHeader,

    [string]$FlagKey,

    [bool]$TargetStatus = $true,

    [int]$TimeoutSeconds = 60,

    [int]$PollIntervalMilliseconds = 1000,

    [int]$DisruptionHoldSeconds = 15,

    [string]$StartDisruptionCommand,

    [string]$StopDisruptionCommand,

    [string]$SourceTopicCheckCommand,

    [string]$DownstreamTopicCheckCommand,

    [string]$RetryLogCheckCommand,

    [string]$RedisWestCheckCommand,

    [string]$RedisEastCheckCommand,

    [string]$ArtifactsRoot = "control-plane-qa/artifacts"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-ScenarioDefinition {
    param(
        [string]$ScenarioName,
        [string]$CustomFlagKey,
        [bool]$CustomTargetStatus
    )

    switch ($ScenarioName) {
        "cp02-west-to-east" {
            return @{
                ScenarioType = "cp02"
                SourceRegion = "west"
                TargetRegion = "east"
                DefaultFlagKey = "ff-cp02-west"
                TargetStatus = $CustomTargetStatus
            }
        }
        "cp02-east-to-west" {
            return @{
                ScenarioType = "cp02"
                SourceRegion = "east"
                TargetRegion = "west"
                DefaultFlagKey = "ff-cp02-east"
                TargetStatus = $CustomTargetStatus
            }
        }
        "cp03-west-with-east-redis-outage" {
            return @{
                ScenarioType = "cp03"
                SourceRegion = "west"
                TargetRegion = "east"
                DefaultFlagKey = "ff-cp03-resilience"
                TargetStatus = $CustomTargetStatus
            }
        }
        "cp03-east-with-west-redis-outage" {
            return @{
                ScenarioType = "cp03"
                SourceRegion = "east"
                TargetRegion = "west"
                DefaultFlagKey = "ff-cp03-resilience"
                TargetStatus = $CustomTargetStatus
            }
        }
    }

    throw "Unsupported scenario: $ScenarioName"
}

function Get-ApiBaseUrl {
    param([string]$Region)

    if ($Region -eq "west") {
        return $WestApiBaseUrl.TrimEnd('/')
    }

    return $EastApiBaseUrl.TrimEnd('/')
}

function New-Headers {
    $headers = @{
        "Content-Type" = "application/json"
    }

    if (-not [string]::IsNullOrWhiteSpace($ApiAuthorizationHeader)) {
        $headers["Authorization"] = $ApiAuthorizationHeader
    }

    return $headers
}

function Invoke-ToggleFlag {
    param(
        [string]$BaseUrl,
        [Guid]$EnvironmentId,
        [string]$FeatureFlagKey,
        [bool]$Status,
        [hashtable]$Headers
    )

    $uri = "{0}/api/v{1}/envs/{2}/feature-flags/{3}/toggle/{4}" -f $BaseUrl, $ApiVersion, $EnvironmentId, $FeatureFlagKey, $Status.ToString().ToLowerInvariant()

    return Invoke-RestMethod -Method Put -Uri $uri -Headers $Headers -Body "{}"
}

function Get-FlagState {
    param(
        [string]$BaseUrl,
        [Guid]$EnvironmentId,
        [string]$FeatureFlagKey,
        [hashtable]$Headers,
        [string]$Region
    )

    $uri = "{0}/api/v{1}/envs/{2}/feature-flags/{3}" -f $BaseUrl, $ApiVersion, $EnvironmentId, $FeatureFlagKey

    try {
        $result = Invoke-RestMethod -Method Get -Uri $uri -Headers $Headers
        $data = $result.data

        return [PSCustomObject]@{
            region = $Region
            observedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
            isEnabled = $data.isEnabled
            key = $data.key
            version = $data.version
            id = $data.id
            raw = $result
            error = $null
        }
    }
    catch {
        return [PSCustomObject]@{
            region = $Region
            observedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
            isEnabled = $null
            key = $FeatureFlagKey
            version = $null
            id = $null
            raw = $null
            error = $_.Exception.Message
        }
    }
}

function Add-Assertion {
    param(
        [System.Collections.Generic.List[object]]$Assertions,
        [string]$Name,
        [bool]$Passed,
        [string]$Details,
        [string]$Status = "evaluated"
    )

    $Assertions.Add([PSCustomObject]@{
        name = $Name
        passed = $Passed
        status = $Status
        details = $Details
    })
}

function Invoke-OptionalCheck {
    param(
        [string]$Name,
        [string]$Command,
        [System.Collections.Generic.List[object]]$Assertions,
        [System.Collections.Generic.List[object]]$Timeline,
        [string]$RunId
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        Add-Assertion -Assertions $Assertions -Name $Name -Passed $true -Details "Not configured." -Status "skipped"
        return
    }

    try {
        $scriptBlock = [ScriptBlock]::Create($Command)
        $output = & $scriptBlock 2>&1 | Out-String

        $Timeline.Add([PSCustomObject]@{
            type = "optional-check"
            check = $Name
            runId = $RunId
            timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
            output = $output.Trim()
        })

        Add-Assertion -Assertions $Assertions -Name $Name -Passed $true -Details "Command executed successfully."
    }
    catch {
        Add-Assertion -Assertions $Assertions -Name $Name -Passed $false -Details $_.Exception.Message
    }
}

function Invoke-DisruptionCommand {
    param(
        [string]$Name,
        [string]$Command,
        [System.Collections.Generic.List[object]]$Timeline,
        [string]$RunId
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        throw "$Name command is required for CP-03 scenarios."
    }

    $scriptBlock = [ScriptBlock]::Create($Command)
    $output = & $scriptBlock 2>&1 | Out-String

    $Timeline.Add([PSCustomObject]@{
        type = "disruption-command"
        phase = $Name
        runId = $RunId
        timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        output = $output.Trim()
    })
}

function Wait-ForConvergence {
    param(
        [string]$SourceBaseUrl,
        [string]$TargetBaseUrl,
        [Guid]$EnvironmentId,
        [string]$FeatureFlagKey,
        [bool]$ExpectedStatus,
        [hashtable]$Headers,
        [int]$TimeoutSec,
        [int]$PollMs,
        [System.Collections.Generic.List[object]]$Timeline
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)

    while ((Get-Date) -lt $deadline) {
        $sourceSnapshot = Get-FlagState -BaseUrl $SourceBaseUrl -EnvironmentId $EnvironmentId -FeatureFlagKey $FeatureFlagKey -Headers $Headers -Region "source"
        $targetSnapshot = Get-FlagState -BaseUrl $TargetBaseUrl -EnvironmentId $EnvironmentId -FeatureFlagKey $FeatureFlagKey -Headers $Headers -Region "target"

        $Timeline.Add([PSCustomObject]@{
            type = "poll"
            timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
            source = $sourceSnapshot
            target = $targetSnapshot
        })

        $sourceMatch = ($sourceSnapshot.error -eq $null) -and ($sourceSnapshot.isEnabled -eq $ExpectedStatus)
        $targetMatch = ($targetSnapshot.error -eq $null) -and ($targetSnapshot.isEnabled -eq $ExpectedStatus)

        if ($sourceMatch -and $targetMatch) {
            return [PSCustomObject]@{
                converged = $true
                source = $sourceSnapshot
                target = $targetSnapshot
            }
        }

        Start-Sleep -Milliseconds $PollMs
    }

    return [PSCustomObject]@{
        converged = $false
        source = $null
        target = $null
    }
}

$definition = Get-ScenarioDefinition -ScenarioName $Scenario -CustomFlagKey $FlagKey -CustomTargetStatus $TargetStatus
$effectiveFlagKey = if ([string]::IsNullOrWhiteSpace($FlagKey)) { $definition.DefaultFlagKey } else { $FlagKey }
$headers = New-Headers

$sourceBaseUrl = Get-ApiBaseUrl -Region $definition.SourceRegion
$targetBaseUrl = Get-ApiBaseUrl -Region $definition.TargetRegion

$runId = [Guid]::NewGuid().ToString()
$runStartedUtc = (Get-Date).ToUniversalTime().ToString("o")

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$artifactDir = Join-Path $repoRoot "$ArtifactsRoot\$Scenario\$runId"
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$timeline = New-Object "System.Collections.Generic.List[object]"
$assertions = New-Object "System.Collections.Generic.List[object]"

$timeline.Add([PSCustomObject]@{
    type = "run-start"
    runId = $runId
    timestampUtc = $runStartedUtc
    scenario = $Scenario
    sourceRegion = $definition.SourceRegion
    targetRegion = $definition.TargetRegion
    envId = $EnvId
    flagKey = $effectiveFlagKey
    expectedStatus = $definition.TargetStatus
})

try {
    if ($definition.ScenarioType -eq "cp03") {
        Invoke-DisruptionCommand -Name "start" -Command $StartDisruptionCommand -Timeline $timeline -RunId $runId

        $holdUntil = (Get-Date).AddSeconds($DisruptionHoldSeconds)
        while ((Get-Date) -lt $holdUntil) {
            $targetDuringOutage = Get-FlagState -BaseUrl $targetBaseUrl -EnvironmentId $EnvId -FeatureFlagKey $effectiveFlagKey -Headers $headers -Region "target"
            $timeline.Add([PSCustomObject]@{
                type = "outage-poll"
                timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
                target = $targetDuringOutage
            })
            Start-Sleep -Milliseconds $PollIntervalMilliseconds
        }
    }

    $toggleResult = Invoke-ToggleFlag -BaseUrl $sourceBaseUrl -EnvironmentId $EnvId -FeatureFlagKey $effectiveFlagKey -Status $definition.TargetStatus -Headers $headers
    $timeline.Add([PSCustomObject]@{
        type = "api-toggle"
        timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        result = $toggleResult
    })
    Add-Assertion -Assertions $assertions -Name "api-toggle-succeeded" -Passed $true -Details "Toggle endpoint responded successfully."

    if ($definition.ScenarioType -eq "cp03") {
        Invoke-DisruptionCommand -Name "stop" -Command $StopDisruptionCommand -Timeline $timeline -RunId $runId
    }

    $convergence = Wait-ForConvergence `
        -SourceBaseUrl $sourceBaseUrl `
        -TargetBaseUrl $targetBaseUrl `
        -EnvironmentId $EnvId `
        -FeatureFlagKey $effectiveFlagKey `
        -ExpectedStatus $definition.TargetStatus `
        -Headers $headers `
        -TimeoutSec $TimeoutSeconds `
        -PollMs $PollIntervalMilliseconds `
        -Timeline $timeline

    Add-Assertion -Assertions $assertions -Name "source-target-convergence" -Passed $convergence.converged -Details "Both regions reported expected isEnabled=$($definition.TargetStatus)."

    Invoke-OptionalCheck -Name "source-topic-check" -Command $SourceTopicCheckCommand -Assertions $assertions -Timeline $timeline -RunId $runId
    Invoke-OptionalCheck -Name "downstream-topic-check" -Command $DownstreamTopicCheckCommand -Assertions $assertions -Timeline $timeline -RunId $runId
    Invoke-OptionalCheck -Name "retry-log-check" -Command $RetryLogCheckCommand -Assertions $assertions -Timeline $timeline -RunId $runId
    Invoke-OptionalCheck -Name "redis-west-check" -Command $RedisWestCheckCommand -Assertions $assertions -Timeline $timeline -RunId $runId
    Invoke-OptionalCheck -Name "redis-east-check" -Command $RedisEastCheckCommand -Assertions $assertions -Timeline $timeline -RunId $runId
}
catch {
    Add-Assertion -Assertions $assertions -Name "runner-execution" -Passed $false -Details $_.Exception.Message
}
finally {
    $timelinePath = Join-Path $artifactDir "timeline.json"
    $assertionsPath = Join-Path $artifactDir "assertions.json"
    $summaryPath = Join-Path $artifactDir "summary.json"

    $timeline | ConvertTo-Json -Depth 20 | Set-Content -Path $timelinePath -Encoding utf8
    $assertions | ConvertTo-Json -Depth 20 | Set-Content -Path $assertionsPath -Encoding utf8

    $failedAssertions = $assertions | Where-Object { $_.status -eq "evaluated" -and -not $_.passed }
    $overallPassed = ($failedAssertions.Count -eq 0)

    $summary = [PSCustomObject]@{
        runId = $runId
        scenario = $Scenario
        startedUtc = $runStartedUtc
        finishedUtc = (Get-Date).ToUniversalTime().ToString("o")
        envId = $EnvId
        flagKey = $effectiveFlagKey
        sourceRegion = $definition.SourceRegion
        targetRegion = $definition.TargetRegion
        expectedStatus = $definition.TargetStatus
        passed = $overallPassed
        failedAssertions = $failedAssertions
        artifacts = [PSCustomObject]@{
            summary = $summaryPath
            assertions = $assertionsPath
            timeline = $timelinePath
        }
    }

    $summary | ConvertTo-Json -Depth 20 | Set-Content -Path $summaryPath -Encoding utf8

    if ($overallPassed) {
        Write-Host "PASS: $Scenario" -ForegroundColor Green
        Write-Host "Artifacts: $artifactDir"
        exit 0
    }

    Write-Host "FAIL: $Scenario" -ForegroundColor Red
    Write-Host "Artifacts: $artifactDir"
    exit 1
}
