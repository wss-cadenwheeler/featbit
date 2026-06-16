# CP-09 k6 WebSocket harness

`cp09-connections.js` is the long-running WebSocket client harness for
`cp09-pod-heartbeats`. It opens real FeatBit evaluation-server streaming
connections in west and east, keeps them alive with pings, reconnects after
unexpected closes, and emits lifecycle events that the Python automation can
assert during pod failover.

## Environment variables

The script reads this contract from `__ENV`:

| Variable | Default | Description |
| -------- | ------- | ----------- |
| `WEST_CLIENTS` | `10` | Non-negative integer. VUs `1..WEST_CLIENTS` connect to west. |
| `EAST_CLIENTS` | `20` | Non-negative integer. Remaining VUs connect to east. |
| `SDK_TYPE` | `server` | `server` or `client`; controls the streaming token type and handshake. |
| `SERVER_SECRET` | none | Required when `SDK_TYPE=server`. |
| `CLIENT_SECRET` | none | Required when `SDK_TYPE=client`. |
| `WEST_PORT` | `5100` | West evaluation-server port. |
| `EAST_PORT` | `5101` | East evaluation-server port. |
| `HOST` | `localhost` | Hostname used for both streaming URLs. |
| `EVENT_LOG_PREFIX` | `CP09_EVENT` | Prefix for structured lifecycle events printed to stdout. |
| `RUN_DURATION` | `5m` | k6 duration string. The run exits after this duration unless terminated first. |

`WEST_CLIENTS + EAST_CLIENTS` must be greater than zero.

## Event log format

k6 cannot append to an arbitrary status file, so the harness writes JSONL events
to stdout. Each event line starts with the configured prefix, followed by one
JSON object:

```text
CP09_EVENT {"ts":1716500000000,"vu":1,"cluster":"west","event":"open","code":null,"details":"initial"}
```

Common `event` values are `open`, `close`, `reconnect`, `message`, and `error`.
The `ts` value is Unix time in milliseconds. The Python runner converts it to
seconds when creating `K6Event` objects.

## Standalone invocation

From `benchmark\k6-scripts\cp09`:

```powershell
k6 run -e WEST_CLIENTS=2 -e EAST_CLIENTS=2 -e SERVER_SECRET=xxx -e RUN_DURATION=30s cp09-connections.js
```

Use `CLIENT_SECRET=... -e SDK_TYPE=client` instead when validating client SDK
streaming connections.

## Python automation

The CP-09 scenario uses the `K6Runner` helper in
`control-plane-qa\02-Tests\automation-py\core\k6_runner.py` to launch this
script as a background k6 process, export the k6 summary into the scenario
artifacts directory, tail stdout/stderr for `CP09_EVENT` lines, and stop the run
cleanly from the scenario's cleanup path. `cp09-pod-heartbeats` then combines
those WebSocket lifecycle events with Redis heartbeat and connection-migration
assertions.
