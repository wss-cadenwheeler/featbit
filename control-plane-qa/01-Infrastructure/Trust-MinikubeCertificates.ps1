<#
.SYNOPSIS
    Download and trust corporate certificates inside specified Minikube clusters.
.DESCRIPTION
    Fetches the required PEM files, copies them into each cluster's /usr/local/share/ca-certificates folder, and runs update-ca-certificates.
.PARAMETER Clusters
    Minikube profile names to update. Defaults to the west and east clusters used by the multi-cluster deployment script.
.EXAMPLE
    .\Trust-MinikubeCertificates.ps1
.EXAMPLE
    .\Trust-MinikubeCertificates.ps1 -Clusters @("dev", "test")
#>
[CmdletBinding()]
param(
    [string[]]$Clusters = @("west", "east"),
    [string[]]$RegistryHosts = @()
)

$ErrorActionPreference = "Stop"

# Load certificate list from TRUST_CERTIFICATES in deployment.env.
# Format: semicolon-separated entries of name|url|target
# Falls back to the built-in corporate defaults when the key is absent.
$deploymentEnvFile = Join-Path $PSScriptRoot "deployment.env"
$trustCertificatesRaw = ""
if (Test-Path $deploymentEnvFile) {
    foreach ($line in Get-Content $deploymentEnvFile) {
        $trimmed = $line.Trim()
        if ($trimmed -and -not $trimmed.StartsWith("#") -and $trimmed.StartsWith("TRUST_CERTIFICATES=")) {
            $trustCertificatesRaw = $trimmed.Substring("TRUST_CERTIFICATES=".Length).Trim()
            break
        }
    }
}

if (-not $trustCertificatesRaw) {
    Write-Warning "TRUST_CERTIFICATES is not set in deployment.env — no certificates will be installed."
    exit 0
}

$certificates = $trustCertificatesRaw -split ";" | Where-Object { $_ } | ForEach-Object {
    $parts = $_ -split "\|"
    @{ Name = $parts[0].Trim(); Url = $parts[1].Trim(); Target = $parts[2].Trim() }
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "featbit-cert-trust"
if (-not (Test-Path $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir | Out-Null
}

try {
    foreach ($certificate in $certificates) {
        $localPath = Join-Path $tempDir "$($certificate.Name).crt"
        Write-Host "Downloading $($certificate.Url)..."
        Invoke-WebRequest -Uri $certificate.Url -OutFile $localPath | Out-Null
        $certificate.LocalPath = $localPath
    }

    foreach ($cluster in $Clusters) {
        Write-Host "`nUpdating Minikube cluster: $cluster"

        foreach ($certificate in $certificates) {
            $localPath = $certificate.LocalPath
            $remoteTempPath = "/tmp/$($certificate.Name).crt"
            Write-Host "  Copying $($certificate.Name) certificate into cluster..."
            & minikube -p $cluster cp $localPath $remoteTempPath | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to copy $($certificate.Name) to $cluster"
            }

            $targetPath = $certificate.Target
            $targetDirectory = $targetPath -replace '/[^/]+$',''
            $installCommand = "sudo mkdir -p $targetDirectory && sudo mv $remoteTempPath $targetPath && sudo chmod 644 $targetPath"
            & minikube ssh -p $cluster -- "$installCommand" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to install $($certificate.Name) on $cluster"
            }
        }

        Write-Host "  Refreshing CA trust store..."
        & minikube ssh -p $cluster -- "sudo update-ca-certificates" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to refresh CA store on $cluster"
        }

        if ($registryHosts.Count -gt 0) {
            Write-Host "  Updating Docker daemon trust..."
            $certPaths = ($certificates | ForEach-Object { $_.Target }) -join " "
            foreach ($registry in $registryHosts) {
                $dockerCommand = "sudo mkdir -p /etc/docker/certs.d/$registry && sudo bash -c 'cat $certPaths > /etc/docker/certs.d/$registry/ca.crt'"
                & minikube ssh -p $cluster -- $dockerCommand | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    throw "Failed to install docker certs for $registry on $cluster"
                }
            }

            $restartCommand = "if command -v systemctl >/dev/null 2>&1; then sudo systemctl restart docker || true; elif command -v service >/dev/null 2>&1; then sudo service docker restart || true; elif [ -x /etc/init.d/docker ]; then sudo /etc/init.d/docker restart || true; fi"
            & minikube ssh -p $cluster -- $restartCommand | Out-Null
        }

        Write-Host "✓ Cluster $cluster trust store updated."
    }
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force
    }
}
