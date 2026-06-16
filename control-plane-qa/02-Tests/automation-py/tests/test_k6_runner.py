import json
import time

from core.k6_runner import DEFAULT_EVENT_PREFIX, K6Event, K6Runner


def _write_log(path, events):
    lines = [
        "plain k6 output\n",
        "time=2026-06-16T10:00:00Z CP09_EVENT not-json-yet\n",
    ]
    for event in events:
        lines.append(f"{DEFAULT_EVENT_PREFIX} {json.dumps(event)}\n")
    lines.append("k6 summary text\n")
    path.write_text("".join(lines), encoding="utf-8")


def test_is_available_returns_false_when_binary_missing(monkeypatch):
    monkeypatch.setattr("core.k6_runner.shutil.which", lambda binary: None)

    assert K6Runner.is_available("missing-k6") is False


def test_read_events_parses_mixed_log_lines(tmp_path):
    log_path = tmp_path / "k6-output.log"
    _write_log(
        log_path,
        [
            {
                "ts": 1_718_480_000_000,
                "vu": 1,
                "cluster": "west",
                "event": "open",
                "code": 101,
                "details": "connected",
            },
            {
                "ts": 1_718_480_001_250,
                "vu": 2,
                "cluster": "east",
                "event": "message",
                "details": "flag update",
            },
        ],
    )
    runner = K6Runner(tmp_path / "script.js", {}, tmp_path)

    events = runner.read_events()

    assert [event.event for event in events] == ["open", "message"]
    assert events[0].ts == 1_718_480_000.0
    assert events[0].vu == 1
    assert events[0].cluster == "west"
    assert events[0].code == 101
    assert events[0].details == "connected"
    assert events[1].code is None


def test_read_events_filters_by_since(tmp_path):
    _write_log(
        tmp_path / "k6-output.log",
        [
            {"ts": 1_000, "vu": 1, "cluster": "west", "event": "open"},
            {"ts": 2_500, "vu": 1, "cluster": "west", "event": "message"},
        ],
    )
    runner = K6Runner(tmp_path / "script.js", {}, tmp_path)

    events = runner.read_events(since=2.0)

    assert [event.event for event in events] == ["message"]


def test_count_events_with_predicate(tmp_path):
    _write_log(
        tmp_path / "k6-output.log",
        [
            {"ts": 1_000, "vu": 1, "cluster": "west", "event": "message"},
            {"ts": 1_200, "vu": 2, "cluster": "east", "event": "message"},
            {"ts": 1_400, "vu": 3, "cluster": "west", "event": "error"},
        ],
    )
    runner = K6Runner(tmp_path / "script.js", {}, tmp_path)

    count = runner.count_events(lambda event: event.cluster == "west")

    assert count == 2


def test_wait_for_count_returns_immediately_when_count_already_met(tmp_path):
    _write_log(
        tmp_path / "k6-output.log",
        [
            {"ts": 1_000, "vu": 1, "cluster": "east", "event": "message"},
            {"ts": 1_100, "vu": 2, "cluster": "east", "event": "message"},
        ],
    )
    runner = K6Runner(tmp_path / "script.js", {}, tmp_path)
    started = time.monotonic()

    count = runner.wait_for_count(
        lambda event: event.cluster == "east",
        count=2,
        timeout=5,
        poll_interval=0.25,
    )

    assert count == 2
    assert time.monotonic() - started < 0.5


def test_k6_event_fields_are_populated_from_sample_json(tmp_path):
    _write_log(
        tmp_path / "k6-output.log",
        [
            {
                "ts": 1_718_480_123_456,
                "vu": 7,
                "cluster": "east",
                "event": "close",
                "code": 1000,
                "details": "normal closure",
                "extra": "preserved",
            }
        ],
    )
    runner = K6Runner(tmp_path / "script.js", {}, tmp_path)

    event = runner.read_events()[0]

    assert event == K6Event(
        ts=1_718_480_123.456,
        vu=7,
        cluster="east",
        event="close",
        code=1000,
        details="normal closure",
        raw={
            "ts": 1_718_480_123_456,
            "vu": 7,
            "cluster": "east",
            "event": "close",
            "code": 1000,
            "details": "normal closure",
            "extra": "preserved",
        },
    )
