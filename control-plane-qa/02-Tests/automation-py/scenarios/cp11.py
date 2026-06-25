"""CP-11: GatedCommit Slow/Down DC and Eviction.

Automates control-plane-qa/02-Tests/manual_scripts/consistency/GatedCommitEviction.md.

While a *live* DC has not staged a change the commit is gated (pointer does not advance
anywhere). Once that DC stops heartbeating and its lease expires it is evicted, and the
change commits on the remaining DC. Requires a stage-block disruption and a
heartbeat-stop disruption for the target DC; gated assertions are skipped when those
commands are not configured (the negative cannot be proven without a real disruption).
"""

import time

from core.scenario_base import ScenarioDefinition

from .consistency_base import ConsistencyScenarioBase


class CP11Scenario(ConsistencyScenarioBase):
    """CP-11 GatedCommit eviction scenario."""

    FLAG_KEY = "ff-cp11-eviction"

    def definition(self) -> ScenarioDefinition:
        # Source = west (toggles), target = east (blocked, then evicted).
        return ScenarioDefinition(
            scenario_type="cp11",
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
            contexts = [source_ctx, target_ctx]

            if not self.is_gated_commit():
                self.assertions.add_skip(
                    "consistency-mode",
                    f"ConsistencyMode={self.config.consistency_mode}; eviction gating only "
                    "applies under GatedCommit.",
                )
                return self.assertions.all_passed()

            # This test is meaningless without a way to block staging to the target DC.
            if not self.config.stage_block_start_command:
                self.assertions.add_skip(
                    "stage-block-configured",
                    "No stage_block_start_command configured; cannot block staging to "
                    f"{target_ctx} to exercise commit gating. Configure the disruption to run.",
                )
                return self.assertions.all_passed()

            flag_id = self.resolve_flag_id(source_url, self.FLAG_KEY, headers)
            if not flag_id:
                self.assertions.add_fail(
                    "flag-id-resolved", f"Could not resolve GUID for '{self.FLAG_KEY}'."
                )
                return self.assertions.all_passed()
            self.assertions.add_pass("flag-id-resolved", f"flag_id={flag_id}.")

            # --- Phase 1: Baseline -------------------------------------------------
            baseline = {c: self.get_committed_pointer(c, "flag", flag_id) for c in contexts}
            baseline_src = baseline.get(source_ctx)
            self.add_timeline_event(
                "phase-start", phase="phase-1-baseline", result={"committed": baseline}
            )

            # --- Phase 2: Block target staging while it is still live --------------
            self.add_timeline_event("phase-start", phase="phase-2-block-live")
            self.run_named_command(
                "stage-block-start", self.config.stage_block_start_command, required=True
            )
            self.toggle_flag(source_url, self.FLAG_KEY, True, headers)
            self.assertions.add_pass("api-toggle-succeeded", "Toggle to true accepted.")

            # Hold: the commit must NOT advance in either DC while target is live-but-missing.
            time.sleep(self.config.disruption_hold_seconds)
            held = {c: self.get_committed_pointer(c, "flag", flag_id) for c in contexts}
            self.add_timeline_event("outage-poll", phase="phase-2", result={"committed": held})
            self.assertions.add(
                "commit-gated-while-live",
                held.get(source_ctx) == baseline_src
                and held.get(target_ctx) == baseline.get(target_ctx),
                f"Committed pointer unchanged during gated window (still {held}); change "
                "is withheld while the live target has not staged it.",
                "evaluated",
            )
            # Positive proof the gate is real (not just "nothing happened"): the SOURCE
            # must have staged the new version while the blocked target has NOT. Without
            # this, commit-gated-while-live would also pass if staging never occurred at
            # all (e.g. GatedCommit silently disabled / coordinator dead).
            source_staged = self.staged_versions(source_ctx, "flag", flag_id)
            target_staged = self.staged_versions(target_ctx, "flag", flag_id)
            self.assertions.add(
                "source-staged-during-gate",
                len(source_staged) > 0,
                f"Source {source_ctx} holds the staged version during the gated window "
                f"({source_staged[:3]}); staging did occur, so the unchanged committed "
                "pointer reflects gating rather than a no-op.",
                "evaluated",
            )
            self.assertions.add(
                "target-missing-staged-version",
                len(target_staged) == 0,
                f"Target {target_ctx} has no staged version while staging is blocked.",
                "evaluated",
            )

            # --- Phase 3: Stop target heartbeats -> eviction -> commit on source ---
            self.add_timeline_event("phase-start", phase="phase-3-evict")
            self.run_named_command(
                "heartbeat-stop", self.config.heartbeat_stop_command, required=False
            )
            if not self.config.heartbeat_stop_command:
                self.assertions.add_skip(
                    "commit-after-eviction",
                    "No heartbeat_stop_command configured; cannot force lease expiry/eviction.",
                )
            else:
                # Wait for lease expiry plus a coordinator tick, then expect a commit on source.
                wait_s = (
                    self.config.lease_ttl_seconds
                    + self.config.commit_coordinator_interval_seconds
                    + 5
                )
                self.add_timeline_event(
                    "wait", phase="phase-3", result={"reason": "lease-expiry", "seconds": wait_s}
                )
                time.sleep(wait_s)
                committed_src, src_ts = self.poll_committed_pointer(
                    source_ctx, "flag", flag_id, expect_present=True
                )
                advanced = committed_src and src_ts != baseline_src
                self.assertions.add(
                    "commit-after-eviction",
                    bool(advanced),
                    f"Source {source_ctx} committed pointer advanced to {src_ts} after the "
                    f"target lease expired (baseline {baseline_src}).",
                    "evaluated",
                )
                # Optional: evicted_commits metric.
                self.run_named_command(
                    "metrics-evicted-commits",
                    self.config.consistency_metrics_check_command,
                    required=False,
                )

            # --- Phase 4: Restore target, confirm no permanent divergence ----------
            self.add_timeline_event("phase-start", phase="phase-4-restore")
            self.run_named_command(
                "heartbeat-resume", self.config.heartbeat_resume_command, required=False
            )
            self.run_named_command(
                "stage-block-stop", self.config.stage_block_stop_command, required=False
            )
            reconverged, observed = self.poll_committed_convergence("flag", flag_id, contexts)
            self.assertions.add(
                "no-permanent-divergence",
                reconverged,
                f"After restore, both DCs share the same committed pointer: {observed}.",
                "evaluated",
            )

            # Post-condition: reset flag to false.
            self._notify_step("cleanup", "running")
            self.toggle_flag(source_url, self.FLAG_KEY, False, headers)
            self.add_timeline_event("cleanup", phase="reset-flag-to-false")
            self._notify_step("cleanup", "ok")

            return self.assertions.all_passed()

        except Exception as exc:  # noqa: BLE001
            self.assertions.add_fail("runner-execution", str(exc))
            # Best-effort: lift disruptions so the environment is not left broken.
            self.run_named_command(
                "heartbeat-resume", self.config.heartbeat_resume_command, required=False
            )
            self.run_named_command(
                "stage-block-stop", self.config.stage_block_stop_command, required=False
            )
            return False
        finally:
            self.write_artifacts()
