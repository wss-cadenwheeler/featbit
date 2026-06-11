<#
.SYNOPSIS
    Download and trust corporate certificates inside specified Minikube clusters.
.DESCRIPTION
    Fetches the required PEM files, copies them into each cluster's /usr/local/share/ca-certificates
    folder, runs update-ca-certificates, and optionally configures per-registry Docker daemon trust.

    Certificate sources (evaluated in order, first match wins):
      1. TRUST_CERTIFICATES in deployment.env  — semicolon-separated "name|url|target" entries.
      2. -WindowsCertStoreSubjects             — subject substring patterns exported directly from
                                                the Windows LocalMachine\Root and CA stores.
                                                Useful when the CA is trusted on the host but not
                                                accessible at a downloadable URL.

.PARAMETER Clusters
    Minikube profile names to update. Defaults to the west and east clusters.
.PARAMETER RegistryHosts
    Registry hostnames to also configure in /etc/docker/certs.d/ and restart Docker.
.PARAMETER WindowsCertStoreSubjects
    Subject substrings to match against the Windows LocalMachine cert stores (Root + CA).
    All unique matching certs are exported as PEM and installed in the clusters.
    Example: @("My Corp CA", "Corporate Root CA 2024")
.EXAMPLE
    # Trust certs listed in deployment.env
    .\Trust-MinikubeCertificates.ps1
.EXAMPLE
    # Export corporate CAs directly from the Windows cert store
    .\Trust-MinikubeCertificates.ps1 -WindowsCertStoreSubjects @("My Corp CA") -RegistryHosts registry.example.com
.EXAMPLE
    .\Trust-MinikubeCertificates.ps1 -Clusters @("dev", "test")
#>
[CmdletBinding()]
param(
    [string[]]$Clusters = @("west", "east"),
    [string[]]$RegistryHosts = @(),
    [string[]]$WindowsCertStoreSubjects = @()
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

# ── Resolve certificate list ──────────────────────────────────────────────────
# Source 1: TRUST_CERTIFICATES in deployment.env  (name|url|target entries)
$certificates = [System.Collections.Generic.List[hashtable]]::new()

if ($trustCertificatesRaw) {
    foreach ($entry in ($trustCertificatesRaw -split ";" | Where-Object { $_ })) {
        $parts = $entry -split "\|"
        $certificates.Add(@{ Name = $parts[0].Trim(); Url = $parts[1].Trim(); Target = $parts[2].Trim() })
    }
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "featbit-cert-trust"
if (-not (Test-Path $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir | Out-Null
}

# Source 2: Windows LocalMachine cert store export (no URL required)
if ($WindowsCertStoreSubjects.Count -gt 0) {
    $seen = @{}
    $allStoreCerts = @(Get-ChildItem Cert:\LocalMachine\Root) + @(Get-ChildItem Cert:\LocalMachine\CA)
    foreach ($subject in $WindowsCertStoreSubjects) {
        foreach ($cert in ($allStoreCerts | Where-Object { $_.Subject -match [regex]::Escape($subject) })) {
            if ($seen.ContainsKey($cert.Thumbprint)) { continue }
            $seen[$cert.Thumbprint] = $true
            $safeName = "wincert-$($cert.Thumbprint.Substring(0,8).ToLower())"
            $localPath = Join-Path $tempDir "$safeName.crt"
            $pem = "-----BEGIN CERTIFICATE-----`r`n" +
                   [Convert]::ToBase64String($cert.RawData, [System.Base64FormattingOptions]::InsertLineBreaks) +
                   "`r`n-----END CERTIFICATE-----"
            [System.IO.File]::WriteAllText($localPath, $pem, [System.Text.Encoding]::ASCII)
            $certificates.Add(@{
                Name      = $safeName
                Url       = ""         # already on disk — skip download
                Target    = "/usr/local/share/ca-certificates/$safeName.crt"
                LocalPath = $localPath
            })
            Write-Host "  Exported from Windows store: $($cert.Subject)"
        }
    }
}

if ($certificates.Count -eq 0) {
    Write-Warning "No certificates to install. Set TRUST_CERTIFICATES in deployment.env or pass -WindowsCertStoreSubjects."
    exit 0
}

try {
    foreach ($certificate in $certificates) {
        if ($certificate.LocalPath) {
            # Already exported from the Windows cert store — no download needed.
            continue
        }
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
