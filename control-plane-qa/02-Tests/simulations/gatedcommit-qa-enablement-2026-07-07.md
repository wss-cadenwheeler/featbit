# GatedCommit QA enablement + full CP-suite validation — 2026-07-07 (#25)

First live east/west validation of the complete Option-A stack (post PRs #81/#82/#85/#88/#96/#97).
`ConsistencyMode=GatedCommit` flipped in place on both clusters per the ops guide §5 rollout
procedure (mode-only `kubectl set env`; the Sentinel Redis wiring from `Deploy-RedisSentinel.ps1`
was preserved), on images built from `feat/control-plane-consistency-option-a` @ `afff1781`.

## Results

| Check | Result |
|---|---|
| `dotnet test -c Release` — back-end / control-plane / eval-server | 717/717 green (incl. Category=Integration; stricter than CI) |
| CI `build-and-test-api` / `build-and-test-els` (workflow_dispatch on the branch) | green (47s / 52s) |
| CI `build-and-test-control-plane` | not dispatchable — workflow file absent from the fork's default branch (GitHub constraint); covered by the local Release run |
| CP-10 happy path (gating + pointer convergence) | PASS |
| CP-11 eviction (survivor commits without partitioned DC) | PASS |
| CP-12 recovery (backfill + reconvergence) | PASS |
| CP-13 readiness fence (503 fenced / liveness 200 / recovery) | PASS — see nuance below |
| CP-14 mode toggle, full flip cycle (BestEffort → GatedCommit → rollback) | PASS |
| CP-15 cross-DC cache self-heal | PASS |

Watermarks observed end-to-end: both DC leases carried `appliedWatermarks` for 12 envs (the #69
pipeline live). Leader election, honest recovery metrics (#92), and the startup local+peer
reconciler refills all visible in logs on the new images.

## Findings (issues filed)

1. **#99 — heartbeat/lease default mismatch.** ELS default `HeartbeatIntervalSeconds=60` vs
   control-plane `LeaseTtlSeconds=15`: leases alive only 25% of the time, live set mostly empty,
   gated commits stall, recovery worker churns on phantom "returning DC" events. QA workaround:
   `ControlPlane__HeartbeatIntervalSeconds=5` on all ELS deployments — leases immediately stable.
2. **#100 — shared Kafka consumer group across DCs.** Static `group.id=featbit-control-plane` on a
   shared broker → single-owner change processing; during CP-11's partition the isolated DC owned
   the assignment, so the survivor could not commit. QA fix: per-cluster
   `Kafka__Consumer__group.id=featbit-control-plane-<dc>` (+ `auto.offset.reset=latest`) —
   CP-11/CP-12 deterministic-green afterwards.

## Test-harness fixes made during the run (committed with this report)

- `core/scenario_base.py` + `scenarios/consistency_base.py`: Redis observation now supports the
  Sentinel topology (`featbit-redis-node-*`, in-pod `redis-cli` against localhost, `-c redis`),
  and prefers it over the orphaned legacy `redis` service (which still resolves — to the wrong,
  empty Redis).
- `scenarios/cp14.py`: BestEffort assertions now check the committed pointer does not ADVANCE
  (pointer residue after a GatedCommit→BestEffort rollback is expected and harmless); ops
  commands (mode flips that wait out rollouts) get a 600s timeout instead of the 30s default.
- `chaos-mesh/cross-dc-partition.yaml`: external targets corrected to the real topology paths
  (peer node IPs 172.31.0.10/.20 + east's host gateway 192.168.58.1). The old target
  (172.31.128.1) matched no traffic — the "partition" was a no-op and the manifest's
  "templated at apply time" comment was wrong (the suite applies it verbatim).
- `chaos-mesh/eval-kafka-partition.yaml`: target changed from pods labeled `app: kafka` (none
  exist — kafka is an ExternalName to the host) to the eval pods' egress to the host gateway.
- `01-Infrastructure/extras/Set-ConsistencyMode.sh`: helper to flip mode on both clusters and
  wait for rollouts (used by CP-14's flip cycle).

## Caveats / nuances

- **CP-13**: the 503 arrived ~6s after the partition — the eval's Kafka *connectivity* health
  check trips before the 30s heartbeat-*freshness* fence gets a chance (the scenario's assertion
  message is canned and claims the latter). Operationally equivalent (pod leaves rotation while
  it cannot heartbeat; liveness stays 200; recovers on heal), but a pure freshness trip (kafka
  reachable, publishes failing) was not isolated. Threshold on east ELS lowered to 30s for the
  test (default 180s) and left in place.
- **Cluster-restart traps** (cost ~1h of the run; recorded for the next operator): minikube
  cannot re-provision a node attached to two docker networks ("container addresses should have
  2 values, got 3") — detach `featbit-cluster-network`, `minikube start`, reattach with the
  static IP. BUT a re-provision regenerates the apiserver cert for the minikube-network IP only,
  while etcd's manifest and the controller-manager/scheduler kubeconfigs may still reference the
  shared-network IP (172.31.0.10) from original provisioning — symptoms: etcd bind failure
  (fixed by the reattach) and controller-manager/scheduler TLS errors (fix: point
  `/etc/kubernetes/{controller-manager,scheduler}.conf` at the cert-valid IP and restart the
  static-pod containers via crictl; deleting the mirror pod is NOT enough).
- The clusters remain in **GatedCommit** with heartbeat 5s, per-DC consumer groups, and staleness
  threshold 30s (east). Rollback path re-verified by CP-14's phase 3.
