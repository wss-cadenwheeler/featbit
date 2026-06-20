# Gated-Commit Cross-DC Consistency (Option A)

Operator guide for the control plane's **gated-commit** consistency mode, which guarantees
that a flag/segment change is not served by any *live* data center until **every live DC has
it** — closing the cross-DC inconsistency window in the default best-effort broadcast.

> **Default is OFF.** Everything below activates only when `ConsistencyMode=GatedCommit`.
> With the default `BestEffort`, behavior is byte-for-byte unchanged from before this feature.

---

## 1. What it does

Without gating, the control plane writes a change to every DC's Redis (best-effort, failures
swallowed) and immediately republishes to the evaluation servers — so a slow/unreachable DC can
serve the old value while others serve the new one.

**GatedCommit** changes the lifecycle to:

1. **Stage** — the control plane writes the new value into every DC's Redis under a *versioned*
   key (`featbit:flag:{id}:v{ts}`) and records a *pending* change in the source of truth
   (Mongo/Postgres). It does **not** advance the served value, and **withholds** the
   evaluation-server publish.
2. **Gate** — a background **commit coordinator** waits until every **live** DC's Redis actually
   has `v{ts}` (probed directly), where "live" = a DC with at least one unexpired pod lease
   (heartbeats).
3. **Commit** — once all live DCs have it, the coordinator advances the committed pointer in
   every DC, promotes pending→committed in the DB (version-guarded), and republishes to the
   evaluation servers. Only now do SDK clients see the new value.
4. **Serve** — evaluation servers read the *committed* version from their local Redis
   (pointer-driven), so full-sync / incremental-sync / reconnect never serve a staged-but-
   uncommitted value.
5. **Evict & recover** — a DC that stops heartbeating is dropped from the live set after its
   lease expires (so it can't block commits forever). When it returns, a **recovery worker**
   backfills its Redis with the current committed state before it rejoins.

Consistency model: **Model A** — the control plane gates on its *own* per-DC Redis writes plus
lease liveness. A partitioned-but-alive DC keeps serving its last *committed* value
(consistent-but-stale) and reconverges on return; it is treated as "not live" while unreachable.

---

## 2. Enabling it

### 2.1 Control plane (`modules/control-plane`)
```jsonc
"ControlPlane": {
  "ConsistencyMode": "GatedCommit",   // default "BestEffort"
  "LeaseTtlSeconds": 15,              // how long a heartbeat keeps a DC "live"
  "CommitCoordinator": { "IntervalSeconds": 5 },
  "Recovery":          { "IntervalSeconds": 10 },
  "DcIdConsistency":   { "IntervalSeconds": 60 }
}
```
And one `Redis:Instances` entry **per DC**, each labeled with its `DcId`:
```jsonc
"Redis": {
  "Instances": [
    { "DcId": "west", "ConnectionString": "redis-west:6379", "Password": "" },
    { "DcId": "east", "ConnectionString": "redis-east:6379", "Password": "" }
  ]
}
```
The **first** instance is the control plane's *local* DC (used for heartbeat/health storage).

### 2.2 Evaluation server (`modules/evaluation-server`), in EACH DC
```jsonc
"ControlPlane": {
  "ConsistencyMode": "GatedCommit",
  "DcId": "west",      // MUST equal this DC's Redis:Instances[].DcId on the control plane
  "Region": "us-west"
}
```

### 2.3 ⚠️ The DcId matching requirement (most common misconfiguration)
`DcId` is the **join key** the coordinator uses to map a Redis instance ↔ the live pods in that
DC. **The `Redis:Instances[].DcId` you set on the control plane MUST exactly equal the
`ControlPlane:DcId` reported by that DC's evaluation servers.** If they differ (e.g. `"west"`
vs `"west-1"`), the coordinator can't correlate stage results to leases and **commits for that
DC silently stall** until its lease expires (which masks the misconfig).

The control plane runs a `DcIdConsistencyChecker` that **warns** (does not fail) when a
configured Redis `DcId` has no reporting lease, or a reporting lease has an unknown `DcId`.
Watch for those warnings (and the `unmatched_dc_count` metric) right after enabling.

---

## 3. Config reference

| Key | Default | Where | Purpose |
|---|---|---|---|
| `ControlPlane:ConsistencyMode` | `BestEffort` | CP + ELS | `GatedCommit` turns the whole feature on |
| `ControlPlane:LeaseTtlSeconds` | `15` | CP | Heartbeat → lease lifetime; eviction threshold |
| `ControlPlane:CommitCoordinator:IntervalSeconds` | `5` | CP | Commit-evaluation tick |
| `ControlPlane:Recovery:IntervalSeconds` | `10` | CP | Returning-DC backfill tick |
| `ControlPlane:DcIdConsistency:IntervalSeconds` | `60` | CP | DcId-mismatch advisory check |
| `ControlPlane:StagedFlagGc:IntervalSeconds` | `300` | API/back-end | GC of superseded `v{ts}` keys |
| `Redis:Instances[].DcId` | — | CP | DC label per Redis instance (join key) |
| `ControlPlane:DcId` / `:Region` | — | ELS | This pod's DC identity (must match a Redis `DcId`) |

---

## 4. Metrics

All on the meter **`FeatBit.ControlPlane.Consistency`** (System.Diagnostics.Metrics — scrape via
OpenTelemetry/`MeterListener`):

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `control_plane.consistency.commits` | counter | `resource_type`, `env_id` | successful commits |
| `control_plane.consistency.time_to_commit_ms` | histogram | `resource_type` | stage→commit latency |
| `control_plane.consistency.pending_backlog` | gauge | `resource_type` | currently-pending items |
| `control_plane.consistency.evicted_commits` | counter | `dc_id` | commits that proceeded without an evicted DC |
| `control_plane.consistency.unmatched_dc_count` | gauge | `direction` | DcId config/lease mismatches |

**Alert on:** sustained `pending_backlog > 0` (commits stuck — a live DC not staging, or a DcId
mismatch), rising `evicted_commits` (a DC repeatedly dropping out), and any non-zero
`unmatched_dc_count` (config drift).

---

## 5. Rollout procedure (QA: `control-plane-qa` east/west)

1. **Pre-flight:** confirm each DC's ELS `ControlPlane:DcId` equals that DC's control-plane
   `Redis:Instances[].DcId`. Confirm all DCs are heartbeating (leases present).
2. **Enable** `ConsistencyMode=GatedCommit` on the control plane and all evaluation servers;
   restart.
3. **Smoke:** watch logs for `DcIdConsistencyChecker` warnings and `unmatched_dc_count`; resolve
   any before proceeding.
4. **Run the validation checklist (§6).**
5. **Roll back** instantly by setting `ConsistencyMode=BestEffort` and restarting — no data
   migration; staged `v{ts}` keys are harmless and GC'd.

---

## 6. Validation checklist (needs the live east/west clusters)

- [ ] **Happy path:** toggle a flag; confirm it goes live in **both** DCs only *after* commit
      (not immediately), and `commits` increments.
- [ ] **One DC slow/down:** block staging to `east` (e.g. pause its Redis); toggle a flag;
      confirm it does **not** go live in `west` while `east` is still missing it; `pending_backlog`
      stays > 0.
- [ ] **Eviction:** keep `east` down past `LeaseTtlSeconds`; confirm the change then commits on
      `west` and `evicted_commits{dc_id=east}` increments.
- [ ] **Recovery:** bring `east` back; confirm the recovery worker backfills its Redis to the
      current committed state (flags **and** segments) and it serves correct values.
- [ ] **Segments:** repeat happy-path + recovery for a segment change and a segment-dependent
      flag.
- [ ] **DcId mismatch (negative):** deliberately mis-set one ELS `DcId`; confirm
      `DcIdConsistencyChecker` warns and `unmatched_dc_count` is non-zero.
- [ ] **Rollback:** flip back to `BestEffort`; confirm normal immediate propagation resumes.

---

## 7. Known limitations / open follow-ups

- **Connected clients during a recovery** (#54): after a returned DC's Redis is backfilled, SDK
  clients that stayed connected through the outage keep stale values until they reconnect/next
  full-sync (a per-DC client-refresh push is not yet implemented).
- **Eval-server applied watermark** (#46): the heartbeat watermark is per-pod in-memory and
  inaccurate; it is **not** used for gating (Model A), only a potential future metric.
- **Self-fence (D5, #22):** a partitioned DC serves last-committed (consistent-but-stale) rather
  than refusing to serve. Optional stricter mode, not implemented.
- **EF residual window:** the optimistic guards on `SetPending`/`PromotePending` are atomic on
  Mongo but load-check-save on EF/Postgres (no rowversion) — a documented narrow window.
- **Multi-replica control plane:** the coordinator/recovery/checker workers assume a single
  active control-plane instance; leader election is a TODO if you run replicas.
- **Stronger model:** an eval-server-confirmed gate (Model B) is documented in #44 as a future
  enhancement.
