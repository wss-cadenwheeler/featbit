[CmdletBinding()]
param(
    [string]$ApiBaseUrl = "http://featbit-api.west.local",

    [string]$ApiAuthorizationHeader,

    [string]$LoginEmail = "test@featbit.com",

    [string]$LoginPassword = "123456",

    [string]$WorkspaceKey = "",

    [bool]$SkipCertificateCheck = $true,

    [string]$ApiVersion = "1",

    [string]$OrganizationName = "playground",

    [string]$OrganizationKey = "playground",

    [string]$ProjectName = "control-plane-test",

    [string]$ProjectKey = "control-plane-test",

    [string]$EnvironmentName = "Dev",

    [string]$EnvironmentKey = "dev",

    [string]$EnvironmentDescription = "Seeded environment for control-plane QA automation.",

    [string[]]$OnboardingEnvironments = @("Prod", "Dev"),

    [string[]]$RequiredFlagKeys = @("ff-cp02-west", "ff-cp02-east", "ff-cp03-resilience"),

    [switch]$ForceFlagsOff,

    [string]$ArtifactsRoot = "control-plane-qa/artifacts/seed"
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

function Resolve-AuthorizationHeader {
    if (-not [string]::IsNullOrWhiteSpace($ApiAuthorizationHeader)) {
        return $ApiAuthorizationHeader
    }

    $headers = @{ "Content-Type" = "application/json" }
    $payload = [PSCustomObject]@{
        email = $LoginEmail
        password = $LoginPassword
        workspaceKey = $WorkspaceKey
    }

    $loginUri = "{0}/api/v{1}/identity/login-by-email" -f $script:BaseUrl, $ApiVersion
    $response = Invoke-ApiRequest -Method "Post" -Uri $loginUri -Headers $headers -Body $payload

    $data = $response.data
    if ($null -eq $data) {
        $data = $response.Data
    }

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
        throw "Login succeeded but token was not found in login response."
    }

    return "Bearer $token"
}

function New-RequestHeaders {
    param(
        [string]$Authorization,
        [Guid]$WorkspaceId,
        [Guid]$OrganizationId
    )

    $headers = @{
        "Content-Type" = "application/json"
        "Authorization" = $Authorization
    }

    if ($WorkspaceId -ne [Guid]::Empty) {
        $headers["Workspace"] = $WorkspaceId.ToString()
    }

    if ($OrganizationId -ne [Guid]::Empty) {
        $headers["Organization"] = $OrganizationId.ToString()
    }

    return $headers
}

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        [hashtable]$Headers,
        [object]$Body = $null,
        [switch]$AllowFailure
    )

    $uri = "{0}{1}" -f $script:BaseUrl, $Path

    try {
        if ($null -eq $Body) {
            return Invoke-ApiRequest -Method $Method -Uri $uri -Headers $Headers
        }

        return Invoke-ApiRequest -Method $Method -Uri $uri -Headers $Headers -Body $Body
    }
    catch {
        if ($AllowFailure) {
            return $null
        }

        throw
    }
}

function Expand-ApiData {
    param([object]$Response)

    if ($null -eq $Response) {
        return $null
    }

    if ($Response.PSObject.Properties.Name -contains "data") {
        return $Response.data
    }

    return $Response
}

function New-BooleanFlagPayload {
    param([string]$Key)

    $enabledVariationId = [Guid]::NewGuid().ToString()
    $disabledVariationId = [Guid]::NewGuid().ToString()

    return [PSCustomObject]@{
        name = $Key
        key = $Key
        description = "Seeded by control-plane QA bootstrap script."
        tags = @("cp", "automation")
        isEnabled = $false
        variationType = "boolean"
        enabledVariationId = $enabledVariationId
        disabledVariationId = $disabledVariationId
        variations = @(
            [PSCustomObject]@{ id = $enabledVariationId; name = "True"; value = "true" },
            [PSCustomObject]@{ id = $disabledVariationId; name = "False"; value = "false" }
        )
    }
}

$script:BaseUrl = $ApiBaseUrl.TrimEnd("/")
$runId = [Guid]::NewGuid().ToString()
$runStartedUtc = (Get-Date).ToUniversalTime().ToString("o")

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$artifactDir = Join-Path $repoRoot "$ArtifactsRoot\$runId"
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$timeline = New-Object "System.Collections.Generic.List[object]"

$effectiveAuthorizationHeader = Resolve-AuthorizationHeader

$globalHeaders = New-RequestHeaders -Authorization $effectiveAuthorizationHeader -WorkspaceId ([Guid]::Empty) -OrganizationId ([Guid]::Empty)

$profileResponse = Invoke-Api -Method "Get" -Path "/api/v$ApiVersion/user/profile" -Headers $globalHeaders
$profile = Expand-ApiData $profileResponse
$workspaceId = [Guid]$profile.workspaceId

$timeline.Add([PSCustomObject]@{
    step = "profile"
    timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    workspaceId = $workspaceId
})

$workspaceHeaders = New-RequestHeaders -Authorization $effectiveAuthorizationHeader -WorkspaceId $workspaceId -OrganizationId ([Guid]::Empty)

$orgListResponse = Invoke-Api -Method "Get" -Path "/api/v$ApiVersion/organizations" -Headers $workspaceHeaders
$organizations = @(Expand-ApiData $orgListResponse)

$organization = $organizations | Where-Object { $_.key -eq $OrganizationKey } | Select-Object -First 1
if ($null -eq $organization) {
    $organization = $organizations | Where-Object { $_.name -eq $OrganizationName } | Select-Object -First 1
}

if ($null -eq $organization) {
    $createOrgPayload = [PSCustomObject]@{
        name = $OrganizationName
        key = $OrganizationKey
    }

    $createdOrgResponse = Invoke-Api -Method "Post" -Path "/api/v$ApiVersion/organizations" -Headers $workspaceHeaders -Body $createOrgPayload
    $organization = Expand-ApiData $createdOrgResponse

    $timeline.Add([PSCustomObject]@{
        step = "create-organization"
        timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        organizationId = $organization.id
        organizationKey = $organization.key
    })
}

$organizationId = [Guid]$organization.id
$orgHeaders = New-RequestHeaders -Authorization $effectiveAuthorizationHeader -WorkspaceId $workspaceId -OrganizationId $organizationId

if ($organization.initialized -eq $false) {
    $onboardingPayload = [PSCustomObject]@{
        organizationName = $OrganizationName
        organizationKey = $OrganizationKey
        projectName = $ProjectName
        projectKey = $ProjectKey
        environments = $OnboardingEnvironments
    }

    [void](Invoke-Api -Method "Post" -Path "/api/v$ApiVersion/organizations/onboarding" -Headers $orgHeaders -Body $onboardingPayload)

    $timeline.Add([PSCustomObject]@{
        step = "organization-onboarding"
        timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        organizationId = $organizationId
    })
}

$projectsResponse = Invoke-Api -Method "Get" -Path "/api/v$ApiVersion/projects" -Headers $orgHeaders
$projects = @(Expand-ApiData $projectsResponse)

$project = $projects | Where-Object { $_.key -eq $ProjectKey } | Select-Object -First 1
if ($null -eq $project) {
    $project = $projects | Where-Object { $_.name -eq $ProjectName } | Select-Object -First 1
}

if ($null -eq $project) {
    $createProjectPayload = [PSCustomObject]@{
        name = $ProjectName
        key = $ProjectKey
    }

    $createdProjectResponse = Invoke-Api -Method "Post" -Path "/api/v$ApiVersion/projects" -Headers $orgHeaders -Body $createProjectPayload
    $project = Expand-ApiData $createdProjectResponse

    $timeline.Add([PSCustomObject]@{
        step = "create-project"
        timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        projectId = $project.id
        projectKey = $project.key
    })
}

$projectId = [Guid]$project.id
$environments = @($project.environments)

$environment = $environments | Where-Object { $_.key -eq $EnvironmentKey } | Select-Object -First 1
if ($null -eq $environment) {
    $environment = $environments | Where-Object { $_.name -eq $EnvironmentName } | Select-Object -First 1
}

if ($null -eq $environment) {
    $createEnvPayload = [PSCustomObject]@{
        name = $EnvironmentName
        key = $EnvironmentKey
        description = $EnvironmentDescription
    }

    $createdEnvironmentResponse = Invoke-Api -Method "Post" -Path "/api/v$ApiVersion/projects/$projectId/envs" -Headers $orgHeaders -Body $createEnvPayload
    $environment = Expand-ApiData $createdEnvironmentResponse

    $timeline.Add([PSCustomObject]@{
        step = "create-environment"
        timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        environmentId = $environment.id
        environmentKey = $environment.key
    })
}

$envId = [Guid]$environment.id

$seededFlags = @()
foreach ($flagKey in $RequiredFlagKeys) {
    $getFlagPath = "/api/v$ApiVersion/envs/$envId/feature-flags/$flagKey"
    $existingFlagResponse = Invoke-Api -Method "Get" -Path $getFlagPath -Headers $orgHeaders -AllowFailure
    $existingFlag = Expand-ApiData $existingFlagResponse

    if ($null -eq $existingFlag) {
        $createFlagPayload = New-BooleanFlagPayload -Key $flagKey
        $createFlagResponse = Invoke-Api -Method "Post" -Path "/api/v$ApiVersion/envs/$envId/feature-flags" -Headers $orgHeaders -Body $createFlagPayload
        $existingFlag = Expand-ApiData $createFlagResponse

        $timeline.Add([PSCustomObject]@{
            step = "create-flag"
            timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
            flagKey = $flagKey
        })
    }

    if ($ForceFlagsOff) {
        [void](Invoke-Api -Method "Put" -Path "/api/v$ApiVersion/envs/$envId/feature-flags/$flagKey/toggle/false" -Headers $orgHeaders -Body @{})

        $timeline.Add([PSCustomObject]@{
            step = "toggle-flag-off"
            timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
            flagKey = $flagKey
        })
    }

    $seededFlags += [PSCustomObject]@{
        key = $flagKey
        id = $existingFlag.id
        isEnabled = $existingFlag.isEnabled
    }
}

$summary = [PSCustomObject]@{
    runId = $runId
    startedUtc = $runStartedUtc
    finishedUtc = (Get-Date).ToUniversalTime().ToString("o")
    workspaceId = $workspaceId
    organization = [PSCustomObject]@{
        id = $organizationId
        name = $OrganizationName
        key = $OrganizationKey
    }
    project = [PSCustomObject]@{
        id = $projectId
        name = $ProjectName
        key = $ProjectKey
    }
    environment = [PSCustomObject]@{
        id = $envId
        name = $EnvironmentName
        key = $EnvironmentKey
    }
    flags = $seededFlags
    artifacts = [PSCustomObject]@{
        summary = (Join-Path $artifactDir "summary.json")
        timeline = (Join-Path $artifactDir "timeline.json")
    }
}

$summaryPath = Join-Path $artifactDir "summary.json"
$timelinePath = Join-Path $artifactDir "timeline.json"

$summary | ConvertTo-Json -Depth 20 | Set-Content -Path $summaryPath -Encoding utf8
$timeline | ConvertTo-Json -Depth 20 | Set-Content -Path $timelinePath -Encoding utf8

Write-Host "Seed complete." -ForegroundColor Green
Write-Host "Workspace: $workspaceId"
Write-Host "Organization: $OrganizationKey ($organizationId)"
Write-Host "Project: $ProjectKey ($projectId)"
Write-Host "Environment: $EnvironmentKey ($envId)"
Write-Host "Artifacts: $artifactDir"
