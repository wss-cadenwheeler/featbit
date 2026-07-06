# FeatBit-Instrumented OpenTelemetry Demo

Deploys the [OpenTelemetry Demo](https://github.com/open-telemetry/opentelemetry-demo)
(the "Astronomy Shop" microservices app) with selected services rebuilt to evaluate
feature flags through FeatBit's native SDKs instead of the demo's built-in
`flagd`/OpenFeature provider. Used by the control-plane QA harness to generate
realistic multi-service flag-evaluation traffic during continuity/failover testing.

## Layout

| Path | Purpose |
|------|---------|
| `Build-OtelDemoImages.ps1` | Shallow-clones upstream at a pinned tag (default `2.2.0`) into `build/otel-demo-src/`, applies the `custom/` overlays, and builds `featbit-*` images. |
| `Deploy-OtelDemo.ps1` | Installs the demo via the upstream Helm chart with the FeatBit values overrides. |
| `Provision-FeatBitFlags.py` | Creates the demo's feature flags in a FeatBit environment. |
| `custom/` | Per-service overlay files copied over the upstream source before building. |
| `values-featbit.yaml`, `values-min.yaml` | Helm values for the FeatBit-instrumented deployment. |
| `build/` | Working directory (gitignored). The upstream clone is fetched on demand; nothing here is committed. |

## Attribution and licensing

The files under `custom/` are **derived works of the OpenTelemetry Demo**
(© The OpenTelemetry Authors), which is licensed under the
[Apache License 2.0](./LICENSE) — a copy of that license is kept in this
directory. Per its terms:

- Each derived overlay file retains the upstream copyright/SPDX header
  (`Copyright The OpenTelemetry Authors` / `SPDX-License-Identifier: Apache-2.0`)
  and carries a prominent comment describing how it deviates from the upstream
  file it replaces.
- The overlays are based on upstream release **2.2.0** (the tag pinned in
  `Build-OtelDemoImages.ps1`; override with `-DemoVersion`).
- Upstream files without license headers (e.g. `*.csproj`) are reproduced
  without adding one, matching the source.

Any file under `custom/` bearing the OpenTelemetry header is Apache-2.0.
Files that are FeatBit-original rather than derived from upstream — the
PowerShell scripts, `Provision-FeatBitFlags.py`, the Helm values files, and
`featbit_client.py` — are covered by this repository's MIT license.

If you add a new overlay for another demo service, start from the upstream
file, keep its header, and add a comment block summarizing your changes.
