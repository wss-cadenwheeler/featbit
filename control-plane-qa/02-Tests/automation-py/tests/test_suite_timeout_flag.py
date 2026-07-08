"""The suite command must expose --timeout-seconds (#113 follow-up).

Only the scenario command had it; suite runs hardcoded 60s, so the
convergence window could not be tuned for suite runs (cp14's first
post-flip gated commit takes ~60-65s on a fresh deploy).
"""

from click.testing import CliRunner

from cli.main import cli


def _suite_param(name):
    for param in cli.commands["suite"].params:
        if param.name == name:
            return param
    return None


def test_suite_help_includes_timeout_seconds():
    result = CliRunner().invoke(cli, ["suite", "cp14", "--help"])
    assert "--timeout-seconds" in result.output


def test_suite_timeout_default_honors_env(monkeypatch):
    monkeypatch.setenv("TIMEOUT_SECONDS", "180")
    param = _suite_param("timeout_seconds")
    assert param is not None, "suite command has no timeout_seconds option"
    default = param.default() if callable(param.default) else param.default
    assert default == 180
