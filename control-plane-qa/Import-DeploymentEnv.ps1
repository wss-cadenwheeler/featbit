#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Loads deployment.env into script-scoped variables and returns a splatting hashtable.

.DESCRIPTION
    Reads a KEY=VALUE env file (ignoring blank lines and # comments), maps each key
    to the corresponding script parameter name, and returns a hashtable suitable for
    splatting into a deploy script.

    Credential parameters (CUSTOM_REGISTRY_USERNAME / CUSTOM_REGISTRY_PASSWORD) are
    combined into a PSCredential and keyed as "CustomRegistryCredential".

    Scripts call this as:
        $envParams = & (Join-Path $PSScriptRoot "Import-DeploymentEnv.ps1")
        # merge with any explicitly-passed bound parameters, then splat the rest

.PARAMETER EnvFile
    Path to the env file. Defaults to deployment.env in the same directory as this script.
#>
param(
    [string]$EnvFile = ""
)

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolved = if ($EnvFile) { $EnvFile } else { Join-Path $repoRoot "deployment.env" }

if (-not (Test-Path $resolved)) {
    return @{}
}

# Parse KEY=VALUE lines.
$raw = @{}
foreach ($line in Get-Content $resolved) {
    $trimmed = $line.Trim()
    if (-not $trimmed -or $trimmed.StartsWith("#")) {
        continue
    }

    $eqIdx = $trimmed.IndexOf("=")
    if ($eqIdx -lt 1) {
        continue
    }

    $key   = $trimmed.Substring(0, $eqIdx).Trim()
    $value = $trimmed.Substring($eqIdx + 1).Trim()

    if ($value) {
        $raw[$key] = $value
    }
}

# Map env keys to PowerShell parameter names.
$keyMap = @{
    "DEPLOYMENT_MODE"              = "DeploymentMode"
    "DATABASE_PROVIDER"            = "DatabaseProvider"
    "CUSTOM_IMAGE_REGISTRY"        = "CustomImageRegistry"
    "FEATBIT_IMAGE_REGISTRY"       = "FeatBitImageRegistry"
    "INFRA_IMAGE_REPOSITORY"       = "InfraImageRepository"
    "INFRA_IMAGE_MAP_FILE"         = "InfraImageMapFile"
    "CUSTOM_REGISTRY_SECRET_NAME"  = "CustomRegistrySecretName"
    "MONGO_IMAGE"                  = "MongoImage"
    "POSTGRES_IMAGE"               = "PostgresImage"
    "MINIKUBE_BASE_IMAGE"          = "MinikubeBaseImage"
    "WEST_CPUS"                    = "WestCpus"
    "WEST_MEMORY"                  = "WestMemory"
    "EAST_CPUS"                    = "EastCpus"
    "EAST_MEMORY"                  = "EastMemory"
    "HOST_INFRA_COMPONENTS"        = "HostInfraComponents"
}

$params = @{}

foreach ($envKey in $raw.Keys) {
    if ($keyMap.ContainsKey($envKey)) {
        $paramName = $keyMap[$envKey]
        $value     = $raw[$envKey]

        # Type coercion for numeric / array params.
        switch ($paramName) {
            { $_ -in "WestCpus", "WestMemory", "EastCpus", "EastMemory" } {
                $params[$paramName] = [int]$value
            }
            "HostInfraComponents" {
                $params[$paramName] = $value -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }
            }
            default {
                $params[$paramName] = $value
            }
        }
    }
}

# Build a PSCredential if username + password are both provided.
$user = $raw["CUSTOM_REGISTRY_USERNAME"]
$pass = $raw["CUSTOM_REGISTRY_PASSWORD"]
if ($user -and $pass) {
    $securePass = ConvertTo-SecureString $pass -AsPlainText -Force
    $params["CustomRegistryCredential"] = New-Object System.Management.Automation.PSCredential($user, $securePass)
}

return $params
