#!/bin/bash
#
# Test-WSL2-Networking.sh
# 
# This script tests if Rancher Desktop + WSL2 + Minikube provides
# direct network access from Windows to MetalLB LoadBalancer IPs.
#
# Usage: Run from WSL2 Ubuntu terminal
#   bash Test-WSL2-Networking.sh
#

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${CYAN}  Rancher Desktop + WSL2 + Minikube Network Test${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo ""

# Step 1: Check prerequisites
echo -e "${YELLOW}Step 1: Checking prerequisites...${NC}"

if ! command -v kubectl &> /dev/null; then
    echo -e "${RED}✗ kubectl not found${NC}"
    echo -e "${YELLOW}Installing kubectl...${NC}"
    curl -LO "https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl"
    chmod +x kubectl
    sudo mv kubectl /usr/local/bin/
    echo -e "${GREEN}✓ kubectl installed${NC}"
fi

if ! command -v minikube &> /dev/null; then
    echo -e "${RED}✗ minikube not found${NC}"
    echo -e "${YELLOW}Installing minikube...${NC}"
    curl -LO https://storage.googleapis.com/minikube/releases/latest/minikube-linux-amd64
    sudo install minikube-linux-amd64 /usr/local/bin/minikube
    rm minikube-linux-amd64
    echo -e "${GREEN}✓ minikube installed${NC}"
fi

echo -e "${GREEN}✓ Prerequisites OK${NC}"
echo ""

# Step 2: Create test cluster
echo -e "${YELLOW}Step 2: Creating test Minikube cluster...${NC}"
echo -e "${CYAN}This may take 2-3 minutes...${NC}"

# Delete if exists
minikube delete -p network-test 2>/dev/null || true

# Create new cluster with docker driver
minikube start -p network-test \
    --driver=docker \
    --cpus=2 \
    --memory=2048 \
    --addons=ingress,metallb

echo -e "${GREEN}✓ Test cluster created${NC}"
echo ""

# Step 3: Configure MetalLB
echo -e "${YELLOW}Step 3: Configuring MetalLB...${NC}"

# Get minikube IP
MINIKUBE_IP=$(minikube -p network-test ip)
echo -e "${CYAN}Minikube IP: ${MINIKUBE_IP}${NC}"

# Calculate IP range (use last octet 200-210)
IFS='.' read -r i1 i2 i3 i4 <<< "$MINIKUBE_IP"
IP_RANGE="${i1}.${i2}.${i3}.200-${i1}.${i2}.${i3}.210"
echo -e "${CYAN}MetalLB IP Range: ${IP_RANGE}${NC}"

# Wait for MetalLB to be ready
echo -e "${CYAN}Waiting for MetalLB controller to be ready...${NC}"
kubectl --context network-test wait --namespace metallb-system \
    --for=condition=ready pod \
    --selector=app=metallb \
    --timeout=90s 2>/dev/null || true

sleep 10

# Check MetalLB version and use appropriate API
echo -e "${CYAN}Checking MetalLB API version...${NC}"
if kubectl --context network-test get crd ipaddresspools.metallb.io 2>/dev/null; then
    # v1beta1 API available
    echo -e "${CYAN}Using MetalLB v1beta1 API${NC}"
    kubectl --context network-test apply -f - <<EOF
apiVersion: metallb.io/v1beta1
kind: IPAddressPool
metadata:
  name: test-pool
  namespace: metallb-system
spec:
  addresses:
  - ${IP_RANGE}
---
apiVersion: metallb.io/v1beta1
kind: L2Advertisement
metadata:
  name: test-adv
  namespace: metallb-system
EOF
else
    # Old v1alpha1 API (ConfigMap-based)
    echo -e "${CYAN}Using MetalLB v1alpha1 API (ConfigMap)${NC}"
    kubectl --context network-test apply -f - <<EOF
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
      - ${IP_RANGE}
EOF
fi

echo -e "${GREEN}✓ MetalLB configured${NC}"
echo ""

# Step 4: Deploy test service
echo -e "${YELLOW}Step 4: Deploying test LoadBalancer service...${NC}"

kubectl --context network-test apply -f - <<EOF
apiVersion: v1
kind: Namespace
metadata:
  name: network-test
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: test-nginx
  namespace: network-test
spec:
  replicas: 1
  selector:
    matchLabels:
      app: test-nginx
  template:
    metadata:
      labels:
        app: test-nginx
    spec:
      containers:
      - name: nginx
        image: nginx:alpine
        ports:
        - containerPort: 80
---
apiVersion: v1
kind: Service
metadata:
  name: test-nginx
  namespace: network-test
spec:
  type: LoadBalancer
  selector:
    app: test-nginx
  ports:
  - port: 80
    targetPort: 80
EOF

echo -e "${GREEN}✓ Test service deployed${NC}"
echo ""

# Step 5: Wait for LoadBalancer IP
echo -e "${YELLOW}Step 5: Waiting for LoadBalancer IP assignment...${NC}"

for i in {1..30}; do
    LB_IP=$(kubectl --context network-test get svc test-nginx -n network-test -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || echo "")
    if [ -n "$LB_IP" ]; then
        break
    fi
    echo -n "."
    sleep 2
done
echo ""

if [ -z "$LB_IP" ]; then
    echo -e "${RED}✗ LoadBalancer IP not assigned after 60 seconds${NC}"
    exit 1
fi

echo -e "${GREEN}✓ LoadBalancer IP assigned: ${LB_IP}${NC}"
echo ""

# Step 6: Test from WSL2
echo -e "${YELLOW}Step 6: Testing access from WSL2...${NC}"

sleep 5  # Give nginx time to fully start

if curl -s --connect-timeout 5 http://${LB_IP} | grep -q "Welcome to nginx"; then
    echo -e "${GREEN}✓ SUCCESS! WSL2 can access LoadBalancer IP${NC}"
else
    echo -e "${RED}✗ FAILED! WSL2 cannot access LoadBalancer IP${NC}"
    exit 1
fi
echo ""

# Step 7: Test from Windows
echo -e "${YELLOW}Step 7: Testing access from Windows...${NC}"
echo -e "${CYAN}Creating PowerShell test script...${NC}"

# Create PowerShell script in /mnt/c/Users/...
WIN_USER=$(cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r')
WIN_HOME="/mnt/c/Users/${WIN_USER}"
PS_SCRIPT="${WIN_HOME}/Desktop/test-network.ps1"

cat > "$PS_SCRIPT" <<PSEOF
Write-Host "Testing LoadBalancer IP from Windows..." -ForegroundColor Cyan
Write-Host "LoadBalancer IP: ${LB_IP}" -ForegroundColor Yellow
Write-Host ""

try {
    \$response = Invoke-WebRequest -Uri "http://${LB_IP}" -UseBasicParsing -TimeoutSec 10
    if (\$response.StatusCode -eq 200) {
        Write-Host "✓ SUCCESS! Windows can access LoadBalancer IP" -ForegroundColor Green
        Write-Host "  Status: \$(\$response.StatusCode)" -ForegroundColor Gray
        Write-Host "  Content Length: \$(\$response.Content.Length) bytes" -ForegroundColor Gray
        Write-Host ""
        Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Green
        Write-Host "  RESULT: WSL2 + Minikube networking WORKS!" -ForegroundColor Green
        Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Green
        Write-Host ""
        Write-Host "You can deploy FeatBit in WSL2 without port forwarding!" -ForegroundColor Cyan
        exit 0
    }
} catch {
    Write-Host "✗ FAILED! Windows cannot access LoadBalancer IP" -ForegroundColor Red
    Write-Host "  Error: \$(\$_.Exception.Message)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Red
    Write-Host "  RESULT: WSL2 + Minikube networking does NOT work" -ForegroundColor Red
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Red
    Write-Host ""
    Write-Host "Recommendation: Use Hyper-V driver instead" -ForegroundColor Yellow
    exit 1
}
PSEOF

echo -e "${GREEN}✓ PowerShell test script created on Desktop${NC}"
echo ""

echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${CYAN}  Test Setup Complete!${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo ""
echo -e "${YELLOW}WSL2 Results:${NC}"
echo -e "${GREEN}  ✓ Cluster created: network-test${NC}"
echo -e "${GREEN}  ✓ LoadBalancer IP: ${LB_IP}${NC}"
echo -e "${GREEN}  ✓ WSL2 can access the LoadBalancer${NC}"
echo ""
echo -e "${YELLOW}Next Step:${NC}"
echo -e "${CYAN}  Run the PowerShell script from Windows to test Windows access:${NC}"
echo -e "${GREEN}  1. Open PowerShell on Windows${NC}"
echo -e "${GREEN}  2. Run: ~/Desktop/test-network.ps1${NC}"
echo ""
echo -e "${CYAN}Or run this from Windows PowerShell:${NC}"
echo -e "${GREEN}  Invoke-WebRequest -Uri http://${LB_IP} -UseBasicParsing${NC}"
echo ""
echo -e "${YELLOW}To clean up test cluster after:${NC}"
echo -e "${CYAN}  minikube delete -p network-test${NC}"
echo ""
