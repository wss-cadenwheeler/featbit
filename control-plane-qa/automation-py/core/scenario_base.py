"""Scenario framework: base class and scenario definition utilities."""

ABC_module = __import__("abc")
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, Optional
import json
import subprocess
import time
import uuid

from .api_client import ApiClient, extract_data
from .assertions import AssertionRegistry
from .auth import resolve_authorization_header, resolve_request_context
from .models import FlagState, ScenarioConfig, TimelineEvent


@dataclass
class ScenarioDefinition:
    """Definition of a scenario variant."""

    scenario_type: str  # cp02, cp03
    source_region: str
    target_region: str
    default_flag_key: str
    target_status: bool


class BaseScenario(ABC):
    """Base class for all scenario implementations."""

    def __init__(self, config: ScenarioConfig):
        """Initialize scenario.

        Args:
            config: ScenarioConfig with all parameters
        """
        self.config = config
        self.run_id = str(uuid.uuid4())
        self.run_started_utc = datetime.utcnow().isoformat() + "Z"
        self.timeline: list[TimelineEvent] = []
        self.assertions = AssertionRegistry()
        self.artifact_dir: Optional[Path] = None

    @abstractmethod
    def definition(self) -> ScenarioDefinition:
        """Return scenario definition (source/target region, flag key, etc.)."""
        raise NotImplementedError

    @abstractmethod
    def run(self) -> bool:
        """Execute scenario. Return True if passed, False if failed."""
        raise NotImplementedError

    def setup_artifacts(self) -> None:
        """Create artifact directory."""
        artifacts_root = Path(self.config.artifacts_root)
        self.artifact_dir = (
            artifacts_root
            / self.config.scenario_name
            / self.run_id
        )
        self.artifact_dir.mkdir(parents=True, exist_ok=True)

    def write_artifacts(self) -> None:
        """Write timeline, assertions, and summary JSON files."""
        if not self.artifact_dir:
            return

        # Write timeline
        timeline_data = [json.loads(event.json()) for event in self.timeline]
        timeline_path = self.artifact_dir / "timeline.json"
        timeline_path.write_text(json.dumps(timeline_data, indent=2), encoding="utf-8")

        # Write assertions
        assertions_data = [json.loads(a.json()) for a in self.assertions.assertions]
        assertions_path = self.artifact_dir / "assertions.json"
        assertions_path.write_text(json.dumps(assertions_data, indent=2), encoding="utf-8")

        # Write summary
        definition = self.definition()
        failed_assertions = self.assertions.get_failed()
        summary_data = {
            "runId": self.run_id,
            "scenario": self.config.scenario_name,
            "startedUtc": self.run_started_utc,
            "finishedUtc": datetime.utcnow().isoformat() + "Z",
            "envId": self.config.env_id,
            "flagKey": definition.default_flag_key,
            "sourceRegion": definition.source_region,
            "targetRegion": definition.target_region,
            "expectedStatus": definition.target_status,
            "passed": self.assertions.all_passed(),
            "failedAssertions": [json.loads(a.json()) for a in failed_assertions],
            "artifacts": {
                "summary": str(self.artifact_dir / "summary.json"),
                "assertions": str(assertions_path),
                "timeline": str(timeline_path),
            },
        }
        summary_path = self.artifact_dir / "summary.json"
        summary_path.write_text(json.dumps(summary_data, indent=2), encoding="utf-8")

    def add_timeline_event(
        self,
        event_type: str,
        **kwargs: Any,
    ) -> None:
        """Add event to timeline."""
        event_dict = {
            "type": event_type,
            "timestamp_utc": datetime.utcnow().isoformat() + "Z",
            **kwargs,
        }
        # Remove None values
        event_dict = {k: v for k, v in event_dict.items() if v is not None}
        event = TimelineEvent(**event_dict)
        self.timeline.append(event)

    def get_api_base_url(self, region: str) -> str:
        """Get API base URL for region."""
        if region == "west":
            return self.config.west_api_base_url.rstrip("/")
        else:
            return self.config.east_api_base_url.rstrip("/")

    def toggle_flag(
        self,
        base_url: str,
        flag_key: str,
        status: bool,
        headers: Dict[str, str],
    ) -> Dict[str, Any]:
        """Toggle a feature flag.

        Args:
            base_url: API base URL
            flag_key: Feature flag key
            status: Target status (true/false)
            headers: Request headers with auth

        Returns:
            API response
        """
        client = ApiClient(
            base_url,
            self.config.skip_certificate_check,
            self.config.api_version,
        )
        status_str = "true" if status else "false"
        endpoint = (
            f"/api/v{self.config.api_version}/envs/{self.config.env_id}/"
            f"feature-flags/{flag_key}/toggle/{status_str}"
        )
        response = client.put(endpoint, body="{}", headers=headers)
        return response

    def get_flag_state(
        self,
        base_url: str,
        flag_key: str,
        region: str,
        headers: Dict[str, str],
    ) -> FlagState:
        """Get current flag state from API.

        Args:
            base_url: API base URL
            flag_key: Feature flag key
            region: Source/target region label
            headers: Request headers with auth

        Returns:
            FlagState with current flag status
        """
        client = ApiClient(
            base_url,
            self.config.skip_certificate_check,
            self.config.api_version,
        )
        endpoint = (
            f"/api/v{self.config.api_version}/envs/{self.config.env_id}/"
            f"feature-flags/{flag_key}"
        )
        try:
            response = client.get(endpoint, headers=headers)
            data = extract_data(response)
            return FlagState(
                region=region,
                observed_at_utc=datetime.utcnow().isoformat() + "Z",
                is_enabled=data.get("isEnabled") if data else None,
                key=data.get("key") if data else None,
                version=data.get("version") if data else None,
                id=data.get("id") if data else None,
                error=None,
            )
        except Exception as e:
            return FlagState(
                region=region,
                observed_at_utc=datetime.utcnow().isoformat() + "Z",
                is_enabled=None,
                key=flag_key,
                version=None,
                id=None,
                error=str(e),
            )

    def poll_convergence(
        self,
        source_base_url: str,
        target_base_url: str,
        flag_key: str,
        expected_status: bool,
        headers: Dict[str, str],
    ) -> tuple[bool, Optional[FlagState], Optional[FlagState]]:
        """Poll until source and target flag states converge.

        Args:
            source_base_url: Source region API URL
            target_base_url: Target region API URL
            flag_key: Feature flag key
            expected_status: Expected final status
            headers: Request headers with auth

        Returns:
            (converged: bool, source_state: FlagState, target_state: FlagState)
        """
        deadline = time.time() + self.config.timeout_seconds

        while time.time() < deadline:
            source_state = self.get_flag_state(
                source_base_url, flag_key, "source", headers
            )
            target_state = self.get_flag_state(
                target_base_url, flag_key, "target", headers
            )

            self.add_timeline_event(
                "poll",
                source=json.loads(source_state.json()),
                target=json.loads(target_state.json()),
            )

            source_match = (
                source_state.error is None
                and source_state.is_enabled == expected_status
            )
            target_match = (
                target_state.error is None
                and target_state.is_enabled == expected_status
            )

            if source_match and target_match:
                return True, source_state, target_state

            time.sleep(self.config.poll_interval_ms / 1000.0)

        return False, None, None

    def run_optional_check(
        self,
        name: str,
        command: Optional[str],
        required: bool = False,
    ) -> None:
        """Run optional check command.

        Args:
            name: Check name
            command: Shell command to run
            required: If True, fail if command not provided or fails
        """
        if not command:
            if required:
                self.assertions.add_fail(
                    name, "Command is required but was not provided."
                )
            else:
                self.assertions.add_skip(name, "Not configured.")
            return

        try:
            result = subprocess.run(
                command,
                shell=True,
                capture_output=True,
                text=True,
                timeout=30,
            )
            output = result.stdout + result.stderr

            self.add_timeline_event(
                "optional-check",
                check=name,
                output=output.strip(),
            )

            if result.returncode == 0:
                self.assertions.add_pass(name, "Command executed successfully.")
            else:
                self.assertions.add_fail(
                    name,
                    f"Command failed with exit code {result.returncode}. "
                    f"Output: {output.strip()}",
                )
        except subprocess.TimeoutExpired:
            self.assertions.add_fail(name, "Command timed out after 30 seconds.")
        except Exception as e:
            self.assertions.add_fail(name, f"Command execution error: {e}")

    def _run_kubectl(self, args: list[str], timeout: int = 20) -> subprocess.CompletedProcess[str]:
        """Run kubectl command and capture output."""
        return subprocess.run(
            ["kubectl", *args],
            capture_output=True,
            text=True,
            timeout=timeout,
        )

    def _discover_redis_pod(self, context: str, namespace: str) -> Optional[str]:
        """Discover a Redis pod name in the target cluster."""
        by_label = self._run_kubectl(
            [
                "--context",
                context,
                "-n",
                namespace,
                "get",
                "pods",
                "-l",
                "app=redis",
                "-o",
                "json",
            ]
        )
        if by_label.returncode == 0:
            try:
                items = json.loads(by_label.stdout).get("items", [])
                for item in items:
                    name = item.get("metadata", {}).get("name")
                    phase = item.get("status", {}).get("phase")
                    if name and phase == "Running":
                        return name
            except json.JSONDecodeError:
                pass

        all_pods = self._run_kubectl(
            ["--context", context, "-n", namespace, "get", "pods", "-o", "json"]
        )
        if all_pods.returncode != 0:
            return None

        try:
            items = json.loads(all_pods.stdout).get("items", [])
            for item in items:
                name = item.get("metadata", {}).get("name", "")
                phase = item.get("status", {}).get("phase")
                if name.startswith("redis") and phase == "Running":
                    return name
        except json.JSONDecodeError:
            return None

        return None

    def run_redis_check(
        self,
        region: str,
        command: Optional[str],
        flag_id: Optional[str] = None,
        flag_key: Optional[str] = None,
        expected_status: Optional[bool] = None,
    ) -> None:
        """Run Redis check via provided command or automatic Kubernetes discovery.

        Auto-check targets GUID-based key lookup using featbit:flags:<flag_id>.
        """
        assertion_name = f"redis-{region}-check"

        if command:
            self.run_optional_check(assertion_name, command, required=True)
            return

        context = region
        namespace = "featbit"
        service_name = "redis"

        try:
            svc_result = self._run_kubectl(
                [
                    "--context",
                    context,
                    "-n",
                    namespace,
                    "get",
                    "svc",
                    service_name,
                    "-o",
                    "json",
                ]
            )
            if svc_result.returncode != 0:
                self.assertions.add_fail(
                    assertion_name,
                    f"Auto-discovery failed: could not read service '{service_name}' in context '{context}'. {svc_result.stderr.strip()}",
                )
                return

            svc = json.loads(svc_result.stdout)
            cluster_ip = svc.get("spec", {}).get("clusterIP")
            ports = svc.get("spec", {}).get("ports", [])
            redis_port = ports[0].get("port") if ports else 6379

            pod_name = self._discover_redis_pod(context=context, namespace=namespace)
            if not pod_name:
                self.assertions.add_fail(
                    assertion_name,
                    f"Auto-discovery failed: no running redis pod found in context '{context}' namespace '{namespace}'.",
                )
                return

            ping_result = self._run_kubectl(
                [
                    "--context",
                    context,
                    "-n",
                    namespace,
                    "exec",
                    pod_name,
                    "--",
                    "redis-cli",
                    "-h",
                    service_name,
                    "-p",
                    str(redis_port),
                    "ping",
                ],
                timeout=30,
            )

            output = (ping_result.stdout + ping_result.stderr).strip()
            if ping_result.returncode != 0 or "PONG" not in ping_result.stdout.upper():
                self.assertions.add_fail(
                    assertion_name,
                    f"Auto-check failed for context={context}, namespace={namespace}, pod={pod_name}, endpoint={cluster_ip}:{redis_port}. Output: {output}",
                )
                return

            if not flag_id:
                self.assertions.add_fail(
                    assertion_name,
                    f"Redis check requires flag_id for GUID lookup in context={context}.",
                )
                return

            keys: list[str] = []
            sampled_values: list[str] = []
            lookup_mode = "flag_id"

            primary_key = f"featbit:flag:{flag_id}"
            get_primary_result = self._run_kubectl(
                [
                    "--context",
                    context,
                    "-n",
                    namespace,
                    "exec",
                    pod_name,
                    "--",
                    "redis-cli",
                    "-h",
                    service_name,
                    "-p",
                    str(redis_port),
                    "GET",
                    primary_key,
                ],
                timeout=30,
            )
            primary_value = (get_primary_result.stdout + get_primary_result.stderr).strip()
            if get_primary_result.returncode == 0 and primary_value and "(nil)" not in primary_value.lower():
                keys = [primary_key]
                sampled_values.append(primary_value)
            else:
                scan_result = self._run_kubectl(
                    [
                        "--context",
                        context,
                        "-n",
                        namespace,
                        "exec",
                        pod_name,
                        "--",
                        "redis-cli",
                        "-h",
                        service_name,
                        "-p",
                        str(redis_port),
                        "--scan",
                        "--pattern",
                        f"{primary_key}*",
                    ],
                    timeout=30,
                )
                if scan_result.returncode == 0:
                    keys = [line.strip() for line in scan_result.stdout.splitlines() if line.strip()]
                else:
                    self.assertions.add_fail(
                        assertion_name,
                        f"Redis key scan failed for flag_id={flag_id} (key=featbit:flag:{flag_id}) in context={context}. Output: {(scan_result.stdout + scan_result.stderr).strip()}",
                    )
                    return

            if not keys:
                # Wildcard scan to surface the actual key format stored in Redis so the
                # expected prefix can be corrected.
                diag_scan = self._run_kubectl(
                    [
                        "--context",
                        context,
                        "-n",
                        namespace,
                        "exec",
                        pod_name,
                        "--",
                        "redis-cli",
                        "-h",
                        service_name,
                        "-p",
                        str(redis_port),
                        "--scan",
                        "--pattern",
                        f"*{flag_id}*",
                    ],
                    timeout=30,
                )
                diag_keys = [line.strip() for line in diag_scan.stdout.splitlines() if line.strip()]
                diag_hint = (
                    f" Wildcard scan found keys: {diag_keys}" if diag_keys
                    else " Wildcard scan also found no keys containing the flag_id."
                )
                self.assertions.add_fail(
                    assertion_name,
                    f"Redis is reachable but no keys found for flag_id={flag_id} "
                    f"(tried key=featbit:flag:{flag_id}) in context={context}.{diag_hint}",
                )
                return

            for key in keys[:5]:
                get_result = self._run_kubectl(
                    [
                        "--context",
                        context,
                        "-n",
                        namespace,
                        "exec",
                        pod_name,
                        "--",
                        "redis-cli",
                        "-h",
                        service_name,
                        "-p",
                        str(redis_port),
                        "GET",
                        key,
                    ],
                    timeout=30,
                )
                text = (get_result.stdout + get_result.stderr).strip()
                sampled_values.append(text)

            expected_state_ok = True
            if expected_status is not None:
                expected_token = f'"isenabled":{str(expected_status).lower()}'
                expected_state_ok = any(expected_token in value.lower() for value in sampled_values)

            self.add_timeline_event(
                "redis-auto-check",
                check=assertion_name,
                output=output,
                source={
                    "context": context,
                    "namespace": namespace,
                    "service": service_name,
                    "clusterIp": cluster_ip,
                    "port": redis_port,
                    "pod": pod_name,
                    "lookupMode": lookup_mode,
                    "flagId": flag_id,
                    "flagKey": flag_key,
                    "matchedKeyCount": len(keys),
                    "sampledKeys": keys[:5],
                    "expectedStatus": expected_status,
                    "expectedStatusMatched": expected_state_ok,
                },
            )

            if expected_state_ok:
                self.assertions.add_pass(
                    assertion_name,
                    f"Redis contains {len(keys)} key(s) for flag_id={flag_id} in context={context}.",
                )
                return

            self.assertions.add_fail(
                assertion_name,
                f"Redis contains keys for flag_key={flag_key} in context={context}, but sampled values did not include expected isEnabled={expected_status}.",
            )
        except subprocess.TimeoutExpired:
            self.assertions.add_fail(
                assertion_name,
                f"Auto-check timed out while querying Redis in context '{context}'.",
            )
        except Exception as e:
            self.assertions.add_fail(assertion_name, f"Auto-check error: {e}")

    def run_disruption_command(
        self,
        phase: str,  # start, stop
        command: Optional[str],
    ) -> None:
        """Run disruption command (start or stop).

        Args:
            phase: Phase (start or stop)
            command: Shell command to run

        Raises:
            RuntimeError: If command not provided or fails
        """
        if not command:
            raise RuntimeError(f"{phase} disruption command is required for CP-03.")

        try:
            result = subprocess.run(
                command,
                shell=True,
                capture_output=True,
                text=True,
                timeout=60,
            )
            output = result.stdout + result.stderr

            self.add_timeline_event(
                "disruption-command",
                phase=phase,
                output=output.strip(),
            )

            if result.returncode != 0:
                raise RuntimeError(
                    f"Disruption command failed: {output.strip()}"
                )
        except subprocess.TimeoutExpired as e:
            raise RuntimeError(f"Disruption command timed out: {e}") from e
        except Exception as e:
            raise RuntimeError(f"Disruption command error: {e}") from e
