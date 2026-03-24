# FeatBit Control Plane Testing Plan

## Current State
- ✅ Deployed to Rancher Desktop (single cluster)
- ✅ Configured ingress with *.east.local and *.west.local (internal DNS → 127.0.0.1)
- ✅ Enabled UseControlPlane=true
- ✅ Kafka UI deployed for monitoring

## Testing Strategy: Two-Phase Approach

### Phase 1: Single-Cluster Testing (Rancher Desktop)
Test all control plane features that work in single-cluster mode.

**Testable Features:**
- ✅ Client connection tracking across eval server pods
- ✅ Admin command distribution via Kafka topics
- ✅ Heartbeat mechanism for ghost connection cleanup
- ✅ Message routing through cp-* topics
- ✅ Control plane consuming and republishing messages
- ✅ Kafka topic flow visualization

**Current Setup:**
- System: 4 cores, 8GB RAM (79% utilization)
- Network: *.local → 127.0.0.1 (internal DNS)
- Adequate for single-cluster testing

### Phase 2: Multi-Cluster Testing (Multi-DC Simulation)
Test Redis synchronization and cross-DC features.

**Testable Features:**
- Redis synchronization across multiple DCs
- Cross-DC failover scenarios
- Race condition validation with replication lag
- Kafka MirrorMaker integration
- Control plane synchronizing multiple Redis instances

**Options:**

#### Option A: Local Multi-Cluster (Minikube)
**Requirements:**
- CPU: 10 cores minimum (12 recommended)
- RAM: 16GB minimum (20GB recommended)
- Disk: 50GB minimum

**Setup:**
```bash
# DC1 cluster
minikube start -p featbit-dc1 --cpus=5 --memory=7168 --disk-size=25g

# DC2 cluster
minikube start -p featbit-dc2 --cpus=5 --memory=7168 --disk-size=25g
```

**Pros:** 
- Full control, no cloud costs
- Faster iteration

**Cons:** 
- May exceed local hardware
- Doesn't simulate real network latency

#### Option B: Cloud Multi-Cluster (Azure AKS)
**Requirements:**
- Azure subscription (developer account)
- 2 AKS clusters in different regions

**Setup:**
```bash
# Create DC1 (East US)
az aks create --resource-group featbit-test \
  --name featbit-dc1 --location eastus \
  --node-count 2 --node-vm-size Standard_D2s_v3

# Create DC2 (West US)
az aks create --resource-group featbit-test \
  --name featbit-dc2 --location westus \
  --node-count 2 --node-vm-size Standard_D2s_v3
```

**Pros:**
- Realistic network latency
- Proper multi-region setup
- Can be shared with reviewers

**Cons:**
- Cloud costs (~$200-300/month if left running)
- Slower iteration

**Recommendation:** Check local hardware first. If < 10 cores or < 16GB RAM, use Azure.

## DNS/Networking Plan

### Problem
- Current: *.<internal-local-dns> → 127.0.0.1 (internal only, single host)
- GitHub reviewers don't have access to internal-local-dns infrastructure
- Need solution that works for:
  - Local testing (developer machine)
  - Multi-cluster testing (cross-cluster communication)
  - External reviewers (documentation/reproduction)

### Solution: Three-Tiered Approach

#### Tier 1: /etc/hosts for Local Testing
**For single-cluster local development:**

```bash
# /etc/hosts (Linux/Mac) or C:\Windows\System32\drivers\etc\hosts (Windows)
127.0.0.1 featbit.local
127.0.0.1 featbit-api.local
127.0.0.1 featbit-eval.local
127.0.0.1 featbit-control-plane.local
127.0.0.1 featbit-kafka-ui.local
```

**Update ingress manifests:**
```yaml
host: featbit-api.local  # instead of internal-local-dns
```

**Pros:** Simple, no infrastructure needed, works for reviewers
**Cons:** Only works for single host, not multi-cluster

#### Tier 2: NodePort Services for Multi-Cluster
**For multi-cluster local (minikube) testing:**

Instead of ingress, use NodePort services for cross-cluster communication:

```yaml
# DC1 - api-server NodePort
apiVersion: v1
kind: Service
metadata:
  name: api-server-external
spec:
  type: NodePort
  ports:
    - port: 5000
      nodePort: 30100
  selector:
    app: api-server
```

**Access via:**
```bash
# Get minikube IP
minikube ip -p featbit-dc1  # e.g., 192.168.49.2

# Access API
curl http://192.168.49.2:30100/health
```

**IP Address Map:**
```
DC1 (featbit-dc1):
  Minikube IP: 192.168.49.2
  API:         192.168.49.2:30100
  Eval:        192.168.49.2:30101
  Control:     192.168.49.2:30102
  UI:          192.168.49.2:30103

DC2 (featbit-dc2):
  Minikube IP: 192.168.49.3
  API:         192.168.49.3:30100
  Eval:        192.168.49.3:30101
  Control:     192.168.49.3:30102
```

**Pros:** Works cross-cluster, simple IP mapping
**Cons:** Requires IP documentation, not production-like

#### Tier 3: Cloud Load Balancers
**For cloud (Azure AKS) multi-cluster testing:**

Use cloud provider LoadBalancer services with public IPs:

```yaml
# API Server LoadBalancer
apiVersion: v1
kind: Service
metadata:
  name: api-server
spec:
  type: LoadBalancer
  ports:
    - port: 80
      targetPort: 5000
  selector:
    app: api-server
```

**Access via public IPs:**
```
DC1 (East US):
  API:     http://20.10.10.100
  Eval:    http://20.10.10.101
  Control: http://20.10.10.102

DC2 (West US):
  API:     http://40.20.20.100
  Eval:    http://40.20.20.101
  Control: http://40.20.20.102
```

**Documentation for reviewers:**
```markdown
## Testing the Deployment

### Access URLs (Cloud Test Environment)
- DC1 UI: http://20.10.10.103
- DC1 API: http://20.10.10.100
- DC2 UI: http://40.20.20.103
- DC2 API: http://40.20.20.100

### Local Testing (Minikube)
See TESTING-LOCAL.md for /etc/hosts configuration and NodePort mappings.
```

**Pros:** Production-like, shareable with reviewers
**Cons:** Cloud costs, public IPs (secure with auth)

## Recommended DNS Strategy

### For PR Documentation
Provide **three deployment methods** so reviewers can choose:

1. **Single-Cluster Local (Simplest)**
   - Use /etc/hosts with *.local domains
   - Rancher Desktop or minikube single profile
   - Document in: `TESTING-SINGLE-CLUSTER.md`

2. **Multi-Cluster Local (Advanced)**
   - Use minikube multi-profile with NodePort
   - Provide IP address map template
   - Document in: `TESTING-MULTI-CLUSTER-LOCAL.md`

3. **Multi-Cluster Cloud (Most Realistic)**
   - Azure AKS with LoadBalancers
   - Provide sample terraform/helm configs
   - Document in: `TESTING-MULTI-CLUSTER-CLOUD.md`

### Implementation Priority
1. ✅ Complete single-cluster testing (current Rancher setup)
2. ⬜ Create /etc/hosts based ingress manifests
3. ⬜ Test with *.local domains locally
4. ⬜ Check developer machine specs
5. ⬜ If specs sufficient: Create minikube multi-profile setup
6. ⬜ If specs insufficient: Create Azure AKS setup
7. ⬜ Document all three approaches for reviewers

## System Requirements

### Single-Cluster (Current)
- **Sufficient:** 4-6 cores, 8-10GB RAM, 30GB disk

### Multi-Cluster Local (Minikube)
- **Minimum:** 10 cores, 16GB RAM, 50GB disk
- **Recommended:** 12 cores, 20GB RAM, 60GB disk
- **Check with:**
  ```bash
  # Linux
  nproc && free -h && df -h
  
  # Windows PowerShell
  (Get-WmiObject Win32_Processor).NumberOfLogicalProcessors
  [Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB)
  ```

### Multi-Cluster Cloud (Azure)
- Azure subscription required
- Estimated cost: ~$5-10/day if cleaned up nightly
- Monthly: ~$200-300 if left running

## Next Steps

### Immediate (This Session)
1. ⬜ Test control plane features on current Rancher setup
2. ⬜ Check developer machine hardware specs
3. ⬜ Decide: local multi-cluster or cloud

### Short Term
1. ⬜ Create *.local ingress manifests for reviewers
2. ⬜ Document /etc/hosts setup
3. ⬜ If local multi-cluster: Create minikube setup scripts
4. ⬜ If cloud: Create Azure AKS terraform/scripts

### Documentation
1. ⬜ Create three testing guides (single, multi-local, multi-cloud)
2. ⬜ Provide IP address map templates
3. ⬜ Include DNS setup instructions for each approach
4. ⬜ Add troubleshooting section for networking
