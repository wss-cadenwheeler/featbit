"""CP-04: Segment Change Propagation Scenarios."""

import json

from core.auth import resolve_authorization_header, resolve_request_context
from core.scenario_base import BaseScenario, ScenarioDefinition


class CP04Scenario(BaseScenario):
    """CP-04 segment creation propagation scenario.

    Validates that creating a segment in the source region propagates
    through Kafka topics and Redis caches to both west and east clusters.
    """

    _DEFAULT_SEGMENT_KEY = "seg-cp04-basic"

    def definition(self) -> ScenarioDefinition:
        """Return scenario definition."""
        if self.config.scenario_name == "cp04-west-to-east":
            return ScenarioDefinition(
                scenario_type="cp04",
                source_region="west",
                target_region="east",
                default_flag_key=self._DEFAULT_SEGMENT_KEY,
                target_status=True,
            )
        else:
            return ScenarioDefinition(
                scenario_type="cp04",
                source_region="east",
                target_region="west",
                default_flag_key=self._DEFAULT_SEGMENT_KEY,
                target_status=True,
            )

    def run(self) -> bool:
        """Execute CP-04 scenario."""
        segment_id = None
        source_url = None
        headers = {}

        try:
            self.setup_artifacts()
            definition = self.definition()

            # --- Phase 1: Authentication ---

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

            headers = {
                "Authorization": auth_header,
                "Content-Type": "application/json",
            }
            if ctx.workspace_id:
                headers["Workspace"] = ctx.workspace_id
            if ctx.organization_id:
                headers["Organization"] = ctx.organization_id

            source_url = self.get_api_base_url(definition.source_region)
            target_url = self.get_api_base_url(definition.target_region)

            self.add_timeline_event(
                "run-start",
                scenario=self.config.scenario_name,
                source_region=definition.source_region,
                target_region=definition.target_region,
                api_base_url_source=source_url,
                api_base_url_target=target_url,
                env_id=str(self.config.env_id),
                flag_key=definition.default_flag_key,
                expected_status=definition.target_status,
                auth_type="bearer" if auth_header.startswith("Bearer") else "openapi",
                workspace_id=ctx.workspace_id,
                organization_id=ctx.organization_id,
            )

            # --- Phase 2: Create segment in source region ---

            self._notify_step("create-segment", "running")
            segment_data = self.create_segment(
                source_url,
                definition.default_flag_key,
                headers,
            )
            segment_id = segment_data.get("id") if segment_data else None

            self.add_timeline_event(
                "segment-create",
                result={
                    "segment_id": segment_id,
                    "segment_key": definition.default_flag_key,
                    "source_region": definition.source_region,
                },
            )

            if segment_id:
                self.assertions.add_pass(
                    "segment-created",
                    f"Segment '{definition.default_flag_key}' created with id={segment_id}.",
                )
                self._notify_step("create-segment", "ok")
            else:
                self.assertions.add_fail(
                    "segment-created",
                    f"Segment creation returned no id. Response: {segment_data}",
                )
                self._notify_step("create-segment", "failed")
                return False

            # --- Phase 3: Poll for convergence in target region ---

            self._notify_step("convergence", "running")
            found, segment_state = self.poll_segment_exists(
                target_url,
                segment_id,
                headers,
            )

            self.assertions.add(
                "segment-propagated-to-target",
                found,
                (
                    f"Segment id={segment_id} accessible in "
                    f"{definition.target_region} region."
                ),
                "evaluated",
            )
            self._notify_step("convergence", "ok" if found else "failed")

            # --- Phase 4: Kafka topic verification ---

            self.run_kafka_topic_check(
                "cp-segment-topic-check",
                None,
                context=definition.source_region,
                bootstrap=self._KAFKA_BOOTSTRAP,
                topic="featbit-control-plane-segment-change",
                flag_id=segment_id,
            )

            self.run_kafka_topic_check(
                "downstream-segment-topic-check",
                None,
                context=definition.source_region,
                bootstrap=self._KAFKA_BOOTSTRAP,
                topic="featbit-segment-change",
                flag_id=segment_id,
            )

            self.run_kafka_topic_check(
                "target-aggregate-segment-topic-check",
                None,
                context=definition.target_region,
                bootstrap=self._KAFKA_AGGREGATE_BOOTSTRAP,
                topic="featbit-segment-change",
                flag_id=segment_id,
            )

            # --- Phase 5: Redis verification in both regions ---

            self.run_segment_redis_check(
                "west",
                segment_id,
                context="west",
            )
            self.run_segment_redis_check(
                "east",
                segment_id,
                context="east",
            )

            # --- Cleanup: Archive segment ---

            self._notify_step("cleanup", "running")
            self.delete_segment(source_url, segment_id, headers)
            self.add_timeline_event("cleanup", phase="archive-segment")
            self._notify_step("cleanup", "ok")

            return self.assertions.all_passed()

        except Exception as e:
            self.assertions.add_fail("runner-execution", str(e))
            return False

        finally:
            # Best-effort cleanup even on failure.
            if segment_id and source_url:
                try:
                    self.delete_segment(source_url, segment_id, headers)
                except Exception:
                    pass
            self.write_artifacts()
