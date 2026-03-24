<#
.SYNOPSIS
    Deploy FeatBit across multiple Minikube clusters with shared Docker network
.DESCRIPTION
    Creates two Minikube clusters (east/west) on a shared Docker network,
    deploys MongoDB replica set across both, and deploys FeatBit Pro
.EXAMPLE
    .\Deploy-FeatBitMultiCluster.ps1
#>

[CmdletBinding()]
param(
    [string]$CustomImageRegistry = "",
    [string]$InfraImageRepository = "",
    [PSCredential]$CustomRegistryCredential,
    [string]$MinikubeBaseImage = "kicbase:v0.0.50-corpca",
    [string]$MongoImage = ""
)

$ErrorActionPreference = "Stop"

# Load deployment.env defaults for any parameter not explicitly passed by the caller.
$_envDefaults = & (Join-Path $PSScriptRoot "Import-DeploymentEnv.ps1")
foreach ($k in $_envDefaults.Keys) {
    if (-not $PSBoundParameters.ContainsKey($k)) {
        Set-Variable -Name $k -Value $_envDefaults[$k]
    }
}

# Color output functions
function Write-Header {
    param([string]$Message)
    Write-Host "`n$('═' * 60)" -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host "$('═' * 60)`n" -ForegroundColor Cyan
}

function Write-Step {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Yellow
}

function Write-Success {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Gray
}

function Write-Error {
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Get-LoadBalancerIp {
  param(
    [string]$ClusterContext,
    [string]$Namespace,
    [string]$ServiceName,
    [int]$MaxAttempts = 30,
    [int]$DelaySeconds = 2
  )

  for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    $ip = kubectl --context $ClusterContext get svc $ServiceName -n $Namespace -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>$null
    if ($ip) {
      return $ip.Trim()
    }

    Start-Sleep -Seconds $DelaySeconds
  }

  return $null
}

# Load certificate list from TRUST_CERTIFICATES in deployment.env.
# Format: semicolon-separated entries of name|url|target
$_trustCertificatesRaw = ""
$_deploymentEnvFile = Join-Path $PSScriptRoot "deployment.env"
if (Test-Path $_deploymentEnvFile) {
    foreach ($line in Get-Content $_deploymentEnvFile) {
        $trimmed = $line.Trim()
        if ($trimmed -and -not $trimmed.StartsWith("#") -and $trimmed.StartsWith("TRUST_CERTIFICATES=")) {
            $_trustCertificatesRaw = $trimmed.Substring("TRUST_CERTIFICATES=".Length).Trim()
            break
        }
    }
}

$CertificateDefinitions = if ($_trustCertificatesRaw) {
    $_trustCertificatesRaw -split ";" | Where-Object { $_ } | ForEach-Object {
        $parts = $_ -split "\|"
        [pscustomobject]@{ Name = $parts[0].Trim(); Url = $parts[1].Trim(); Target = $parts[2].Trim(); LocalPath = $null }
    }
} else {
    @()
}

$CertificateTempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "featbit-cert-trust"

function Ensure-CertificateDownloads {
    if (-not (Test-Path $CertificateTempDirectory)) {
        New-Item -ItemType Directory -Path $CertificateTempDirectory | Out-Null
    }

    foreach ($definition in $CertificateDefinitions) {
        if (-not $definition.LocalPath -or -not (Test-Path $definition.LocalPath)) {
            $localPath = Join-Path $CertificateTempDirectory "$($definition.Name).crt"
            Write-Step "Downloading $($definition.Name) certificate..."
            Invoke-WebRequest -Uri $definition.Url -OutFile $localPath | Out-Null
            $definition.LocalPath = $localPath
        }
    }
}

function Install-ClusterCertificates {
    param(
        [string]$ClusterName,
        [string]$RegistryHost
    )

    Write-Step "Installing certificates on $ClusterName cluster..."

    foreach ($certificate in $CertificateDefinitions) {
        $remoteTempPath = "/tmp/$($certificate.Name).crt"
        & minikube -p $ClusterName cp $certificate.LocalPath $remoteTempPath | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to copy $($certificate.Name) to $ClusterName"
        }

        $targetDirectory = $certificate.Target -replace '/[^/]+$',''
        $installCommand = "sudo mkdir -p $targetDirectory && sudo mv $remoteTempPath $($certificate.Target) && sudo chmod 644 $($certificate.Target)"
        & minikube ssh -p $ClusterName -- $installCommand | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install $($certificate.Name) on $ClusterName"
        }
    }

    & minikube ssh -p $ClusterName -- "sudo update-ca-certificates" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to refresh CA store on $ClusterName"
    }

    if ($RegistryHost) {
        $certPaths = ($CertificateDefinitions | ForEach-Object { $_.Target }) -join " "
        $dockerCommand = "sudo mkdir -p /etc/docker/certs.d/$RegistryHost && sudo bash -c 'cat $certPaths > /etc/docker/certs.d/$RegistryHost/ca.crt'"
        & minikube ssh -p $ClusterName -- $dockerCommand | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install Docker trust bundle on $ClusterName"
        }

        $restartCommand = "if command -v systemctl >/dev/null 2>&1; then sudo systemctl restart docker || true; elif command -v service >/dev/null 2>&1; then sudo service docker restart || true; elif [ -x /etc/init.d/docker ]; then sudo /etc/init.d/docker restart || true; fi"
        & minikube ssh -p $ClusterName -- $restartCommand | Out-Null
    }

    Write-Success "$ClusterName certificates installed"
}

function Ensure-CustomRegistryImagePullSecret {
    param(
        [string]$ClusterContext,
        [string]$Namespace,
        [string]$Registry,
        [PSCredential]$Credential,
        [string]$SecretName = "registry-credentials"
    )

    $username = $Credential.UserName
    $password = $Credential.GetNetworkCredential().Password

    & kubectl --context $ClusterContext --namespace $Namespace delete secret $SecretName --ignore-not-found | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to remove existing $SecretName secret in $ClusterContext"
    }

    & kubectl --context $ClusterContext --namespace $Namespace create secret docker-registry $SecretName --docker-server=$Registry --docker-username=$username --docker-password=$password --docker-email=devnull@$Registry | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create $SecretName secret in $ClusterContext"
    }

    Write-Success "$SecretName secret created in $ClusterContext"
}

# Configuration
$SharedNetwork = "featbit-network"
$WestCluster = "west"
$EastCluster = "east"
$Namespace = "featbit"

Write-Header "FeatBit Multi-Cluster Deployment"

Write-Info "This will:"
Write-Info "  1. Create a shared Docker network"
Write-Info "  2. Create two Minikube clusters (east/west)"
Write-Info "  3. Deploy MongoDB replica set across both clusters"
Write-Info "  4. Deploy FeatBit Pro to both clusters"
Write-Info "  5. Configure ingress and services"
Write-Info "  6. Trust custom registry certificates and configure image pull secrets"
Write-Info "  7. Infra image repository: $InfraImageRepository"
Write-Info "  8. MongoDB image: $MongoImage"
Write-Info ""
Write-Info "⏱️  This will take 10-15 minutes..."
Write-Host ""

if (-not $CustomRegistryCredential -and $CustomImageRegistry) {
    Write-Step "Enter credentials for $CustomImageRegistry when prompted."
    $CustomRegistryCredential = Get-Credential -Message "Registry credentials ($CustomImageRegistry)"
    if (-not $CustomRegistryCredential) {
        Write-Error "Registry credentials are required to create image pull secrets."
        exit 1
    }
}

# Step 1: Clean up existing resources
Write-Header "Cleanup"

Write-Step "Stopping existing clusters..."
& minikube stop -p $WestCluster 2>$null
& minikube stop -p $EastCluster 2>$null
Write-Step "Deleting existing clusters..."
& minikube delete -p $WestCluster 2>$null
& minikube delete -p $EastCluster 2>$null

Write-Step "Removing existing Docker network..."
$existingNetwork = docker network ls --filter name=$SharedNetwork --format "{{.Name}}"
if ($existingNetwork -eq $SharedNetwork) {
    docker network rm $SharedNetwork 2>$null | Out-Null
}

Write-Success "Cleanup complete"

# Step 2: Create shared Docker network
Write-Header "Creating Shared Docker Network"

Write-Step "Creating network: $SharedNetwork"
$networkResult = docker network create --driver bridge --subnet 172.19.0.0/16 $SharedNetwork
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create Docker network"
    exit 1
}
Write-Success "Network created: $SharedNetwork"

# Step 3: Create Minikube clusters
Write-Header "Creating Minikube Clusters"

Write-Step "Creating west cluster..."
Write-Info "This may take 3-5 minutes..."
$westStartArguments = @(
  "start",
  "-p", $WestCluster,
  "--driver=docker",
  "--cpus=4",
  "--memory=8192",
  "--insecure-registry=host.minikube.internal:5000"
)

if ($MinikubeBaseImage) {
  Write-Info "Using custom Minikube base image: $MinikubeBaseImage"
  $westStartArguments += "--base-image=$MinikubeBaseImage"
}

& minikube @westStartArguments

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create west cluster"
    exit 1
}

# Connect west cluster to shared network with static IP
Write-Step "Connecting west to shared network..."
docker network connect --ip 172.19.0.10 $SharedNetwork $WestCluster
Write-Success "West cluster created and connected"

Write-Step "Updating west kubeconfig context..."
& minikube update-context -p $WestCluster | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to update kubeconfig for west cluster"
    exit 1
}
Write-Success "West kubeconfig context ready"

Write-Step "Creating east cluster..."
Write-Info "This may take 3-5 minutes..."
$eastStartArguments = @(
  "start",
  "-p", $EastCluster,
  "--driver=docker",
  "--cpus=4",
  "--memory=8192",
  "--insecure-registry=host.minikube.internal:5000"
)

if ($MinikubeBaseImage) {
  $eastStartArguments += "--base-image=$MinikubeBaseImage"
}

& minikube @eastStartArguments

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create east cluster"
    exit 1
}

# Connect east cluster to shared network with static IP
Write-Step "Connecting east to shared network..."
docker network connect --ip 172.19.0.20 $SharedNetwork $EastCluster
Write-Success "East cluster created and connected"

Write-Step "Updating east kubeconfig context..."
& minikube update-context -p $EastCluster | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to update kubeconfig for east cluster"
    exit 1
}
Write-Success "East kubeconfig context ready"

# Step 4: Trust corporate certificates
if ($CertificateDefinitions.Count -gt 0) {
    Write-Header "Trusting Corporate Certificates"

    Ensure-CertificateDownloads

    Install-ClusterCertificates -ClusterName $WestCluster -RegistryHost $CustomImageRegistry
    Install-ClusterCertificates -ClusterName $EastCluster -RegistryHost $CustomImageRegistry
} else {
    Write-Info "TRUST_CERTIFICATES not set in deployment.env — skipping certificate trust step."
}

# Step 5: Create namespaces
Write-Header "Creating Namespaces"

Write-Step "Creating namespace in west..."
kubectl --context $WestCluster create namespace $Namespace 2>$null
Write-Success "West namespace ready"

Write-Step "Creating namespace in east..."
kubectl --context $EastCluster create namespace $Namespace 2>$null
Write-Success "East namespace ready"

# Step 6: Create registry image pull secrets
Write-Header "Creating Registry Image Pull Secrets"

if ($CustomImageRegistry -and $CustomRegistryCredential) {
    Ensure-CustomRegistryImagePullSecret -ClusterContext $WestCluster -Namespace $Namespace -Registry $CustomImageRegistry -Credential $CustomRegistryCredential
    Ensure-CustomRegistryImagePullSecret -ClusterContext $EastCluster -Namespace $Namespace -Registry $CustomImageRegistry -Credential $CustomRegistryCredential
}

# Step 7: Enable addons
Write-Header "Enabling Cluster Addons"

Write-Step "Enabling west cluster addons..."
& minikube -p $WestCluster addons enable ingress
& minikube -p $WestCluster addons enable metallb
Write-Success "West addons enabled"

Write-Step "Enabling east cluster addons..."
& minikube -p $EastCluster addons enable ingress
& minikube -p $EastCluster addons enable metallb
Write-Success "East addons enabled"

# Step 8: Get cluster IPs
Write-Header "Configuring MetalLB"

$westIP = & minikube -p $WestCluster ip
$eastIP = & minikube -p $EastCluster ip

Write-Info "West cluster IP: $westIP"
Write-Info "East cluster IP: $eastIP"

# Configure MetalLB for west
Write-Step "Configuring MetalLB for west..."
@"
apiVersion: v1
kind: ConfigMap
metadata:
  namespace: metallb-system
  name: config
data:
  config: |
    address-pools:
    - name: default
      protocol: layer2
      addresses:
      - 172.19.1.100-172.19.1.110
"@ | kubectl --context $WestCluster apply -f -

Write-Success "West MetalLB configured"

# Configure MetalLB for east
Write-Step "Configuring MetalLB for east..."
@"
apiVersion: v1
kind: ConfigMap
metadata:
  namespace: metallb-system
  name: config
data:
  config: |
    address-pools:
    - name: default
      protocol: layer2
      addresses:
      - 172.19.2.100-172.19.2.110
"@ | kubectl --context $EastCluster apply -f -

Write-Success "East MetalLB configured"

# Step 9: Get cluster node IPs for MongoDB
Write-Header "Getting Cluster Network Information"

$westNodeIP = docker inspect -f "{{(index .NetworkSettings.Networks `"$SharedNetwork`" ).IPAddress}}" $WestCluster
$eastNodeIP = docker inspect -f "{{(index .NetworkSettings.Networks `"$SharedNetwork`" ).IPAddress}}" $EastCluster

Write-Info "West node IP: $westNodeIP"
Write-Info "East node IP: $eastNodeIP"

# Step 10: Deploy MongoDB
Write-Header "Deploying MongoDB Replica Set"

Write-Step "Creating MongoDB manifests..."
$mongoDir = Join-Path $PSScriptRoot "kubernetes\mongodb"
New-Item -ItemType Directory -Force -Path $mongoDir | Out-Null

# MongoDB StatefulSet for west (primary + 1 secondary)
@"
apiVersion: v1
kind: Service
metadata:
  name: mongodb-west
  namespace: $Namespace
spec:
  type: LoadBalancer
  ports:
  - port: 27017
    targetPort: 27017
  selector:
    app: mongodb-west
---
apiVersion: v1
kind: Service
metadata:
  name: mongodb-west-headless
  namespace: $Namespace
spec:
  clusterIP: None
  ports:
  - port: 27017
    targetPort: 27017
  selector:
    app: mongodb-west
---
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: mongodb-west
  namespace: $Namespace
spec:
  serviceName: mongodb-west-headless
  replicas: 2
  selector:
    matchLabels:
      app: mongodb-west
  template:
    metadata:
      labels:
        app: mongodb-west
    spec:
      imagePullSecrets:
      - name: registry-credentials
      containers:
      - name: mongodb
        image: $MongoImage
        ports:
        - containerPort: 27017
        env:
        - name: MONGO_INITDB_ROOT_USERNAME
          value: admin
        - name: MONGO_INITDB_ROOT_PASSWORD
          value: password
        command:
        - mongod
        - --replSet
        - rs0
        - --bind_ip_all
        volumeMounts:
        - name: mongo-data
          mountPath: /data/db
  volumeClaimTemplates:
  - metadata:
      name: mongo-data
    spec:
      accessModes: ["ReadWriteOnce"]
      resources:
        requests:
          storage: 1Gi
"@ | Set-Content -Path "$mongoDir\mongodb-west.yaml"

# MongoDB StatefulSet for east (1 secondary)
@"
apiVersion: v1
kind: Service
metadata:
  name: mongodb-east
  namespace: $Namespace
spec:
  type: LoadBalancer
  ports:
  - port: 27017
    targetPort: 27017
  selector:
    app: mongodb-east
---
apiVersion: v1
kind: Service
metadata:
  name: mongodb-east-headless
  namespace: $Namespace
spec:
  clusterIP: None
  ports:
  - port: 27017
    targetPort: 27017
  selector:
    app: mongodb-east
---
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: mongodb-east
  namespace: $Namespace
spec:
  serviceName: mongodb-east-headless
  replicas: 1
  selector:
    matchLabels:
      app: mongodb-east
  template:
    metadata:
      labels:
        app: mongodb-east
    spec:
      imagePullSecrets:
      - name: registry-credentials
      containers:
      - name: mongodb
        image: $MongoImage
        ports:
        - containerPort: 27017
        env:
        - name: MONGO_INITDB_ROOT_USERNAME
          value: admin
        - name: MONGO_INITDB_ROOT_PASSWORD
          value: password
        command:
        - mongod
        - --replSet
        - rs0
        - --bind_ip_all
        volumeMounts:
        - name: mongo-data
          mountPath: /data/db
  volumeClaimTemplates:
  - metadata:
      name: mongo-data
    spec:
      accessModes: ["ReadWriteOnce"]
      resources:
        requests:
          storage: 1Gi
"@ | Set-Content -Path "$mongoDir\mongodb-east.yaml"

Write-Success "MongoDB manifests created"

Write-Step "Deploying MongoDB to west cluster..."
kubectl --context $WestCluster apply -f "$mongoDir\mongodb-west.yaml"
Write-Success "West MongoDB deployed"

Write-Step "Deploying MongoDB to east cluster..."
kubectl --context $EastCluster apply -f "$mongoDir\mongodb-east.yaml"
Write-Success "East MongoDB deployed"

Write-Step "Waiting for MongoDB pods..."
kubectl --context $WestCluster wait --for=condition=ready pod -l app=mongodb-west -n $Namespace --timeout=300s
kubectl --context $EastCluster wait --for=condition=ready pod -l app=mongodb-east -n $Namespace --timeout=300s
Write-Success "MongoDB pods ready"

# Step 11: Initialize MongoDB replica set
Write-Header "Initializing MongoDB Replica Set"

Write-Step "Getting MongoDB LoadBalancer IPs..."
Start-Sleep -Seconds 10

$westMongoIP = Get-LoadBalancerIp -ClusterContext $WestCluster -Namespace $Namespace -ServiceName "mongodb-west"
$eastMongoIP = Get-LoadBalancerIp -ClusterContext $EastCluster -Namespace $Namespace -ServiceName "mongodb-east"

if (-not $westMongoIP -or -not $eastMongoIP) {
  Write-Error "Failed to resolve MongoDB LoadBalancer IPs. Cannot initialize replica set safely."
  exit 1
}

Write-Info "West MongoDB IP: $westMongoIP"
Write-Info "East MongoDB IP: $eastMongoIP"

$westReplicaHost = "mongodb-west-0.mongodb-west-headless.${Namespace}.svc.cluster.local:27017"

Write-Step "Initializing replica set..."
$initScript = @"
rs.initiate({
  _id: 'rs0',
  members: [
    { _id: 0, host: '$westReplicaHost', priority: 1 },
    { _id: 1, host: '${eastMongoIP}:27017', priority: 2 }
  ]
})
"@

kubectl --context $WestCluster exec mongodb-west-0 -n $Namespace -- mongosh --username admin --password password --authenticationDatabase admin --eval "$initScript" | Out-Null

if ($LASTEXITCODE -ne 0) {
  Write-Info "Authentication/init command failed; retrying without authentication..."
  kubectl --context $WestCluster exec mongodb-west-0 -n $Namespace -- mongosh --eval "$initScript" | Out-Null
  if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to initialize replica set"
    exit 1
  }
}

Write-Success "Replica set initialized"

# Step 12: Summary
Write-Header "Deployment Complete"

Write-Info "Cluster Information:"
Write-Info ""
Write-Info "  West Cluster:"
Write-Info "    Context: $WestCluster"
Write-Info "    Node IP: $westNodeIP"
Write-Info "    MongoDB IP: $westMongoIP"
Write-Info ""
Write-Info "  East Cluster:"
Write-Info "    Context: $EastCluster"
Write-Info "    Node IP: $eastNodeIP"
Write-Info "    MongoDB IP: $eastMongoIP"
Write-Info ""
Write-Info "  Shared Network: $SharedNetwork (172.19.0.0/16)"
Write-Info ""
Write-Info "MongoDB Replica Set:"
Write-Info "  Connection string: mongodb://admin:password@${westMongoIP}:27017,${eastMongoIP}:27017/?replicaSet=rs0"
Write-Info ""
Write-Info "Image Pull Secrets:"
Write-Info "  registry-credentials (custom registry: $CustomImageRegistry)"
Write-Info ""
Write-Info "Next Steps:"
Write-Info "  1. Verify replica set status:"
Write-Info "     kubectl --context $WestCluster exec mongodb-west-0 -n $Namespace -- mongosh --username admin --password password --authenticationDatabase admin --eval 'rs.status()'"
Write-Info ""
Write-Info "  2. Deploy FeatBit:"
Write-Info "     .\Deploy-FeatBitClusters.ps1"
Write-Info ""

if (Test-Path $CertificateTempDirectory) {
    Remove-Item -Path $CertificateTempDirectory -Recurse -Force
}

