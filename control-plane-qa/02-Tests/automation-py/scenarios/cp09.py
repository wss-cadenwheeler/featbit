"""CP-09: Pod Heartbeat Lifecycle Scenario.

Validates the evaluation-server pod heartbeat lifecycle as described in
``manual_scripts/connection-management/PodHeartbeats.md``:

* Heartbeats are recorded in the Redis hash ``featbit:heartbeat:all``
  (one entry per pod) in both regions, and their timestamps advance over
  time (manual steps 4-5, expected result #1).
* When a west evaluation-server pod is deleted, its heartbeat entry is
  purged from Redis after the pod heartbeat timeout window expires,
  while the east region heartbeat keeps advancing (manual steps 6-9,
  expected result #2).
* When the Deployment controller replaces the deleted pod, a heartbeat
  with a brand-new ``PodId`` appears in Redis (manual steps 12-13,
  expected result #3).

NOTE: Manual steps 2-3 and 10-11 require opening real evaluation-server
client connections (10 west / 20 east) and observing that they migrate
to the surviving pod. The Python automation harness does not currently
have a WebSocket client dependency (``websocket-client`` / ``websockets``
are not in ``pyproject.toml``) and the existing helpers do not expose a
connection-opening primitive. Per the CP-09 task instructions we record
those steps with ``add_skip`` and document the rationale; they should be
covered separately by the test-app fixtures under
``02-Tests/test-app`` once a programmatic hook is available.
"""

import json
import time
from typing import Dict, List, Optional, Tuple

from core.auth import resolve_authorization_header, resolve_request_context
from core.scenario_base import BaseScenario, ScenarioDefinition

# Redis key written by RedisCacheService.UpsertPodHeartbeat() in
# modules/back-end/src/Infrastructure/Caches/Redis/RedisCacheService.cs.
HEARTBEAT_HASH_KEY = "featbit:heartbeat:all"

# Default pod heartbeat timeout used by evaluation-server is 90s.
# We allow a small grace period for the back-end janitor pass.
DEFAULT_HEARTBEAT_TIMEOUT_SECONDS = 91
DEFAULT_FAILOVER_TIMEOUT_SECONDS = 240

# Evaluation-server publishes a heartbeat every
# ControlPlane:HeartbeatIntervalSeconds (default 60) — see
# modules/evaluation-server/src/Api/appsettings.json. Wait at least one
# full interval (plus a small grace) before resampling, otherwise the
# Timestamp field will be unchanged and "timestamp-advances" assertions
# will spuriously fail.
DEFAULT_HEARTBEAT_PUBLISH_INTERVAL_SECONDS = 60
HEARTBEAT_RESAMPLE_GRACE_SECONDS = 10
HEARTBEAT_RESAMPLE_MAX_WAIT_SECONDS = (
    DEFAULT_HEARTBEAT_PUBLISH_INTERVAL_SECONDS + HEARTBEAT_RESAMPLE_GRACE_SECONDS + 30
)

# Evaluation-server deployment metadata in the Minikube clusters.
EVAL_SERVER_LABEL_SELECTOR = "app=evaluation-server"
FEATBIT_NAMESPACE = "featbit"


class CP09Scenario(BaseScenario):
    """CP-09 pod heartbeat lifecycle scenario."""

    def definition(self) -> ScenarioDefinition:
        """Return scenario definition (single, non-paired scenario)."""
        return ScenarioDefinition(
            scenario_type="cp09",
            source_region="west",
            target_region="east",
            default_flag_key="ff-cp09-pod-heartbeats",
            target_status=True,
        )

    def run(self) -> bool:
        """Execute CP-09 scenario."""
        try:
            self.setup_artifacts()
            definition = self.definition()

            # --- Phase 1: Authentication and Authorization ---
            # (Mirrors cp08 lines 41-69; auth is not strictly required for
            # the Redis/kubectl probes below, but we log run-start with the
            # same context every other scenario uses.)
            self._notify_step("auth", "running")
            auth_header = resolve_authorization_header(
                self.get_api_base_url(definition.source_region),
                self.config.api_authorization_header,
                self.config.login_email,
                self.config.login_password,
                self.config.workspace_key,
                self.config.skip_certificate_check,
                self.config.api_version,
            )
            ctx = resolve_request_context(
                self.get_api_base_url(definition.source_region),
                auth_header,
                self.config.organization_key,
                self.config.skip_certificate_check,
                self.config.api_version,
            )
            self._notify_step("auth", "ok")

            self.add_timeline_event(
                "run-start",
                scenario=self.config.scenario_name,
                source_region=definition.source_region,
                target_region=definition.target_region,
                api_base_url_source=self.get_api_base_url(definition.source_region),
                api_base_url_target=self.get_api_base_url(definition.target_region),
                auth_type="bearer" if auth_header.startswith("Bearer") else "openapi",
                workspace_id=ctx.workspace_id,
                organization_id=ctx.organization_id,
                heartbeat_hash_key=HEARTBEAT_HASH_KEY,
            )

            # Tunables: scenario config timeout_seconds drives the failover
            # wait; the per-pod heartbeat window is fixed at 91s by the
            # back-end janitor logic.
            failover_timeout = max(
                self.config.timeout_seconds or 0,
                DEFAULT_FAILOVER_TIMEOUT_SECONDS,
            )
            heartbeat_timeout = DEFAULT_HEARTBEAT_TIMEOUT_SECONDS
            poll_interval = max(self.config.poll_interval_ms or 1000, 1000) / 1000.0

            # --- Phase 2: Baseline heartbeat presence (manual steps 4-5) ---
            self._notify_step("baseline-heartbeats", "running")

            west_baseline = self._read_heartbeats("west")
            east_baseline = self._read_heartbeats("east")

            self.add_timeline_event(
                "heartbeats-baseline",
                west_pod_ids=list(west_baseline.keys()),
                east_pod_ids=list(east_baseline.keys()),
            )

            self._assert_heartbeats_present("baseline-west-heartbeats", "west", west_baseline)
            self._assert_heartbeats_present("baseline-east-heartbeats", "east", east_baseline)

            # Sample again after at least one full heartbeat publish interval
            # and assert at least one west/east entry's timestamp advanced
            # (expected result #1). Polls up to HEARTBEAT_RESAMPLE_MAX_WAIT
            # seconds so a clean run finishes fast on fast publishers and a
            # slow publisher doesn't false-fail us.
            west_resample = self._wait_for_heartbeat_advance(
                "west", west_baseline, HEARTBEAT_RESAMPLE_MAX_WAIT_SECONDS, poll_interval
            )
            self.add_timeline_event(
                "heartbeats-resample",
                west_pod_ids=list(west_resample.keys()),
            )
            self._assert_timestamps_advanced(
                "west-heartbeat-timestamp-advances", west_baseline, west_resample
            )

            east_resample = self._wait_for_heartbeat_advance(
                "east", east_baseline, HEARTBEAT_RESAMPLE_MAX_WAIT_SECONDS, poll_interval
            )
            self._assert_timestamps_advanced(
                "east-heartbeat-timestamp-advances", east_baseline, east_resample
            )

            self._notify_step("baseline-heartbeats", "ok")

            # --- Phase 3: Synthetic client connections (manual steps 2-3) ---
            # Skipped: no WebSocket primitive available in this harness.
            self._notify_step("client-connections", "skipped")
            self.assertions.add_skip(
                "open-west-client-connections",
                (
                    "No WebSocket client dependency in pyproject.toml; "
                    "opening 10 west streaming clients is not implemented. "
                    "Cover via test-app fixture in 02-Tests/test-app."
                ),
            )
            self.assertions.add_skip(
                "open-east-client-connections",
                (
                    "No WebSocket client dependency in pyproject.toml; "
                    "opening 20 east streaming clients is not implemented. "
                    "Cover via test-app fixture in 02-Tests/test-app."
                ),
            )
            self.assertions.add_skip(
                "client-connections-recorded-in-redis",
                (
                    "Step 4 of manual script: verifying featbit:connection:* "
                    "keys requires the synthetic clients from steps 2-3, "
                    "which are skipped above."
                ),
            )
            self.add_timeline_event(
                "client-connections-skipped",
                reason="no websocket dependency in pyproject.toml",
            )

            # --- Phase 4: West pod failover (manual steps 6-10) ---
            self._notify_step("west-pod-failover", "running")

            pre_delete_west_pods = self._get_eval_server_pods("west")
            self.add_timeline_event(
                "west-pods-before-delete",
                pods=pre_delete_west_pods,
            )

            if not pre_delete_west_pods:
                self.assertions.add_fail(
                    "west-eval-server-present",
                    "No evaluation-server pod found in west cluster before delete.",
                )
                self._notify_step("west-pod-failover", "failed", "no west pod")
                return self.assertions.all_passed()

            self.assertions.add_pass(
                "west-eval-server-present",
                f"Found {len(pre_delete_west_pods)} evaluation-server pod(s) in west.",
            )

            # Map west pod names to their PodId GUID via the heartbeats hash.
            # The hash field IS the PodId, but the pod name (e.g.
            # evaluation-server-abc-xyz) is not directly recorded. We treat
            # the union of west heartbeat keys as the "west pod ids" we
            # want to see purged after delete.
            pre_delete_west_pod_ids = set(west_resample.keys())

            # Guard against the empty-baseline footgun: if Redis returned no
            # west heartbeats (e.g. redis-cli/kubectl errored upstream), the
            # purge-after-delete assertion would trivially pass without
            # proving anything. Short-circuit the failover + recovery phases.
            if not pre_delete_west_pod_ids:
                self.assertions.add_fail(
                    "pre-delete-west-podids-known",
                    (
                        "No west PodIds present in heartbeats hash before "
                        "delete; cannot verify purge or recovery. Inspect "
                        "earlier 'baseline-west-heartbeats' assertion and "
                        "the redis-hgetall-failed / redis-pod-not-found "
                        "timeline events."
                    ),
                )
                self._notify_step("west-pod-failover", "failed", "no baseline podids")
                self.assertions.add_skip(
                    "west-heartbeat-purged-after-pod-delete",
                    "Skipped because pre-delete west PodIds set was empty.",
                )
                self.assertions.add_skip(
                    "west-heartbeat-restored-with-new-pod-id",
                    "Skipped because pre-delete west PodIds set was empty.",
                )
                return self.assertions.all_passed()

            self.assertions.add_pass(
                "pre-delete-west-podids-known",
                f"Tracking {len(pre_delete_west_pod_ids)} pre-delete west PodId(s).",
            )

            # Delete west evaluation-server pods (manual step 6).
            delete_result = self._run_kubectl(
                [
                    "--context",
                    "west",
                    "-n",
                    FEATBIT_NAMESPACE,
                    "delete",
                    "pod",
                    "-l",
                    EVAL_SERVER_LABEL_SELECTOR,
                    "--wait=false",
                ],
                timeout=30,
            )
            self.add_timeline_event(
                "west-eval-server-deleted",
                returncode=delete_result.returncode,
                stdout=(delete_result.stdout or "").strip()[:300],
                stderr=(delete_result.stderr or "").strip()[:300],
            )
            if delete_result.returncode != 0:
                self.assertions.add_fail(
                    "west-eval-server-delete",
                    f"kubectl delete failed: {(delete_result.stderr or '').strip()[:200]}",
                )
                self._notify_step("west-pod-failover", "failed", "delete failed")
                return self.assertions.all_passed()

            self.assertions.add_pass(
                "west-eval-server-delete",
                f"Deleted {len(pre_delete_west_pods)} west evaluation-server pod(s).",
            )

            # Manual step 7-8: wait at least heartbeat_timeout seconds for
            # the back-end to purge the entry. We poll up to failover_timeout
            # to keep the suite resilient on slow runners.
            self._notify_step("west-heartbeat-purge", "running")
            purged, observed_west, east_during = self._poll_until_west_heartbeat_purged(
                pre_delete_west_pod_ids,
                deadline_seconds=failover_timeout,
                poll_interval_seconds=poll_interval,
            )

            self.add_timeline_event(
                "west-heartbeat-purge-result",
                purged=purged,
                pre_delete_pod_ids=sorted(pre_delete_west_pod_ids),
                final_west_pod_ids=sorted(observed_west.keys()),
                final_east_pod_ids=sorted(east_during.keys()),
                heartbeat_timeout_seconds=heartbeat_timeout,
            )

            if purged:
                self.assertions.add_pass(
                    "west-heartbeat-purged-after-pod-delete",
                    (
                        f"All pre-delete west PodIds removed from "
                        f"{HEARTBEAT_HASH_KEY} within {failover_timeout}s."
                    ),
                )
                self._notify_step("west-heartbeat-purge", "ok")
            else:
                still_present = pre_delete_west_pod_ids & set(observed_west.keys())
                self.assertions.add_fail(
                    "west-heartbeat-purged-after-pod-delete",
                    (
                        f"PodIds still present after {failover_timeout}s: "
                        f"{sorted(still_present)}"
                    ),
                )
                self._notify_step("west-heartbeat-purge", "failed")

            # Manual step 9: east heartbeat still present and advancing.
            self._assert_heartbeats_present(
                "east-heartbeat-survives-west-failover", "east", east_during
            )
            self._assert_timestamps_advanced(
                "east-heartbeat-advances-during-west-failover",
                east_resample,
                east_during,
            )

            # Manual step 10-11: client migration. Skipped — depends on
            # phase 3 having opened real clients.
            self.assertions.add_skip(
                "west-clients-migrated-to-east",
                (
                    "Depends on phase 3 (synthetic client connections), "
                    "which is skipped. Cover via test-app fixture."
                ),
            )

            self._notify_step("west-pod-failover", "ok")

            # --- Phase 5: West pod recovery (manual steps 12-13) ---
            self._notify_step("west-pod-recovery", "running")

            # Use `rollout status` instead of `kubectl wait` so we wait for
            # the Deployment as a whole to converge (readyReplicas ==
            # desiredReplicas) rather than just "any pod with the label is
            # Ready" — the label selector would otherwise match the
            # still-terminating old pod and let us pass before the
            # replacement is up.
            wait_result = self._run_kubectl(
                [
                    "--context",
                    "west",
                    "-n",
                    FEATBIT_NAMESPACE,
                    "rollout",
                    "status",
                    "deployment/evaluation-server",
                    "--timeout=120s",
                ],
                timeout=130,
            )
            self.add_timeline_event(
                "west-eval-server-ready-wait",
                returncode=wait_result.returncode,
                stdout=(wait_result.stdout or "").strip()[:300],
                stderr=(wait_result.stderr or "").strip()[:300],
            )

            if wait_result.returncode == 0:
                self.assertions.add_pass(
                    "west-eval-server-ready",
                    "Replacement west evaluation-server pod reached Ready.",
                )
            else:
                self.assertions.add_fail(
                    "west-eval-server-ready",
                    f"kubectl wait failed: {(wait_result.stderr or '').strip()[:200]}",
                )

            # Poll for a NEW PodId appearing in the west heartbeats hash.
            new_pod_id, west_after = self._poll_for_new_west_pod_id(
                pre_delete_west_pod_ids,
                deadline_seconds=failover_timeout,
                poll_interval_seconds=poll_interval,
            )
            self.add_timeline_event(
                "west-heartbeat-restored",
                new_pod_id=new_pod_id,
                west_pod_ids=sorted(west_after.keys()),
            )

            if new_pod_id:
                self.assertions.add_pass(
                    "west-heartbeat-restored-with-new-pod-id",
                    (
                        f"New west PodId {new_pod_id} present in "
                        f"{HEARTBEAT_HASH_KEY} (distinct from pre-delete set)."
                    ),
                )
                self._notify_step("west-pod-recovery", "ok")
            else:
                self.assertions.add_fail(
                    "west-heartbeat-restored-with-new-pod-id",
                    (
                        f"No new west PodId appeared in {HEARTBEAT_HASH_KEY} "
                        f"within {failover_timeout}s. "
                        f"Current entries: {sorted(west_after.keys())}"
                    ),
                )
                self._notify_step("west-pod-recovery", "failed")

            # Step 13: opening 10 new west clients — skipped (depends on
            # phase 3 client-opening primitive).
            self.assertions.add_skip(
                "open-replacement-west-clients",
                "Depends on WebSocket client primitive; see phase 3 note.",
            )

            # --- Phase 6: Cleanup ---
            # No manual teardown — the Deployment controller has already
            # recreated the deleted pod. We log a no-op event for
            # consistency with other scenarios.
            self._notify_step("cleanup", "running")
            self.add_timeline_event("cleanup", phase="cp09-no-op")
            self._notify_step("cleanup", "ok")

            return self.assertions.all_passed()

        except Exception as exc:  # pragma: no cover - defensive
            self.assertions.add_fail("runner-execution", str(exc))
            return False

        finally:
            self.write_artifacts()

    # ------------------------------------------------------------------
    # Heartbeat helpers
    # ------------------------------------------------------------------

    def _wait_for_heartbeat_advance(
        self,
        context: str,
        baseline: Dict[str, dict],
        max_wait_seconds: float,
        poll_interval_seconds: float,
    ) -> Dict[str, dict]:
        """Poll heartbeats until at least one entry's timestamp advances.

        Returns the latest snapshot. If no advance is observed within
        ``max_wait_seconds`` the final snapshot is still returned (the
        caller will then record a fail via ``_assert_timestamps_advanced``).
        """
        deadline = time.monotonic() + max_wait_seconds
        sample = self._read_heartbeats(context)
        attempts = 0
        while time.monotonic() < deadline:
            attempts += 1
            if self._any_timestamp_advanced(baseline, sample):
                self.add_timeline_event(
                    "heartbeat-advance-observed",
                    context=context,
                    attempts=attempts,
                )
                return sample
            time.sleep(max(poll_interval_seconds, 2.0))
            sample = self._read_heartbeats(context)
        self.add_timeline_event(
            "heartbeat-advance-timeout",
            context=context,
            attempts=attempts,
            max_wait_seconds=max_wait_seconds,
        )
        return sample

    @staticmethod
    def _any_timestamp_advanced(before: Dict[str, dict], after: Dict[str, dict]) -> bool:
        """Return True if any PodId present in both samples has a newer Timestamp."""
        for pod_id, entry_before in before.items():
            entry_after = after.get(pod_id)
            if entry_after is None:
                continue
            ts_before = CP09Scenario._extract_timestamp(entry_before)
            ts_after = CP09Scenario._extract_timestamp(entry_after)
            if ts_before and ts_after and ts_after > ts_before:
                return True
        return False

    def _read_heartbeats(self, context: str) -> Dict[str, dict]:
        """Return parsed heartbeats hash from the given cluster context.

        Returns a dict ``{pod_id: parsed_json_value}``. Empty dict on any
        error (a diagnostic event is logged via add_timeline_event).
        """
        pod = self._discover_redis_pod(context, FEATBIT_NAMESPACE)
        if not pod:
            self.add_timeline_event(
                "redis-pod-not-found",
                context=context,
                namespace=FEATBIT_NAMESPACE,
            )
            return {}

        result = self._run_kubectl(
            [
                "--context",
                context,
                "-n",
                FEATBIT_NAMESPACE,
                "exec",
                pod,
                "--",
                "redis-cli",
                "HGETALL",
                HEARTBEAT_HASH_KEY,
            ],
            timeout=20,
        )

        if result.returncode != 0:
            self.add_timeline_event(
                "redis-hgetall-failed",
                context=context,
                key=HEARTBEAT_HASH_KEY,
                stderr=(result.stderr or "").strip()[:300],
            )
            return {}

        return self._parse_hgetall(result.stdout or "")

    @staticmethod
    def _parse_hgetall(raw: str) -> Dict[str, dict]:
        """Parse redis-cli HGETALL default output (alternating lines)."""
        lines = [ln for ln in (raw or "").splitlines() if ln != ""]
        out: Dict[str, dict] = {}
        for i in range(0, len(lines) - 1, 2):
            field = lines[i].strip()
            value_raw = lines[i + 1]
            try:
                value = json.loads(value_raw)
            except (json.JSONDecodeError, TypeError):
                value = {"_raw": value_raw}
            out[field] = value
        return out

    @staticmethod
    def _extract_timestamp(entry: dict) -> Optional[str]:
        """Return Timestamp field (case-insensitive) or None."""
        if not isinstance(entry, dict):
            return None
        for key in ("Timestamp", "timestamp"):
            if key in entry and entry[key]:
                return str(entry[key])
        return None

    def _assert_heartbeats_present(
        self, assertion_name: str, region: str, heartbeats: Dict[str, dict]
    ) -> None:
        """Pass if at least one valid heartbeat entry exists for region."""
        if not heartbeats:
            self.assertions.add_fail(
                assertion_name,
                f"No heartbeat entries found in {region} {HEARTBEAT_HASH_KEY}.",
            )
            return

        valid_count = 0
        for pod_id, entry in heartbeats.items():
            if self._extract_timestamp(entry) and self._looks_like_guid(pod_id):
                valid_count += 1

        if valid_count > 0:
            self.assertions.add_pass(
                assertion_name,
                (
                    f"{region}: {valid_count}/{len(heartbeats)} heartbeat "
                    "entries have a valid PodId GUID and Timestamp."
                ),
            )
        else:
            self.assertions.add_fail(
                assertion_name,
                (
                    f"{region}: no heartbeat entry has both a GUID PodId "
                    f"and a Timestamp. Entries: {list(heartbeats.keys())}"
                ),
            )

    def _assert_timestamps_advanced(
        self,
        assertion_name: str,
        before: Dict[str, dict],
        after: Dict[str, dict],
    ) -> None:
        """Pass if at least one shared PodId has a newer Timestamp."""
        shared = set(before.keys()) & set(after.keys())
        if not shared:
            # If no shared pods (e.g. pod was replaced), as long as 'after'
            # has at least one entry with a Timestamp, that still proves
            # heartbeats are being written.
            after_has_ts = any(self._extract_timestamp(v) for v in after.values())
            if after_has_ts:
                self.assertions.add_pass(
                    assertion_name,
                    (
                        "No shared PodIds across samples but new "
                        "heartbeat entries are present (pod likely rotated)."
                    ),
                )
            else:
                self.assertions.add_fail(
                    assertion_name,
                    "No shared PodIds and no new heartbeat entries with Timestamps.",
                )
            return

        advanced: List[Tuple[str, str, str]] = []
        for pod_id in shared:
            ts_before = self._extract_timestamp(before[pod_id]) or ""
            ts_after = self._extract_timestamp(after[pod_id]) or ""
            if ts_before and ts_after and ts_after > ts_before:
                advanced.append((pod_id, ts_before, ts_after))

        if advanced:
            self.assertions.add_pass(
                assertion_name,
                (
                    f"{len(advanced)}/{len(shared)} shared PodIds had "
                    f"advancing Timestamps. Example: {advanced[0]}"
                ),
            )
        else:
            self.assertions.add_fail(
                assertion_name,
                (
                    "No shared PodId showed an advancing Timestamp "
                    f"across samples. Shared: {sorted(shared)}"
                ),
            )

    @staticmethod
    def _looks_like_guid(value: str) -> bool:
        """Lightweight GUID format check (8-4-4-4-12 hex)."""
        if not isinstance(value, str):
            return False
        parts = value.split("-")
        if len(parts) != 5:
            return False
        lengths = [8, 4, 4, 4, 12]
        if [len(p) for p in parts] != lengths:
            return False
        return all(all(c in "0123456789abcdefABCDEF" for c in p) for p in parts)

    # ------------------------------------------------------------------
    # kubectl helpers
    # ------------------------------------------------------------------

    def _get_eval_server_pods(self, context: str) -> List[str]:
        """List evaluation-server pod names in the given cluster context."""
        result = self._run_kubectl(
            [
                "--context",
                context,
                "-n",
                FEATBIT_NAMESPACE,
                "get",
                "pods",
                "-l",
                EVAL_SERVER_LABEL_SELECTOR,
                "-o",
                "json",
            ],
            timeout=20,
        )
        if result.returncode != 0:
            return []
        try:
            items = json.loads(result.stdout).get("items", [])
        except json.JSONDecodeError:
            return []
        return [it.get("metadata", {}).get("name", "") for it in items if it.get("metadata")]

    # ------------------------------------------------------------------
    # Polling helpers
    # ------------------------------------------------------------------

    def _poll_until_west_heartbeat_purged(
        self,
        pre_delete_pod_ids: set,
        deadline_seconds: int,
        poll_interval_seconds: float,
    ) -> Tuple[bool, Dict[str, dict], Dict[str, dict]]:
        """Poll west heartbeats hash until all pre_delete IDs are gone.

        Returns (purged, last_west_heartbeats, last_east_heartbeats).
        """
        deadline = time.time() + deadline_seconds
        last_west: Dict[str, dict] = {}
        last_east: Dict[str, dict] = {}

        while time.time() < deadline:
            last_west = self._read_heartbeats("west")
            last_east = self._read_heartbeats("east")

            self.add_timeline_event(
                "failover-poll",
                west_pod_ids=sorted(last_west.keys()),
                east_pod_ids=sorted(last_east.keys()),
            )

            still_present = pre_delete_pod_ids & set(last_west.keys())
            if not still_present:
                return True, last_west, last_east

            time.sleep(poll_interval_seconds)

        return False, last_west, last_east

    def _poll_for_new_west_pod_id(
        self,
        pre_delete_pod_ids: set,
        deadline_seconds: int,
        poll_interval_seconds: float,
    ) -> Tuple[Optional[str], Dict[str, dict]]:
        """Poll west heartbeats hash for a PodId not in pre_delete_pod_ids."""
        deadline = time.time() + deadline_seconds
        last_west: Dict[str, dict] = {}

        while time.time() < deadline:
            last_west = self._read_heartbeats("west")
            new_ids = set(last_west.keys()) - pre_delete_pod_ids
            self.add_timeline_event(
                "recovery-poll",
                west_pod_ids=sorted(last_west.keys()),
                new_pod_ids=sorted(new_ids),
            )

            # Require the new entry to have a Timestamp (proves it is live).
            for pod_id in new_ids:
                if self._extract_timestamp(last_west.get(pod_id, {})):
                    return pod_id, last_west

            time.sleep(poll_interval_seconds)

        return None, last_west
