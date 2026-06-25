"""CP-12: GatedCommit Recovery Backfill (Returning DC).

Automates control-plane-qa/02-Tests/manual_scripts/consistency/GatedCommitRecovery.md.

A DC that dropped out (lease expired) and returns must be backfilled by the recovery
worker with the current committed flag (and segment) state before it rejoins. Requires
heartbeat stop/resume commands for the target DC. The segment-membership edit step is
left as a skipped assertion (the segment update API path is out of scope for this
generator); segment *pointer* backfill is checked when a segment id is available.
"""

import time

from core.scenario_base import ScenarioDefinition

from .consistency_base import ConsistencyScenarioBase


class CP12Scenario(ConsistencyScenarioBase):
    """CP-12 GatedCommit recovery scenario."""

    FLAG_KEY = "ff-cp12-recovery"
    SEGMENT_KEY = "seg-cp12-recovery"

    def definition(self) -> ScenarioDefinition:
        # Source = west (stays live, accepts changes), target = east (drops, returns).
        return ScenarioDefinition(
            scenario_type="cp12",
            source_region="west",
            target_region="east",
            default_flag_key=self.FLAG_KEY,
            target_status=self.config.target_status,
        )

    def run(self) -> bool:
        try:
            self.setup_artifacts()
            definition = self.definition()
            headers = self.prepare(definition)

            source_url = self.get_api_base_url(definition.source_region)
            source_ctx = definition.source_region
            target_ctx = definition.target_region

            if not self.is_gated_commit():
                self.assertions.add_skip(
                    "consistency-mode",
                    f"ConsistencyMode={self.config.consistency_mode}; recovery backfill only "
                    "applies under GatedCommit.",
                )
                return self.assertions.all_passed()

            if not self.config.heartbeat_stop_command:
                self.assertions.add_skip(
                    "heartbeat-stop-configured",
                    "No heartbeat_stop_command configured; cannot take the target DC out to "
                    "exercise recovery backfill.",
                )
                return self.assertions.all_passed()

            flag_id = self.resolve_flag_id(source_url, self.FLAG_KEY, headers)
            if not flag_id:
                self.assertions.add_fail(
                    "flag-id-resolved", f"Could not resolve GUID for '{self.FLAG_KEY}'."
                )
                return self.assertions.all_passed()
            self.assertions.add_pass("flag-id-resolved", f"flag_id={flag_id}.")

            # --- Phase 1: Drop target out -----------------------------------------
            self.add_timeline_event("phase-start", phase="phase-1-drop-target")
            self.run_named_command(
                "heartbeat-stop", self.config.heartbeat_stop_command, required=True
            )
            evict_wait = self.config.lease_ttl_seconds + 5
            time.sleep(evict_wait)

            # --- Phase 2: Make a committed change while target is absent -----------
            self.add_timeline_event("phase-start", phase="phase-2-change-while-absent")
            baseline_src = self.get_committed_pointer(source_ctx, "flag", flag_id)
            self.toggle_flag(source_url, self.FLAG_KEY, True, headers)
            # Source commits via eviction (no live target to gate on). A committed
            # pointer already exists from the prior state, so wait for it to ADVANCE
            # past the baseline rather than merely be present.
            committed_src, src_ts = self.poll_committed_pointer(
                source_ctx, "flag", flag_id, advanced_from=baseline_src
            )
            self.assertions.add(
                "committed-on-source-while-absent",
                bool(committed_src),
                f"Source {source_ctx} committed to {src_ts} while target was evicted "
                f"(baseline {baseline_src}).",
                "evaluated",
            )

            # Target Redis should now be stale (still at the old pointer, or unreachable).
            target_before = self.get_committed_pointer(target_ctx, "flag", flag_id)
            self.assertions.add(
                "target-stale-before-recovery",
                target_before != src_ts,
                f"Target {target_ctx} pointer ({target_before}) is stale vs source ({src_ts}) "
                "before recovery.",
                "evaluated",
            )

            # --- Phase 3: Bring target back -> recovery worker backfills -----------
            self.add_timeline_event("phase-start", phase="phase-3-recover")
            self.run_named_command(
                "heartbeat-resume", self.config.heartbeat_resume_command, required=True
            )
            recovery_timeout = (
                self.config.recovery_interval_seconds * 3 + self.config.timeout_seconds
            )
            backfilled, observed_ts = self.poll_committed_pointer(
                target_ctx, "flag", flag_id, expect_ts=src_ts, timeout=recovery_timeout
            )
            self.assertions.add(
                "flag-backfilled-on-recovery",
                backfilled,
                f"Target {target_ctx} committed pointer backfilled to {observed_ts} "
                f"(matches source {src_ts}) after recovery.",
                "evaluated",
            )
            # The committed versioned snapshot must also be present in the recovered DC.
            staged = self.staged_versions(target_ctx, "flag", flag_id)
            self.assertions.add(
                "flag-version-present-after-recovery",
                len(staged) > 0,
                f"Recovered target holds the committed version snapshot: {staged[:3]}.",
                "evaluated",
            )

            # Segment backfill: check pointer only when a segment id is available.
            segment_id = (self.config.segment_ids_by_key or {}).get(self.SEGMENT_KEY)
            if segment_id:
                seg_backfilled, seg_ts = self.poll_committed_pointer(
                    target_ctx,
                    "segment",
                    segment_id,
                    expect_present=True,
                    timeout=recovery_timeout,
                )
                self.assertions.add(
                    "segment-backfilled-on-recovery",
                    seg_backfilled,
                    f"Target {target_ctx} segment committed pointer present after recovery "
                    f"(ts={seg_ts}).",
                    "evaluated",
                )
            else:
                self.assertions.add_skip(
                    "segment-backfilled-on-recovery",
                    f"No segment id for '{self.SEGMENT_KEY}' (seed a segment and pass "
                    "segment_ids_by_key to cover segment recovery).",
                )

            # The SDK-serving check (eval endpoint) and segment-membership edit are not
            # automated here; the committed pointer is the gated read source of truth.
            self.assertions.add_skip(
                "sdk-serves-committed-value",
                "Verifying an SDK/eval-endpoint evaluation in the recovered DC is not "
                "automated; the committed pointer above is the gated read source. Known "
                "limitation #54: clients connected through the outage may lag until reconnect.",
            )

            # --- Post-condition ---------------------------------------------------
            self._notify_step("cleanup", "running")
            self.toggle_flag(source_url, self.FLAG_KEY, False, headers)
            self.add_timeline_event("cleanup", phase="reset-flag-to-false")
            self._notify_step("cleanup", "ok")

            return self.assertions.all_passed()

        except Exception as exc:  # noqa: BLE001
            self.assertions.add_fail("runner-execution", str(exc))
            self.run_named_command(
                "heartbeat-resume", self.config.heartbeat_resume_command, required=False
            )
            return False
        finally:
            self.write_artifacts()
