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

    [string]$WestApiBaseUrl = "https://featbit-api.west.local",

    [string]$EastApiBaseUrl = "https://featbit-api.east.local",

    [string]$LoginApiBaseUrl,

    [string]$ApiAuthorizationHeader,

    [string]$LoginEmail = "test@featbit.com",

    [string]$LoginPassword = "123456",

    [string]$WorkspaceKey = "",

    [string]$OrganizationKey = "playground",

    [bool]$SkipCertificateCheck = $true,

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

function Invoke-ApiRequest {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body = $null
    )

    $invokeArgs = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
    }

    if ($SkipCertificateCheck) {
        $invokeArgs["SkipCertificateCheck"] = $true
    }

    if ($null -ne $Body) {
        if ($Body -is [string]) {
            $invokeArgs["Body"] = $Body
        }
        else {
            $invokeArgs["Body"] = ($Body | ConvertTo-Json -Depth 20)
        }
    }

    return Invoke-RestMethod @invokeArgs
}

function Get-ApiData {
    param([object]$Response)

    if ($null -eq $Response) {
        return $null
    }

    if ($Response.PSObject.Properties.Name -contains "data") {
        return $Response.data
    }

    if ($Response.PSObject.Properties.Name -contains "Data") {
        return $Response.Data
    }

    return $Response
}

function Resolve-AuthorizationHeader {
    param([string]$DefaultBaseUrl)

    if (-not [string]::IsNullOrWhiteSpace($ApiAuthorizationHeader)) {
        return $ApiAuthorizationHeader
    }

    $loginBaseUrl = if ([string]::IsNullOrWhiteSpace($LoginApiBaseUrl)) { $DefaultBaseUrl } else { $LoginApiBaseUrl.TrimEnd('/') }
    $loginUri = "{0}/api/v{1}/identity/login-by-email" -f $loginBaseUrl, $ApiVersion

    $loginPayload = [PSCustomObject]@{
        email = $LoginEmail
        password = $LoginPassword
        workspaceKey = $WorkspaceKey
    }

    $loginHeaders = @{ "Content-Type" = "application/json" }
    $response = Invoke-ApiRequest -Method "Post" -Uri $loginUri -Headers $loginHeaders -Body $loginPayload
    $data = Get-ApiData -Response $response
    $token = $null

    if ($null -ne $data) {
        if ($data.PSObject.Properties.Name -contains "token") {
            $token = $data.token
        }
        elseif ($data.PSObject.Properties.Name -contains "Token") {
            $token = $data.Token
        }
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "Login succeeded but access token was not found in response payload."
    }

    return "Bearer $token"
}

function Resolve-RequestContext {
    param(
        [string]$BaseUrl,
        [string]$AuthorizationHeader
    )

    if (-not $AuthorizationHeader.StartsWith("Bearer ")) {
        return [PSCustomObject]@{
            workspaceId = $null
            organizationId = $null
        }
    }

    $authHeaders = @{
        "Content-Type" = "application/json"
        "Authorization" = $AuthorizationHeader
    }

    $profileUri = "{0}/api/v{1}/user/profile" -f $BaseUrl, $ApiVersion
    $profileResponse = Invoke-ApiRequest -Method "Get" -Uri $profileUri -Headers $authHeaders
    $profile = Get-ApiData -Response $profileResponse

    $workspaceId = $null
    if ($null -ne $profile) {
        $workspaceId = if ($profile.PSObject.Properties.Name -contains "workspaceId") { $profile.workspaceId } else { $profile.WorkspaceId }
    }

    if ([string]::IsNullOrWhiteSpace($workspaceId)) {
        throw "Unable to resolve workspace id from /user/profile for bearer authentication."
    }

    $authHeaders["Workspace"] = $workspaceId
    $orgListUri = "{0}/api/v{1}/organizations" -f $BaseUrl, $ApiVersion
    $orgListResponse = Invoke-ApiRequest -Method "Get" -Uri $orgListUri -Headers $authHeaders
    $organizations = @(Get-ApiData -Response $orgListResponse)
    if (-not $organizations -or $organizations.Count -eq 0) {
        throw "No organizations available for the current login. Seed data first or provide an API token."
    }

    $organization = $organizations | Where-Object { $_.key -eq $OrganizationKey } | Select-Object -First 1
    if ($null -eq $organization) {
        $organization = $organizations | Select-Object -First 1
    }

    return [PSCustomObject]@{
        workspaceId = $workspaceId
        organizationId = $organization.id
    }
}

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
    param(
        [string]$AuthorizationHeader,
        [string]$WorkspaceId,
        [string]$OrganizationId
    )

    $headers = @{
        "Content-Type" = "application/json"
    }

    if (-not [string]::IsNullOrWhiteSpace($AuthorizationHeader)) {
        $headers["Authorization"] = $AuthorizationHeader
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkspaceId)) {
        $headers["Workspace"] = $WorkspaceId
    }

    if (-not [string]::IsNullOrWhiteSpace($OrganizationId)) {
        $headers["Organization"] = $OrganizationId
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

    return Invoke-ApiRequest -Method "Put" -Uri $uri -Headers $Headers -Body "{}"
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
        $result = Invoke-ApiRequest -Method "Get" -Uri $uri -Headers $Headers
        $data = Get-ApiData -Response $result

        return [PSCustomObject]@{
            region = $Region
            observedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
            isEnabled = $data.isEnabled
            key = $data.key
            version = if ($data.PSObject.Properties.Name -contains "version") { $data.version } else { $null }
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
        [string]$RunId,
        [bool]$Required = $false
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        if ($Required) {
            Add-Assertion -Assertions $Assertions -Name $Name -Passed $false -Details "Command is required but was not provided."
            return
        }

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

$sourceBaseUrl = Get-ApiBaseUrl -Region $definition.SourceRegion
$targetBaseUrl = Get-ApiBaseUrl -Region $definition.TargetRegion

$effectiveAuthorizationHeader = Resolve-AuthorizationHeader -DefaultBaseUrl $sourceBaseUrl
$requestContext = Resolve-RequestContext -BaseUrl $sourceBaseUrl -AuthorizationHeader $effectiveAuthorizationHeader
$headers = New-Headers -AuthorizationHeader $effectiveAuthorizationHeader -WorkspaceId $requestContext.workspaceId -OrganizationId $requestContext.organizationId

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
    apiBaseUrlSource = $sourceBaseUrl
    apiBaseUrlTarget = $targetBaseUrl
    envId = $EnvId
    flagKey = $effectiveFlagKey
    expectedStatus = $definition.TargetStatus
    authType = if ($effectiveAuthorizationHeader.StartsWith("Bearer ")) { "bearer" } else { "openapi" }
    workspaceId = $requestContext.workspaceId
    organizationId = $requestContext.organizationId
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
    Invoke-OptionalCheck -Name "redis-west-check" -Command $RedisWestCheckCommand -Assertions $assertions -Timeline $timeline -RunId $runId -Required $true
    Invoke-OptionalCheck -Name "redis-east-check" -Command $RedisEastCheckCommand -Assertions $assertions -Timeline $timeline -RunId $runId -Required $true
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

    $failedAssertions = @($assertions | Where-Object { $_.status -eq "evaluated" -and -not $_.passed })
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
    Write-Host ""
    
    $passedCount = ($assertions | Where-Object { $_.status -eq "evaluated" -and $_.passed }).Count
    $failedCount = $failedAssertions.Count
    $skippedCount = ($assertions | Where-Object { $_.status -eq "skipped" }).Count
    
    Write-Host "Results: $passedCount passed | $failedCount FAILED | $skippedCount skipped" -ForegroundColor Yellow
    Write-Host ""
    
    if ($failedAssertions.Count -gt 0) {
        Write-Host "Failed Assertions:" -ForegroundColor Red
        foreach ($assertion in $failedAssertions) {
            Write-Host "  ✗ $($assertion.name)" -ForegroundColor Red
            if (-not [string]::IsNullOrWhiteSpace($assertion.details)) {
                Write-Host "    └─ $($assertion.details)" -ForegroundColor Yellow
            }
        }
        Write-Host ""
    }
    
    Write-Host "Artifacts Directory: $artifactDir" -ForegroundColor Gray
    Write-Host "  timeline.json ........... detailed event log" -ForegroundColor Gray
    Write-Host "  assertions.json ........ all assertion details" -ForegroundColor Gray
    Write-Host "  summary.json ........... overall result summary" -ForegroundColor Gray
    
    exit 1
}
