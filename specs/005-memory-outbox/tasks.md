# Tasks — SPEC-005

- [x] **T1** — Write the spec, plan and task breakdown.
  - Depends on: —
  - Files: `specs/005-memory-outbox/{spec,plan,tasks}.md`.
  - Verification: DoR checklist passes — scenarios, acceptance criteria and open questions are settled.

- [x] **T2** — Add `InMemoryOutboxSettings` and `InMemoryOutboxStore` (records, group index, capacity,
  `TimeProvider`).
  - Depends on: T1.
  - Files: `src/Libraries/Outbox/Sa.Outbox/InMemory/{InMemoryOutboxSettings,InMemoryOutboxStore}.cs`.
  - Verification: compiles; unit tests for index ordering and capacity added in T8.

- [x] **T3** — Add `InMemoryOutboxBulkWriter`.
  - Depends on: T2.
  - Files: `src/Libraries/Outbox/Sa.Outbox/InMemory/InMemoryOutboxBulkWriter.cs`.
  - Verification: `InsertBulk` stores serialized payloads, returns the stored count, throws on non-null
    `context` and on capacity overflow (AC-6, AC-7).

- [x] **T4** — Add `InMemoryOutboxDeliveryManager` (rent / return / extend, leases, attempt accounting).
  - Depends on: T2, T3.
  - Files: `src/Libraries/Outbox/Sa.Outbox/InMemory/InMemoryOutboxDeliveryManager.cs`.
  - Verification: AC-2, AC-3, AC-4, AC-5 covered by T8.

- [x] **T5** — Add `InMemoryOutboxTenantDetector`.
  - Depends on: T2.
  - Files: `src/Libraries/Outbox/Sa.Outbox/InMemory/InMemoryOutboxTenantDetector.cs`.
  - Verification: AC-8.

- [x] **T6** — Add `Setup.AddSaOutboxUsingInMemory` and the `InMemory` descriptor.
  - Depends on: T3–T5.
  - Files: `src/Libraries/Outbox/Sa.Outbox/InMemory/Setup.cs`.
  - Verification: AC-1; host start succeeds with a consumer group and fails as expected with a wrong scope key.

- [x] **T7** — Keep `Sa.Outbox` at 0.18.0 (the release version is unified across packages).
  - Depends on: T6.
  - Files: `src/Libraries/Outbox/Sa.Outbox/Sa.Outbox.csproj`.
  - Verification: `dotnet build` produces `Sa.Outbox.0.18.0.nupkg`.

- [x] **T8** — Registration and publish tests (no container).
  - Depends on: T6.
  - Files: `src/Tests/Sa.Outbox.Tests/InMemory/{RegistrationTests,PublishTests}.cs`.
  - Verification: AC-1, AC-6, AC-7, AC-9 green.

- [x] **T9** — Delivery pipeline tests (rent / return / extend / detector).
  - Depends on: T8.
  - Files: `src/Tests/Sa.Outbox.Tests/InMemory/{DeliveryTests,LeaseTests,TenantDetectorTests}.cs`.
  - Verification: AC-2, AC-3, AC-4, AC-5, AC-8, AC-10 green; suite runs without Docker.

- [x] **T10** — Add the `InMemoryOutbox.ConsoleApp` sample and register it in `src/Sa.slnx`.
  - Depends on: T6.
  - Files: `src/Samples/InMemoryOutbox.ConsoleApp/**`, `src/Sa.slnx`.
  - Verification: AC-11 — `dotnet run --project src/Samples/InMemoryOutbox.ConsoleApp` completes with no
    external service.

- [x] **T11** — Documentation: provider table and limitations in `Sa.Outbox/Readme.md` (+ ru), root
  `README.md` (+ ru), amend `PlugServices/Readme.md`.
  - Depends on: T10.
  - Files: as listed.
  - Verification: AC-12; link check → 0 broken; the limitation block names every non-goal.

- [x] **T12** — ADR-0003: why the storage implementation lives in the core package.
  - Depends on: T1.
  - Files: `docs/decisions/ADR-0003-inmemory-provider-in-core.md`.
  - Verification: follows the `ADR-0001` format; records the rejected sibling-project alternative.

- [x] **T13** — Regression verification.
  - Depends on: T7–T12.
  - Files: whole solution.
  - Verification: `dotnet build src/Sa.slnx -c Release` → 0 errors (1 pre-existing CS0108 warning in
    `Sa.Outbox.PostgreSqlTests`, unrelated); `Sa.Outbox.Tests` 214/214, `Sa.Outbox.PostgreSqlTests`
    275/275, `Sa.Outbox.RedisTests` 52/52 green; `Sa.Outbox.0.18.0.nupkg` produced, every other
    package version unchanged; sample runs end-to-end without infrastructure.

## Completion checks
- [ ] Every acceptance criterion mapped to verification.
- [ ] Tests/build/package run for affected projects.
- [ ] Docs and spec match actual behavior.
- [ ] Public API and NuGet metadata checked where relevant.
