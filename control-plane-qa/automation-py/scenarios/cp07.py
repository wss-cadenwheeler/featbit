"""CP-07: License Change Propagation Scenario.

Validates that a license change message is sent through the control plane
to update Redis in both west and east datacenters.
"""

import json
import subprocess
import time

from core.api_client import ApiClient, extract_data
from core.auth import resolve_authorization_header, resolve_request_context
from core.models import LicenseState
from core.scenario_base import BaseScenario, ScenarioDefinition


class CP07Scenario(BaseScenario):
    """CP-07 license change propagation scenario."""

    def definition(self) -> ScenarioDefinition:
        """Return scenario definition."""
        if self.config.scenario_name == "cp07-west-to-east":
            return ScenarioDefinition(
                scenario_type="cp07",
                source_region="west",
                target_region="east",
                default_flag_key="license-cp07",
                target_status=True,
            )
        else:  # cp07-east-to-west
            return ScenarioDefinition(
                scenario_type="cp07",
                source_region="east",
                target_region="west",
                default_flag_key="license-cp07",
                target_status=True,
            )

    def run(self) -> bool:
        """Execute CP-07 scenario."""
        try:
            self.setup_artifacts()
            definition = self.definition()

            # --- Phase 1: Authentication and Authorization ---

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

            self.add_timeline_event(
                "run-start",
                scenario=self.config.scenario_name,
                source_region=definition.source_region,
                target_region=definition.target_region,
                api_base_url_source=self.get_api_base_url(
                    definition.source_region
                ),
                api_base_url_target=self.get_api_base_url(
                    definition.target_region
                ),
                auth_type=(
                    "bearer"
                    if auth_header.startswith("Bearer")
                    else "openapi"
                ),
                workspace_id=ctx.workspace_id,
                organization_id=ctx.organization_id,
            )

            source_url = self.get_api_base_url(definition.source_region)
            target_url = self.get_api_base_url(definition.target_region)

            # --- Phase 2: Baseline License Check ---

            self._notify_step("baseline-check", "running")
            source_baseline = self._get_license_state(
                source_url,
                definition.source_region,
                headers,
            )
            self.add_timeline_event(
                "baseline-license-check",
                source=json.loads(source_baseline.json()),
            )
            self._notify_step("baseline-check", "ok")

            # --- Phase 3: Update License ---

            self._notify_step("license-update", "running")

            license_key = "CP-07-LICENSE-KEY"
            license_update_result = self._update_license(
                source_url,
                license_key,
                headers,
            )

            if not license_update_result.get("success"):
                self.assertions.add_fail(
                    "license-update-failed",
                    "License update endpoint did not succeed.",
                )
                return False

            self.add_timeline_event(
                "license-updated",
                license_key=license_key,
                result=license_update_result,
            )

            self.assertions.add_pass(
                "api-license-update-succeeded",
                "License update endpoint responded successfully.",
            )
            self._notify_step("license-update", "ok")

            # --- Phase 4: Poll for Convergence ---

            self._notify_step("convergence", "running")

            converged, _, _ = self._poll_license_convergence(
                source_url,
                target_url,
                headers,
            )

            self.assertions.add(
                "source-target-convergence",
                converged,
                "Both regions updated with new license.",
                "evaluated",
            )
            self._notify_step("convergence", "ok" if converged else "failed")

            # --- Phase 5: Kafka Topic Verification ---

            self._run_license_kafka_check(
                "source-license-topic-check",
                self.config.source_topic_check_command,
                definition.source_region,
                self._KAFKA_BOOTSTRAP,
                "featbit-control-plane-license-change",
                license_key,
            )

            # --- Phase 6: Redis Verification ---

            self._run_license_redis_check(
                "west",
                self.config.redis_west_check_command,
                license_key,
            )

            self._run_license_redis_check(
                "east",
                self.config.redis_east_check_command,
                license_key,
            )

            # --- Phase 7: Post-condition: Reset License (if applicable) ---

            self._notify_step("cleanup", "running")
            self.add_timeline_event("cleanup", phase="license-check-complete")
            self._notify_step("cleanup", "ok")

            return self.assertions.all_passed()

        except Exception as e:
            self.assertions.add_fail("runner-execution", str(e))
            return False

        finally:
            self.write_artifacts()

    def _get_license_state(
        self,
        base_url: str,
        region: str,
        headers: dict,
    ) -> LicenseState:
        """Get current license state from API."""
        client = ApiClient(
            base_url,
            self.config.skip_certificate_check,
            self.config.api_version,
        )

        endpoint = (
            f"/api/v{self.config.api_version}/workspaces/"
            f"{self.config.workspace_key}/license"
        )

        try:
            response = client.get(endpoint, headers=headers)
            data = extract_data(response)
            return LicenseState(
                region=region,
                observed_at_utc=self._get_utc_timestamp(),
                is_present=data is not None,
                license_key=data.get("licenseKey") if data else None,
                is_expired=data.get("isExpired") if data else None,
                error=None,
            )
        except Exception as e:
            return LicenseState(
                region=region,
                observed_at_utc=self._get_utc_timestamp(),
                is_present=False,
                license_key=None,
                is_expired=None,
                error=str(e),
            )

    def _update_license(
        self,
        base_url: str,
        license_key: str,
        headers: dict,
    ) -> dict:
        """Update license via API."""
        client = ApiClient(
            base_url,
            self.config.skip_certificate_check,
            self.config.api_version,
        )

        endpoint = (
            f"/api/v{self.config.api_version}/workspaces/"
            f"{self.config.workspace_key}/license"
        )

        payload = {
            "licenseKey": license_key,
        }

        response = client.put(endpoint, body=payload, headers=headers)
        data = extract_data(response)
        return {"success": True, "data": data} if data else {"success": False}

    def _poll_license_convergence(
        self,
        source_url: str,
        target_url: str,
        headers: dict,
    ) -> tuple:
        """Poll both regions until license converges or timeout."""
        timeout = self.config.convergence_timeout_seconds or 120
        poll_interval = self.config.convergence_poll_interval_seconds or 2
        elapsed = 0
        source_state = None
        target_state = None

        while elapsed < timeout:
            source_state = self._get_license_state(
                source_url, "source", headers
            )
            target_state = self._get_license_state(
                target_url, "target", headers
            )

            self.add_timeline_event(
                "convergence-poll",
                source=json.loads(source_state.json()),
                target=json.loads(target_state.json()),
            )

            # Both regions must have license present
            if source_state.is_present and target_state.is_present:
                # Optional: verify license keys match
                keys_match = (
                    source_state.license_key
                    == target_state.license_key
                )
                if keys_match:
                    self.add_timeline_event(
                        "convergence-achieved",
                        elapsed_seconds=elapsed,
                    )
                    return True, source_state, target_state

            time.sleep(poll_interval)
            elapsed += poll_interval

        self.add_timeline_event(
            "convergence-timeout",
            timeout_seconds=timeout,
        )
        return False, source_state, target_state

    def _run_license_kafka_check(
        self,
        check_name: str,
        command: str,
        region: str,
        bootstrap: str,
        topic: str,
        license_key: str,
    ) -> None:
        """Run a custom Kafka check for license presence."""
        if not command:
            self.assertions.add_skip(
                check_name,
                f"No Kafka check command configured for {region}.",
            )
            return

        try:
            result = subprocess.run(
                command,
                shell=True,
                capture_output=True,
                text=True,
                timeout=30,
            )

            self.add_timeline_event(
                "kafka-check",
                check=check_name,
                region=region,
                bootstrap=bootstrap,
                topic=topic,
                license_key=license_key,
                exit_code=result.returncode,
                output=result.stdout[:500] if result.stdout else None,
            )

            if result.returncode == 0:
                self.assertions.add_pass(
                    check_name,
                    f"License {license_key} found in {topic}.",
                )
            else:
                self.assertions.add_fail(
                    check_name,
                    f"License check failed: {result.stderr[:200]}",
                )
        except subprocess.TimeoutExpired:
            self.assertions.add_fail(
                check_name,
                "License Kafka check timed out.",
            )
        except Exception as e:
            self.assertions.add_fail(
                check_name,
                f"License Kafka check error: {str(e)[:100]}",
            )

    def _run_license_redis_check(
        self,
        region: str,
        command: str,
        license_key: str,
    ) -> None:
        """Run a custom Redis check for license presence."""
        if not command:
            self.assertions.add_skip(
                f"{region}-redis-license-check",
                f"No Redis check command configured for {region}.",
            )
            return

        try:
            result = subprocess.run(
                command,
                shell=True,
                capture_output=True,
                text=True,
                timeout=30,
            )

            self.add_timeline_event(
                "redis-check",
                check=f"{region}-redis-license-check",
                region=region,
                license_key=license_key,
                exit_code=result.returncode,
                output=result.stdout[:500] if result.stdout else None,
            )

            if result.returncode == 0:
                self.assertions.add_pass(
                    f"{region}-redis-license-check",
                    f"License {license_key} found in {region} Redis.",
                )
            else:
                self.assertions.add_fail(
                    f"{region}-redis-license-check",
                    f"License check failed: {result.stderr[:200]}",
                )
        except subprocess.TimeoutExpired:
            self.assertions.add_fail(
                f"{region}-redis-license-check",
                "License Redis check timed out.",
            )
        except Exception as e:
            self.assertions.add_fail(
                f"{region}-redis-license-check",
                f"License Redis check error: {str(e)[:100]}",
            )

    @staticmethod
    def _get_utc_timestamp() -> str:
        """Get current UTC timestamp in ISO format."""
        from datetime import datetime

        return datetime.utcnow().isoformat() + "Z"
