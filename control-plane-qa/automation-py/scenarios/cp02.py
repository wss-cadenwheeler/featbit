"""CP-02: Cross-DC Flag Propagation Correctness Scenarios."""

from core.auth import resolve_authorization_header, resolve_request_context
from core.scenario_base import BaseScenario, ScenarioDefinition


class CP02Scenario(BaseScenario):
    """CP-02 correctness scenario base class."""

    def definition(self) -> ScenarioDefinition:
        """Return scenario definition."""
        if self.config.scenario_name == "cp02-west-to-east":
            return ScenarioDefinition(
                scenario_type="cp02",
                source_region="west",
                target_region="east",
                default_flag_key="ff-cp02-west",
                target_status=self.config.target_status,
            )
        else:  # cp02-east-to-west
            return ScenarioDefinition(
                scenario_type="cp02",
                source_region="east",
                target_region="west",
                default_flag_key="ff-cp02-east",
                target_status=self.config.target_status,
            )

    def run(self) -> bool:
        """Execute CP-02 scenario."""
        try:
            self.setup_artifacts()
            definition = self.definition()

            # Resolve auth
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

            # Resolve request context
            ctx = resolve_request_context(
                self.get_api_base_url(definition.source_region),
                auth_header,
                self.config.organization_key,
                self.config.skip_certificate_check,
                self.config.api_version,
            )
            self._notify_step("auth", "ok")

            # Build headers
            headers = {
                "Authorization": auth_header,
                "Content-Type": "application/json",
            }
            if ctx.workspace_id:
                headers["Workspace"] = ctx.workspace_id
            if ctx.organization_id:
                headers["Organization"] = ctx.organization_id

            # Log run start
            self.add_timeline_event(
                "run-start",
                scenario=self.config.scenario_name,
                source_region=definition.source_region,
                target_region=definition.target_region,
                api_base_url_source=self.get_api_base_url(definition.source_region),
                api_base_url_target=self.get_api_base_url(definition.target_region),
                env_id=str(self.config.env_id),
                flag_key=definition.default_flag_key,
                expected_status=definition.target_status,
                auth_type="bearer" if auth_header.startswith("Bearer") else "openapi",
                workspace_id=ctx.workspace_id,
                organization_id=ctx.organization_id,
            )

            # Toggle flag
            source_url = self.get_api_base_url(definition.source_region)
            target_url = self.get_api_base_url(definition.target_region)

            self._notify_step("toggle", "running")
            toggle_result = self.toggle_flag(
                source_url,
                definition.default_flag_key,
                definition.target_status,
                headers,
            )

            self.add_timeline_event(
                "api-toggle",
                result=toggle_result,
            )
            self.assertions.add_pass("api-toggle-succeeded", "Toggle endpoint responded successfully.")
            self._notify_step("toggle", "ok")

            # Poll for convergence
            self._notify_step("convergence", "running")
            converged, source_state, target_state = self.poll_convergence(
                source_url,
                target_url,
                definition.default_flag_key,
                definition.target_status,
                headers,
            )

            self.assertions.add(
                "source-target-convergence",
                converged,
                f"Both regions reported expected isEnabled={definition.target_status}.",
                "evaluated",
            )
            self._notify_step("convergence", "ok" if converged else "failed")

            # Resolve flag_id for topic and Redis checks.
            flag_id = (self.config.flag_ids_by_key or {}).get(definition.default_flag_key)

            # Kafka topic checks: message arrived on CP topic, forwarded to eval topic,
            # and MirrorMaker2 replicated to the target cluster's aggregate broker.
            self.run_kafka_topic_check(
                "source-topic-check",
                self.config.source_topic_check_command,
                context=definition.source_region,
                bootstrap=self._KAFKA_BOOTSTRAP,
                topic="featbit-control-plane-feature-flag-change",
                flag_id=flag_id,
            )
            self.run_kafka_topic_check(
                "downstream-topic-check",
                self.config.downstream_topic_check_command,
                context=definition.source_region,
                bootstrap=self._KAFKA_BOOTSTRAP,
                topic="featbit-feature-flag-change",
                flag_id=flag_id,
            )
            self.run_kafka_topic_check(
                "retry-log-check",
                self.config.retry_log_check_command,
                context=definition.target_region,
                bootstrap=self._KAFKA_AGGREGATE_BOOTSTRAP,
                topic="featbit-feature-flag-change",
                flag_id=flag_id,
            )

            # Run Redis checks.
            self.run_redis_check(
                "west",
                self.config.redis_west_check_command,
                flag_id=flag_id,
                flag_key=definition.default_flag_key,
                expected_status=definition.target_status,
            )
            self.run_redis_check(
                "east",
                self.config.redis_east_check_command,
                flag_id=flag_id,
                flag_key=definition.default_flag_key,
                expected_status=definition.target_status,
            )

            return self.assertions.all_passed()

        except Exception as e:
            self.assertions.add_fail("runner-execution", str(e))
            return False

        finally:
            self.write_artifacts()
