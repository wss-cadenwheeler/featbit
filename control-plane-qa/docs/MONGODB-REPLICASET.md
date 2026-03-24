# MongoDB Replica Set Configuration

This document describes the MongoDB replica set setup for FeatBit Pro across multiple Kubernetes clusters.

## Architecture

### Replica Set Topology

```
┌─────────────────────────────────────────────────────────┐
│                  MongoDB Replica Set                     │
│                     "rs-featbit"                         │
│                                                          │
│  ┌──────────────────┐         ┌──────────────────┐     │
│  │  WEST CLUSTER    │         │  EAST CLUSTER    │     │
│  │                  │         │                  │     │
│  │  ┌────────────┐  │         │  ┌────────────┐  │     │
│  │  │ mongodb-0  │  │         │  │ mongodb-2  │  │     │
│  │  │ (Primary)  │  │         │  │ (Secondary)│  │     │
│  │  │ Priority: 2│  │         │  │ Priority: 1│  │     │
│  │  └────────────┘  │         │  └────────────┘  │     │
│  │         │        │         │         │        │     │
│  │  ┌────────────┐  │         │         │        │     │
│  │  │ mongodb-1  │  │         │         │        │     │
│  │  │ (Secondary)│  │         │         │        │     │
│  │  │ Priority: 1│  │         │         │        │     │
│  │  └────────────┘  │         │         │        │     │
│  └──────────────────┘         └──────────────────┘     │
│                                                          │
└─────────────────────────────────────────────────────────┘
```

### Configuration Details

- **Replica Set Name**: `rs-featbit`
- **Total Members**: 3
  - **West Cluster**: 2 replicas (mongodb-west-0, mongodb-west-1)
  - **East Cluster**: 1 replica (mongodb-east-0)
- **Priority Settings**:
  - mongodb-west-0: Priority 2 (preferred primary)
  - mongodb-west-1: Priority 1 (can become primary)
  - mongodb-east-0: Priority 1 (can become primary)

## Networking

### Cross-Cluster Connectivity

Since pods in separate Minikube clusters cannot directly communicate, we use a combination of:

1. **LoadBalancer Services**: Each MongoDB pod is exposed via a LoadBalancer service
2. **Port Forwarding**: kubectl port-forward maps LoadBalancer IPs to localhost
3. **DNS Resolution**: hosts file entries map DNS names to localhost
4. **Replica Set Configuration**: MongoDB configured with DNS names

### Port Mappings

| MongoDB Pod | DNS Name | localhost Port | Cluster | Service Name |
|------------|----------|----------------|---------|--------------|
| mongodb-west-0 | mongodb-0.west.local | 27017 | west | mongodb-0-lb |
| mongodb-west-1 | mongodb-1.west.local | 27018 | west | mongodb-1-lb |
| mongodb-east-0 | mongodb-2.east.local | 27019 | east | mongodb-2-lb |

### hosts File Entries

```
127.0.0.1 mongodb-0.west.local mongodb-1.west.local mongodb-2.east.local
```

## Connection String

### Replica Set URI

```
mongodb://admin:password@mongodb-0.west.local:27017,mongodb-1.west.local:27018,mongodb-2.east.local:27019/featbit?replicaSet=rs-featbit&authSource=admin
```

### Environment Variable Format

```yaml
env:
- name: MongoDb__ConnectionString
  value: "mongodb://admin:password@mongodb-0.west.local:27017,mongodb-1.west.local:27018,mongodb-2.east.local:27019/featbit?replicaSet=rs-featbit&authSource=admin"
- name: MongoDb__Database
  value: "featbit"
```

## Deployment Steps

### 1. Deploy Infrastructure

The deployment script automatically deploys MongoDB StatefulSets:

```powershell
.\Deploy-FeatBitClusters.ps1
```

This will:
- Deploy MongoDB StatefulSet to west cluster (2 replicas)
- Deploy MongoDB StatefulSet to east cluster (1 replica)
- Create LoadBalancer services for each pod
- Wait for pods to be ready

### 2. Start Port Forwards

Start the port forwarding script to enable cross-cluster communication:

```powershell
.\Start-PortForwards.ps1
```

This runs in the background and maintains port forwards for:
- Application services (UI, API, Evaluation)
- MongoDB replica set members

### 3. Initialize Replica Set

Once port forwards are running, initialize the replica set:

```powershell
.\Initialize-MongoDBReplicaSet.ps1
```

This script will:
1. Verify MongoDB pods are running
2. Check port forwards are active
3. Execute `rs.initiate()` with all members
4. Wait for replica set to stabilize
5. Verify replica set status
6. Run database initialization script (seed data)

### 4. Update Application ConfigMaps

Update FeatBit application ConfigMaps to use the replica set connection string:

**West Cluster:**
```powershell
kubectl --context west edit configmap api-server-config -n featbit
```

**East Cluster:**
```powershell
kubectl --context east edit configmap api-server-config -n featbit
```

Update the `MongoDb__ConnectionString` value to:
```
mongodb://admin:password@mongodb-0.west.local:27017,mongodb-1.west.local:27018,mongodb-2.east.local:27019/featbit?replicaSet=rs-featbit&authSource=admin
```

### 5. Restart Application Pods

Restart FeatBit pods to pick up the new connection string:

```powershell
# West cluster
kubectl --context west rollout restart deployment api-server -n featbit
kubectl --context west rollout restart deployment evaluation-server -n featbit

# East cluster
kubectl --context east rollout restart deployment api-server -n featbit
kubectl --context east rollout restart deployment evaluation-server -n featbit
```

## Management

### Check Replica Set Status

Connect to the primary and check status:

```bash
mongosh --host localhost:27017 -u admin -p password --authenticationDatabase admin
```

```javascript
rs.status()
```

### Check Replication Lag

```javascript
rs.printReplicationInfo()
```

### Check Member Health

```javascript
rs.conf()
```

### Force Re-election (if needed)

```javascript
rs.stepDown()
```

## Failover Behavior

### Automatic Failover

If the primary (mongodb-west-0) fails:
1. Replica set automatically elects a new primary
2. Election considers priority settings
3. mongodb-west-1 or mongodb-east-0 becomes primary
4. Applications automatically reconnect to new primary

### Priority-Based Election

- mongodb-west-0 (Priority: 2) - Preferred primary
- If mongodb-west-0 is unavailable, mongodb-west-1 or mongodb-east-0 can become primary
- When mongodb-west-0 recovers, it will become primary again

## Troubleshooting

### Pods Not Starting

Check pod status:
```powershell
kubectl --context west get pods -n featbit -l app=mongodb
kubectl --context east get pods -n featbit -l app=mongodb
```

View logs:
```powershell
kubectl --context west logs -n featbit mongodb-west-0
kubectl --context east logs -n featbit mongodb-east-0
```

### Port Forwards Not Working

Check if port forwards are running:
```powershell
Get-NetTCPConnection -LocalPort 27017,27018,27019
```

Restart port forwards:
```powershell
.\Stop-PortForwards.ps1
.\Start-PortForwards.ps1
```

### Replica Set Initialization Failed

Check DNS resolution:
```powershell
# Should resolve to 127.0.0.1
nslookup mongodb-0.west.local
nslookup mongodb-1.west.local
nslookup mongodb-2.east.local
```

Check connectivity:
```powershell
Test-NetConnection -ComputerName localhost -Port 27017
Test-NetConnection -ComputerName localhost -Port 27018
Test-NetConnection -ComputerName localhost -Port 27019
```

Manual initialization:
```bash
mongosh --host localhost:27017 -u admin -p password --authenticationDatabase admin
```

```javascript
rs.initiate({
  _id: 'rs-featbit',
  members: [
    { _id: 0, host: 'mongodb-0.west.local:27017', priority: 2 },
    { _id: 1, host: 'mongodb-1.west.local:27018', priority: 1 },
    { _id: 2, host: 'mongodb-2.east.local:27019', priority: 1 }
  ]
})
```

### Data Not Replicating

Check replica set status:
```javascript
rs.status()
```

Look for:
- All members should show `state: 2 (SECONDARY)` or `state: 1 (PRIMARY)`
- `health: 1` for all members
- No replication lag

Check oplog:
```javascript
use local
db.oplog.rs.find().limit(10).sort({$natural:-1})
```

## Production Considerations

For production deployments with real multi-zone clusters:

1. **Network Connectivity**: Use proper network connectivity between clusters
   - Service mesh (Istio, Linkerd)
   - VPN/Tunnel (Submariner, Cilium Cluster Mesh)
   - Direct network routing

2. **Persistent Storage**: Use proper storage classes with backups
   - Cloud provider persistent disks
   - Replicated storage (Longhorn, Rook/Ceph)

3. **Resource Limits**: Adjust based on workload
   - Memory: 2-4GB per pod minimum
   - CPU: 1-2 cores per pod minimum
   - Storage: Based on data size + growth

4. **Monitoring**: Add monitoring and alerting
   - MongoDB Atlas Cloud Manager
   - Prometheus + MongoDB Exporter
   - Grafana dashboards

5. **Backups**: Implement backup strategy
   - Regular snapshots
   - Point-in-time recovery
   - Cross-region backup replication

6. **Security**: Enhance security
   - TLS/SSL for connections
   - Strong passwords
   - Network policies
   - RBAC

## Files

- `kubernetes/pro/infrastructure/mongodb-west-statefulset.yaml` - West cluster MongoDB StatefulSet
- `kubernetes/pro/infrastructure/mongodb-east-statefulset.yaml` - East cluster MongoDB StatefulSet
- `Initialize-MongoDBReplicaSet.ps1` - Replica set initialization script
- `Start-PortForwards.ps1` - Port forwarding management
- `Setup-FeatBitProxy.ps1` - nginx proxy and DNS configuration
