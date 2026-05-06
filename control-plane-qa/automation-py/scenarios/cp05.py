"""CP-05: Secret Change Propagation Scenario.

Validates that a secret change message is sent through the control plane
to update Redis in both west and east datacenters.
"""

import json
import subprocess
import time
import uuid

from core.api_client import ApiClient, extract_data
from core.auth import resolve_authorization_header, resolve_request_context
from core.models import SecretState
from core.scenario_base import BaseScenario, ScenarioDefinition


class CP05Scenario(BaseScenario):
    """CP-05 secret change propagation scenario."""

    def definition(self) -> ScenarioDefinition:
        """Return scenario definition."""
        if self.config.scenario_name == "cp05-west-to-east":
            return ScenarioDefinition(
                scenario_type="cp05",
                source_region="west",
                target_region="east",
                default_flag_key="secret-cp05-west",
                target_status=True,
            )
        else:  # cp05-east-to-west
            return ScenarioDefinition(
                scenario_type="cp05",
                source_region="east",
                target_region="west",
                default_flag_key="secret-cp05-east",
                target_status=True,
            )

    def run(self) -> bool:
        """Execute CP-05 scenario."""
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
                env_id=str(self.config.env_id),
                secret_key=definition.default_flag_key,
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

            # --- Phase 2: Create Secret ---

            self._notify_step("secret-create", "running")

            secret_key = f"{definition.default_flag_key}-{str(uuid.uuid4())[:8]}"
            secret_value = f"secret-value-{str(uuid.uuid4())[:8]}"

            secret_create_result = self._create_secret(
                source_url,
                secret_key,
                secret_value,
                headers,
            )

            if not secret_create_result.get("id"):
                self.assertions.add_fail(
                    "secret-create-failed",
                    "Secret creation returned no ID.",
                )
                return False

            secret_id = secret_create_result["id"]

            self.add_timeline_event(
                "secret-created",
                secret_id=secret_id,
                secret_key=secret_key,
            )

            self.assertions.add_pass(
                "api-secret-create-succeeded",
                "Secret creation endpoint responded successfully.",
            )
            self._notify_step("secret-create", "ok")

            # --- Phase 3: Poll for Convergence ---

            self._notify_step("convergence", "running")

            converged, _, _ = self._poll_secret_convergence(
                source_url,
                target_url,
                secret_id,
                secret_key,
                headers,
            )

            self.assertions.add(
                "source-target-convergence",
                converged,
                f"Both regions have secret {secret_key} in Redis.",
                "evaluated",
            )
            self._notify_step("convergence", "ok" if converged else "failed")

            # --- Phase 4: Kafka Topic Verification ---

            self._run_secret_kafka_check(
                "source-secret-topic-check",
                self.config.source_topic_check_command,
                definition.source_region,
                self._KAFKA_BOOTSTRAP,
                "featbit-control-plane-secret-change",
                secret_id,
                secret_key,
            )

            # --- Phase 5: Redis Verification ---

            self._run_secret_redis_check(
                "west",
                self.config.redis_west_check_command,
                secret_id,
                secret_key,
            )

            self._run_secret_redis_check(
                "east",
                self.config.redis_east_check_command,
                secret_id,
                secret_key,
            )

            # --- Post-condition: Cleanup ---

            self._notify_step("cleanup", "running")
            self._delete_secret(source_url, secret_id, headers)
            self.add_timeline_event("cleanup", phase="delete-secret")
            self._notify_step("cleanup", "ok")

            return self.assertions.all_passed()

        except Exception as e:
            self.assertions.add_fail("runner-execution", str(e))
            return False

        finally:
            self.write_artifacts()

    def _create_secret(
        self,
        base_url: str,
        secret_key: str,
        secret_value: str,
        headers: dict,
    ) -> dict:
        """Create a secret via API."""
        client = ApiClient(
            base_url,
            self.config.skip_certificate_check,
            self.config.api_version,
        )

        endpoint = (
            f"/api/v{self.config.api_version}/envs/{self.config.env_id}"
            f"/secrets"
        )

        payload = {
            "name": secret_key,
            "value": secret_value,
        }

        response = client.post(endpoint, body=payload, headers=headers)
        data = extract_data(response)
        return data if data else {}

    def _delete_secret(
        self,
        base_url: str,
        secret_id: str,
        headers: dict,
    ) -> bool:
        """Delete a secret via API."""
        client = ApiClient(
            base_url,
            self.config.skip_certificate_check,
            self.config.api_version,
        )

        endpoint = (
            f"/api/v{self.config.api_version}/envs/{self.config.env_id}"
            f"/secrets/{secret_id}"
        )

        try:
            client.request("DELETE", endpoint, headers=headers)
            return True
        except Exception:
            return False

    def _get_secret_state(
        self,
        base_url: str,
        secret_id: str,
        region: str,
        headers: dict,
    ) -> SecretState:
        """Get current secret state from API."""
        client = ApiClient(
            base_url,
            self.config.skip_certificate_check,
            self.config.api_version,
        )

        endpoint = (
            f"/api/v{self.config.api_version}/envs/{self.config.env_id}"
            f"/secrets/{secret_id}"
        )

        try:
            response = client.get(endpoint, headers=headers)
            data = extract_data(response)
            return SecretState(
                region=region,
                observed_at_utc=self._get_utc_timestamp(),
                is_present=data is not None,
                id=data.get("id") if data else None,
                name=data.get("name") if data else None,
                error=None,
            )
        except Exception as e:
            return SecretState(
                region=region,
                observed_at_utc=self._get_utc_timestamp(),
                is_present=False,
                id=secret_id,
                name=None,
                error=str(e),
            )

    def _poll_secret_convergence(
        self,
        source_url: str,
        target_url: str,
        secret_id: str,
        secret_key: str,
        headers: dict,
    ) -> tuple:
        """Poll both regions until secret converges or timeout."""
        timeout = self.config.convergence_timeout_seconds or 120
        poll_interval = self.config.convergence_poll_interval_seconds or 2
        elapsed = 0
        source_state = None
        target_state = None

        while elapsed < timeout:
            source_state = self._get_secret_state(
                source_url, secret_id, "source", headers
            )
            target_state = self._get_secret_state(
                target_url, secret_id, "target", headers
            )

            self.add_timeline_event(
                "convergence-poll",
                source=json.loads(source_state.json()),
                target=json.loads(target_state.json()),
            )

            if source_state.is_present and target_state.is_present:
                self.add_timeline_event(
                    "convergence-achieved",
                    secret_id=secret_id,
                    secret_key=secret_key,
                    elapsed_seconds=elapsed,
                )
                return True, source_state, target_state

            time.sleep(poll_interval)
            elapsed += poll_interval

        self.add_timeline_event(
            "convergence-timeout",
            secret_id=secret_id,
            secret_key=secret_key,
            timeout_seconds=timeout,
        )
        return False, source_state, target_state

    def _run_secret_kafka_check(
        self,
        check_name: str,
        command: str,
        region: str,
        bootstrap: str,
        topic: str,
        secret_id: str,
        secret_key: str,
    ) -> None:
        """Run a custom Kafka check for secret presence."""
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
                secret_id=secret_id,
                exit_code=result.returncode,
                output=result.stdout[:500] if result.stdout else None,
            )

            if result.returncode == 0:
                self.assertions.add_pass(
                    check_name,
                    f"Secret {secret_key} found in {topic}.",
                )
            else:
                self.assertions.add_fail(
                    check_name,
                    f"Secret check failed: {result.stderr[:200]}",
                )
        except subprocess.TimeoutExpired:
            self.assertions.add_fail(
                check_name,
                "Secret Kafka check timed out.",
            )
        except Exception as e:
            self.assertions.add_fail(
                check_name,
                f"Secret Kafka check error: {str(e)[:100]}",
            )

    def _run_secret_redis_check(
        self,
        region: str,
        command: str,
        secret_id: str,
        secret_key: str,
    ) -> None:
        """Run a custom Redis check for secret presence."""
        if not command:
            self.assertions.add_skip(
                f"{region}-redis-secret-check",
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
                check=f"{region}-redis-secret-check",
                region=region,
                secret_id=secret_id,
                exit_code=result.returncode,
                output=result.stdout[:500] if result.stdout else None,
            )

            if result.returncode == 0:
                self.assertions.add_pass(
                    f"{region}-redis-secret-check",
                    f"Secret {secret_key} found in {region} Redis.",
                )
            else:
                self.assertions.add_fail(
                    f"{region}-redis-secret-check",
                    f"Secret check failed: {result.stderr[:200]}",
                )
        except subprocess.TimeoutExpired:
            self.assertions.add_fail(
                f"{region}-redis-secret-check",
                "Secret Redis check timed out.",
            )
        except Exception as e:
            self.assertions.add_fail(
                f"{region}-redis-secret-check",
                f"Secret Redis check error: {str(e)[:100]}",
            )

    @staticmethod
    def _get_utc_timestamp() -> str:
        """Get current UTC timestamp in ISO format."""
        from datetime import datetime

        return datetime.utcnow().isoformat() + "Z"
