[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("cp02", "cp03")]
    [string]$Suite,

    [Guid]$EnvId ="69310513-2c85-46e4-94bf-9247981e3565" ,

    [string]$ApiAuthorizationHeader,

    [string]$LoginEmail = "test@featbit.com",

    [string]$LoginPassword = "123456",

    [string]$WorkspaceKey = "",

    [string]$OrganizationKey = "playground",

    [bool]$SkipCertificateCheck = $true,

    [switch]$SeedData,

    [string]$SeedOrganizationName = "playground",

    [string]$SeedOrganizationKey = "playground",

    [string]$SeedProjectName = "control-plane-test",

    [string]$SeedProjectKey = "control-plane-test",

    [string]$SeedEnvironmentName = "Dev",

    [string]$SeedEnvironmentKey = "dev",

    [string]$WestApiBaseUrl = "http://featbit-api.west.local",

    [string]$EastApiBaseUrl = "http://featbit-api.east.local",

    [string]$StartWestDisruptionCommand,

    [string]$StopWestDisruptionCommand,

    [string]$StartEastDisruptionCommand,

    [string]$StopEastDisruptionCommand
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runnerPath = Join-Path $PSScriptRoot "Run-ControlPlaneScenario.ps1"
$seedPath = Join-Path $PSScriptRoot "Seed-ControlPlaneQaData.ps1"

$effectiveEnvId = $EnvId

if ($SeedData) {
    $seedResult = & $seedPath `
        -ApiBaseUrl $WestApiBaseUrl `
        -ApiAuthorizationHeader $ApiAuthorizationHeader `
        -LoginEmail $LoginEmail `
        -LoginPassword $LoginPassword `
        -WorkspaceKey $WorkspaceKey `
        -SkipCertificateCheck $SkipCertificateCheck `
        -OrganizationName $SeedOrganizationName `
        -OrganizationKey $SeedOrganizationKey `
        -ProjectName $SeedProjectName `
        -ProjectKey $SeedProjectKey `
        -EnvironmentName $SeedEnvironmentName `
        -EnvironmentKey $SeedEnvironmentKey `
        -ForceFlagsOff

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    if ($effectiveEnvId -eq [Guid]::Empty) {
        $seedSummaryLine = $seedResult | Where-Object { $_ -like "Environment:*" } | Select-Object -Last 1
        if (-not [string]::IsNullOrWhiteSpace($seedSummaryLine)) {
            if ($seedSummaryLine -match "\(([0-9a-fA-F\-]{36})\)") {
                $effectiveEnvId = [Guid]$matches[1]
            }
        }
    }
}

if ($effectiveEnvId -eq [Guid]::Empty) {
    throw "EnvId is required unless -SeedData is used and returns an environment id."
}

if ($Suite -eq "cp02") {
    & $runnerPath -Scenario "cp02-west-to-east" -EnvId $effectiveEnvId -ApiAuthorizationHeader $ApiAuthorizationHeader -WestApiBaseUrl $WestApiBaseUrl -EastApiBaseUrl $EastApiBaseUrl -LoginEmail $LoginEmail -LoginPassword $LoginPassword -WorkspaceKey $WorkspaceKey -OrganizationKey $OrganizationKey -SkipCertificateCheck $SkipCertificateCheck
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & $runnerPath -Scenario "cp02-east-to-west" -EnvId $effectiveEnvId -ApiAuthorizationHeader $ApiAuthorizationHeader -WestApiBaseUrl $WestApiBaseUrl -EastApiBaseUrl $EastApiBaseUrl -LoginEmail $LoginEmail -LoginPassword $LoginPassword -WorkspaceKey $WorkspaceKey -OrganizationKey $OrganizationKey -SkipCertificateCheck $SkipCertificateCheck
    exit $LASTEXITCODE
}

& $runnerPath -Scenario "cp03-west-with-east-redis-outage" -EnvId $effectiveEnvId -ApiAuthorizationHeader $ApiAuthorizationHeader -WestApiBaseUrl $WestApiBaseUrl -EastApiBaseUrl $EastApiBaseUrl -LoginEmail $LoginEmail -LoginPassword $LoginPassword -WorkspaceKey $WorkspaceKey -OrganizationKey $OrganizationKey -SkipCertificateCheck $SkipCertificateCheck -StartDisruptionCommand $StartEastDisruptionCommand -StopDisruptionCommand $StopEastDisruptionCommand
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $runnerPath -Scenario "cp03-east-with-west-redis-outage" -EnvId $effectiveEnvId -ApiAuthorizationHeader $ApiAuthorizationHeader -WestApiBaseUrl $WestApiBaseUrl -EastApiBaseUrl $EastApiBaseUrl -LoginEmail $LoginEmail -LoginPassword $LoginPassword -WorkspaceKey $WorkspaceKey -OrganizationKey $OrganizationKey -SkipCertificateCheck $SkipCertificateCheck -StartDisruptionCommand $StartWestDisruptionCommand -StopDisruptionCommand $StopWestDisruptionCommand
exit $LASTEXITCODE
