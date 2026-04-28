# Control-Plane Scenario Runner (API-first)

This runner starts implementation for CP-02 and CP-03 automation using featbit-api in west and east as the only mutation path.

## Scenarios

- cp02-west-to-east
- cp02-east-to-west
- cp03-west-with-east-redis-outage
- cp03-east-with-west-redis-outage

## Required Inputs

- Scenario name
- Environment ID
- Authorization header value (for example: `Bearer <token>` or `api-<token>`)

## One-liner Examples

### CP-02 west-to-east

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp02-west-to-east -EnvId "00000000-0000-0000-0000-000000000000" -ApiAuthorizationHeader "api-REPLACE_ME"
```

### CP-02 east-to-west

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp02-east-to-west -EnvId "00000000-0000-0000-0000-000000000000" -ApiAuthorizationHeader "api-REPLACE_ME"
```

### CP-03 west with east outage

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp03-west-with-east-redis-outage -EnvId "00000000-0000-0000-0000-000000000000" -ApiAuthorizationHeader "api-REPLACE_ME" -StartDisruptionCommand "Write-Output 'TODO: block east redis path'" -StopDisruptionCommand "Write-Output 'TODO: restore east redis path'"
```

### CP-03 east with west outage

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp03-east-with-west-redis-outage -EnvId "00000000-0000-0000-0000-000000000000" -ApiAuthorizationHeader "api-REPLACE_ME" -StartDisruptionCommand "Write-Output 'TODO: block west redis path'" -StopDisruptionCommand "Write-Output 'TODO: restore west redis path'"
```

## Optional One-liner Checks

You can pass check commands as exact one-liners. If omitted, checks are marked `skipped`.

- `-SourceTopicCheckCommand`
- `-DownstreamTopicCheckCommand`
- `-RetryLogCheckCommand`
- `-RedisWestCheckCommand`
- `-RedisEastCheckCommand`

Example:

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp02-west-to-east -EnvId "00000000-0000-0000-0000-000000000000" -ApiAuthorizationHeader "api-REPLACE_ME" -SourceTopicCheckCommand "Write-Output 'source-topic check OK'" -DownstreamTopicCheckCommand "Write-Output 'downstream check OK'"
```

## Output

Each run writes artifacts to:

`control-plane-qa/artifacts/<scenario>/<runId>/`

Files:

- `summary.json`
- `assertions.json`
- `timeline.json`
