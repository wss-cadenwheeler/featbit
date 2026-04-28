# Control-Plane Scenario Runner (API-first)

This runner starts implementation for CP-02 and CP-03 automation using featbit-api in west and east as the only mutation path.

Default behavior now supports direct login with:

- Username: test@featbit.com
- Password: 123456
- West API base URL: https://featbit-api.west.local
- East API base URL: https://featbit-api.east.local

## Fresh Install Bootstrap

Use the seed script first on a fresh install. It can initialize org/project/environment and create the prerequisite flags.

### Seed one-liner

```powershell
.\control-plane-qa\automation\Seed-ControlPlaneQaData.ps1 -ApiBaseUrl "https://featbit-api.west.local" -ForceFlagsOff
```

Defaults created by the seed script:

- Organization: `playground`
- Project: `control-plane-test`
- Environment: `Dev` (`dev`)
- Flags: `ff-cp02-west`, `ff-cp02-east`, `ff-cp03-resilience`

## Scenarios

- cp02-west-to-east
- cp02-east-to-west
- cp03-west-with-east-redis-outage
- cp03-east-with-west-redis-outage

## Required Inputs

- Scenario name
- Environment ID

Optional:

- Authorization header value (for example: `Bearer <token>` or `api-<token>`)
- LoginEmail and LoginPassword if you do not want defaults

## One-liner Examples

### CP-02 west-to-east

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp02-west-to-east -EnvId "00000000-0000-0000-0000-000000000000"
```

### CP-02 east-to-west

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp02-east-to-west -EnvId "00000000-0000-0000-0000-000000000000"
```

### CP-03 west with east outage

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp03-west-with-east-redis-outage -EnvId "00000000-0000-0000-0000-000000000000" -StartDisruptionCommand "Write-Output 'TODO: block east redis path'" -StopDisruptionCommand "Write-Output 'TODO: restore east redis path'"
```

### CP-03 east with west outage

```powershell
.\control-plane-qa\automation\Run-ControlPlaneScenario.ps1 -Scenario cp03-east-with-west-redis-outage -EnvId "00000000-0000-0000-0000-000000000000" -StartDisruptionCommand "Write-Output 'TODO: block west redis path'" -StopDisruptionCommand "Write-Output 'TODO: restore west redis path'"
```

## Suite Wrapper (Optional)

Run both directions of a suite and optionally seed data first.

### CP-02 suite with seeding

```powershell
.\control-plane-qa\automation\Invoke-CPScenarios.ps1 -Suite cp02 -SeedData
```

### CP-03 suite with seeding

```powershell
.\control-plane-qa\automation\Invoke-CPScenarios.ps1 -Suite cp03 -SeedData -StartEastDisruptionCommand "Write-Output 'TODO: block east redis path'" -StopEastDisruptionCommand "Write-Output 'TODO: restore east redis path'" -StartWestDisruptionCommand "Write-Output 'TODO: block west redis path'" -StopWestDisruptionCommand "Write-Output 'TODO: restore west redis path'"
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
