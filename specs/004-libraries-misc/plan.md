# Plan — SPEC-004: Move standalone libraries into `Misc/`

## Scope and assumptions

- Only the three infrastructure standalone projects move: `Sa.Partitional.PostgreSql`, `Sa.Schedule`,
  `Sa.Utils.WorkQueue`.
- The shared-source base `Sa` stays directly in `src/Libraries/` (not packed, no group).
- Same change set as SPEC-003: move + all path-dependent references + docs; no functional change.

## Affected projects/files

| Path/project | Change | Reason | Risk |
|---|---|---|---|
| `src/Libraries/{Sa.Partitional.PostgreSql,Sa.Schedule,Sa.Utils.WorkQueue}/*` | `git mv` into `src/Libraries/Misc/` | new group | depth changes |
| `src/Libraries/Misc/*/*.csproj` | props import → `..\..\..\…`, shared-source links → `..\..\Sa\…` | deeper by one | medium |
| `src/Libraries/Outbox/Sa.Outbox/*.csproj`, `Sa.Outbox.PostgreSql`, `Hybrid/Sa.HybridFileStorage.Postgres` | cross-group refs → `..\..\Misc\…` | new group | low |
| `src/Samples/*`, `src/Tests/*`, `src/Benchmarks/*/*.csproj` | refs → `..\..\Libraries\Misc\…` | new group | low |
| `src/Sa.slnx` | new `/Libraries/Misc/` solution folder | group visibility | low |
| docs | paths updated | consistency | low |

## Design

- Group name: `Misc` (single group for infrastructure standalone libraries; declined `Core`/`Infrastructure`).
- Packaging stays group-agnostic (find by name), no `PROJECTS` list change.

## Compatibility and migration

- Package IDs, versions, TFMs, dependencies unchanged; no published artifact affected.

## Test strategy

- Unit/integration: same suites; build + package first.

## Rollout and observability

- Single commit/PR per repo convention; one revert undoes the move.

## Risks and mitigations

- Missed `..` fails at restore/build; scripted resolution check + full build cover it.

## Rollback

- One revert; no version bump.

## Decisions required

- None outstanding.
