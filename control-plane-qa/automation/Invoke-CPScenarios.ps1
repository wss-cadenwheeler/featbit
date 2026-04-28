[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("cp02", "cp03")]
    [string]$Suite,

    [Parameter(Mandatory = $true)]
    [Guid]$EnvId,

    [Parameter(Mandatory = $true)]
    [string]$ApiAuthorizationHeader,

    [string]$WestApiBaseUrl = "http://localhost:15000",

    [string]$EastApiBaseUrl = "http://localhost:15001",

    [string]$StartWestDisruptionCommand,

    [string]$StopWestDisruptionCommand,

    [string]$StartEastDisruptionCommand,

    [string]$StopEastDisruptionCommand
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runnerPath = Join-Path $PSScriptRoot "Run-ControlPlaneScenario.ps1"

if ($Suite -eq "cp02") {
    & $runnerPath -Scenario "cp02-west-to-east" -EnvId $EnvId -ApiAuthorizationHeader $ApiAuthorizationHeader -WestApiBaseUrl $WestApiBaseUrl -EastApiBaseUrl $EastApiBaseUrl
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & $runnerPath -Scenario "cp02-east-to-west" -EnvId $EnvId -ApiAuthorizationHeader $ApiAuthorizationHeader -WestApiBaseUrl $WestApiBaseUrl -EastApiBaseUrl $EastApiBaseUrl
    exit $LASTEXITCODE
}

& $runnerPath -Scenario "cp03-west-with-east-redis-outage" -EnvId $EnvId -ApiAuthorizationHeader $ApiAuthorizationHeader -WestApiBaseUrl $WestApiBaseUrl -EastApiBaseUrl $EastApiBaseUrl -StartDisruptionCommand $StartEastDisruptionCommand -StopDisruptionCommand $StopEastDisruptionCommand
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $runnerPath -Scenario "cp03-east-with-west-redis-outage" -EnvId $EnvId -ApiAuthorizationHeader $ApiAuthorizationHeader -WestApiBaseUrl $WestApiBaseUrl -EastApiBaseUrl $EastApiBaseUrl -StartDisruptionCommand $StartWestDisruptionCommand -StopDisruptionCommand $StopWestDisruptionCommand
exit $LASTEXITCODE
