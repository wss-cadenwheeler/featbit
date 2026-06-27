# Per-cluster Redis + Sentinel for FeatBit

Replaces the single **shared host redis** (a continuity gap surfaced by the
active/passive cutover simulation) with an **independent HA Redis per cluster**:
each of west and east runs its own Redis (1 master + 2 replicas) with 3 Sentinels,
and that cluster's FeatBit connects to **its own** Sentinel. No redis is shared
between DCs.

## How FeatBit connects (no code change)

FeatBit's .NET services (api-server, evaluation-server, control-plane) use
StackExchange.Redis 2.13.1, which resolves the Sentinel-elected master from the
connection string's `serviceName=` keyword. So this is **configuration only**:

```
Redis__ConnectionString = featbit-redis:26379,serviceName=mymaster
```

- `featbit-redis:26379` — the in-cluster Sentinel service (bitnami chart).
- `serviceName=mymaster` — the Sentinel master set; StackExchange asks Sentinel
  for the current master and connects to it, following failover automatically.

Verified: with this string, the eval/api pods appear in the Sentinel-elected
master's `CLIENT LIST` and serve flags; on master failover Sentinel re-elects and
StackExchange reconnects to the new master.

## Files

- `values.yaml` — bitnami/redis chart 23.2.12 (appVersion 8.2.3) values:
  `architecture=replication`, `sentinel.enabled`, 3 nodes, no auth, no persistence
  (ephemeral; api-server repopulates redis from MongoDB on startup), networkPolicy
  off, images pinned to `bitnamilegacy/redis(-sentinel):8.2.1` (bitnami moved the
  free Docker Hub images under `bitnamilegacy/*`).
- `Deploy-RedisSentinel.ps1` — installs the chart into each cluster and points that
  cluster's FeatBit api/eval/control-plane at its own Sentinel. Idempotent.

## Deploy

Automatic: `Deploy-FeatBitClusters.ps1` calls this at the end when
`-UseRedisSentinel` is true (the default). Standalone / re-run:

```powershell
pwsh ./Deploy-RedisSentinel.ps1 -Contexts west,east
```

## Verify

```bash
# master per cluster
kubectl --context west -n featbit exec featbit-redis-node-0 -c sentinel -- \
  redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster
# flags served via each cluster's own sentinel-backed eval
kubectl --context <c> -n featbit get deploy evaluation-server \
  -o jsonpath='{..env[?(@.name=="Redis__ConnectionString")].value}{"\n"}'
```

## Notes / follow-ups

- **Cross-cluster** control-plane redis (`Redis__Instances__1`, the *other* DC) is
  left as configured by the main deploy; in BestEffort consistency mode it is
  non-critical. Pointing it at the peer cluster's Sentinel needs cross-cluster
  exposure (NodePort/LB) and is a follow-up. The **local** instance
  (`Redis__Instances__0`) is on the local Sentinel.
- The legacy host redis (`featbit-infra-redis-1:6379`) becomes **orphaned** once
  FeatBit is on Sentinel (no FeatBit clients). It can be removed from
  `HostInfraComponents`.
- Storage is ephemeral by design for this QA harness; enable `master.persistence`
  /`replica.persistence` in `values.yaml` for durable data.
- A passive cluster's eval may not write the cp09 `featbit:heartbeat:all` entry
  until it has active streaming clients (observed: heartbeat present on the active
  DC, absent on the idle/passive DC); it populates when that DC becomes active.
