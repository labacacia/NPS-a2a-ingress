English | [中文版](./CHANGELOG.cn.md)

# Changelog — A2A Ingress (`LabAcacia.A2aIngress`)

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Until NPS reaches v1.0 stable, every repository in the suite is synchronized to the same pre-release version tag.

---

## [1.0.0-alpha.14] — Unreleased

### Docs
- Align README release/protocol badges with the suite alpha.14 candidate documentation boundary and current protocol versions.

### Packaging
- Emit SourceLink-enabled `.snupkg` symbol packages and include the README in NuGet packages.
- Remove stale Bridge-era obsolete attributes from the current Ingress API surface.

## [1.0.0-alpha.13] — 2026-06-13

### Changed
- Suite version alignment to `1.0.0-alpha.13`. No functional changes in this repository for this release; the bump keeps the whole NPS suite on a single version tag (oracle: NPS-Dev `version.yaml`).

## [1.0.0-alpha.8] — 2026-05-28

### Synced

- Version bumped `1.0.0-alpha.7` → `1.0.0-alpha.8` in lockstep with the NPS suite.
  No functional changes in A2A Ingress itself.
- Suite highlights: RFC-0005 `ReputationPolicyEvaluator` in .NET SDK; cgn_limit pre-execution enforcement; RFC-0002 and RFC-0005 promoted to Accepted.

---

## [1.0.0-alpha.7] — 2026-05-17

### Synced

- Version bumped `1.0.0-alpha.6` → `1.0.0-alpha.7` in lockstep with the NPS suite.
  No functional changes in A2A Ingress itself.
- Suite highlights at alpha.7: `ReputationLogClient` (RFC-0004 Ph2) across all six
  SDKs; AnchorNodeClient test parity for Python / Go / Java / Rust; NIP CA Server
  gains CR-0005 RA model database migration (`db/003_ra_model.sql`).

---

## [1.0.0-alpha.6] — 2026-05-14

### Changed

- **Version bump to `1.0.0-alpha.6`** — synchronized with NPS suite alpha.6 release. No functional changes in this ingress adapter.

---

## [1.0.0-alpha.5] — 2026-05-01

### Synced

- Version bumped 1.0.0-alpha.4 → 1.0.0-alpha.5 in lockstep with the
  rest of the NPS suite. No functional changes in A2A Ingress itself.
- Suite highlights at alpha.5: nps-ledger Phase 3 STH gossip federation,
  `AnchorNodeMiddleware` `node_kind` deprecation warning (alias removed at
  alpha.6), NDP DNS TXT fallback across all six SDKs, 30 new NWP error
  code constants.
- 18 tests still green.
- Tracks suite consolidation: alpha.5.2 tracking-only sub-version folded
  back into alpha.5 (refs #28). Per the no-sub-versions policy, any
  per-package sub-patch label is dropped and its content merged into the
  parent suite version.

---

## [1.0.0-alpha.4] — 2026-04-30

### Synced

- Version bumped 1.0.0-alpha.3 → 1.0.0-alpha.4 in lockstep with the
  rest of the NPS suite. No functional changes in A2A Ingress itself.
- `LabAcacia.NPS.NWP` dependency follows to alpha.4, picking up
  `LabAcacia.NPS.NWP.Anchor` topology query types (NPS-CR-0002) at
  the SDK layer. A2A Ingress does not surface those over A2A at
  alpha.4.
- 18 tests still green.

### Summary

- Exposes NWP Memory / Action / Complex Nodes as Google A2A v0.2
  servers. External A2A clients can call NPS Nodes without an NPS
  SDK on the client side.

---

## [1.0.0-alpha.3] — 2026-04-26

### Renamed (BREAKING)

- Package renamed `LabAcacia.A2aBridge` → `LabAcacia.A2aIngress` per [NPS-CR-0001](https://github.com/labacacia/NPS-Dev/blob/dev/spec/cr/NPS-CR-0001-anchor-bridge-split.md). The new spec-level **Bridge Node** type (NWP §2A) carries the *NPS → external* direction; this package carries the **inverse** direction (external → NPS) and is therefore renamed `*Ingress`. The on-the-wire surface is identical to alpha.2; only the assembly name + namespace changed. Consumers update `<PackageReference Include="LabAcacia.A2aBridge"/>` → `LabAcacia.A2aIngress` and the `using LabAcacia.A2aBridge;` import.
- The corresponding GitHub repository was renamed `labacacia/NPS-a2a-bridge` → `labacacia/NPS-a2a-ingress`. GitHub redirects the old URL automatically; existing clones can update with `git remote set-url origin https://github.com/labacacia/NPS-a2a-ingress.git`.
- Tests still pass at the same count as alpha.2 (no functional change beyond rename).

### Synced

- Version bumped 1.0.0-alpha.2 → 1.0.0-alpha.3 in lockstep with the rest of the NPS suite.

---

## [1.0.0-alpha.2] — 2026-04-19

### Changed

- Version bump to `1.0.0-alpha.2` for suite-wide synchronization. No functional changes since `1.0.0-alpha.1`.
- 18 tests green.

### Summary

- Exposes NWP Action / Complex / Gateway Nodes as Google A2A v0.2 servers; `GET /.well-known/agent.json`, JSON-RPC 2.0 `tasks/send` / `tasks/get` / `tasks/cancel`.

---

## [1.0.0-alpha.1] — 2026-04-10

Initial release under the NPS suite `v1.0.0-alpha.1` umbrella tag.

[1.0.0-alpha.7]: https://github.com/labacacia/NPS-a2a-ingress/releases/tag/v1.0.0-alpha.7
[1.0.0-alpha.6]: https://github.com/labacacia/NPS-a2a-ingress/releases/tag/v1.0.0-alpha.6
[1.0.0-alpha.5]: https://github.com/labacacia/NPS-a2a-ingress/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://github.com/labacacia/NPS-a2a-ingress/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://github.com/labacacia/NPS-a2a-ingress/releases/tag/v1.0.0-alpha.3
[1.0.0-alpha.2]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.1
