# World-scoped Resource Owner Identity Audit

Date: 2026-11-26

Repository: Lazyair150/jar-system-dotnet  
Branch: architecture/core-liquid-identity-consolidation  
PR: #63 (Open / Draft; not merged)  
Starting verified documentation commit: f53a691a4b79c7b136443d62e35843d241a22203  
Starting verified code baseline: e78abd388aeee0785933feef894e53292b955e60

## Goal

Continue the Core identity and ledger work after the resource-event identity slice. Formalize the missing World-scoped owner identity before implementing more ledger endpoints or the compound transfer coordinator.

## Source facts inspected

- WorldV2Context.Create(...) has no explicit WorldInstanceId parameter/property.
- ShipLiquidInventory is constructed from capacity and balances; it has no OwnerId.
- ResourceArea composes ProducerModule, LiquidProduction, Warehouse and optional ShipLiquidInventory but has no stable instance identity.
- ProducerModule keys Producer registrations by Producer.Name; Producer has no independent ProducerInstanceId.
- Warehouse owns Jar/Liquid stock and a Jar ID generator but has no persisted OwnerId.
- ResourceLedger has ResourceIdentity, OperationId and nullable account endpoint IDs, but null does not yet distinguish external endpoints from legacy unknown endpoints.
- ResourceLedgerMutation.Name remains the compatibility/display field; DisplayName is not yet a separate contract.
- OrderTransferService still moves Jar entities and liquid staging through legacy domain methods, then publishes one mixed RecordBatchOnce batch. It has preflight and local failure cleanup, but no durable multi-account Prepare/Commit/Compensate boundary or compound receipt.

## Findings

### 1. World identity must precede account identity

Without a WorldInstanceId, identical OwnerKind/LocalOwnerId/AccountKind tuples in two independent worlds can collide if account IDs are canonicalized only from local owner data. Configuration fingerprints cannot serve as World identity because two independent worlds may intentionally share identical configuration.

### 2. Owner identity must not reuse names or roles

- Ship display names and generic labels such as ship are not owner identity.
- BoardDefinitionId is a template identity, not a live Board/Circuit instance identity.
- Master/Slave is a movable role relation, not an individual identity.
- Producer.Name is currently both registry key and human-readable name; it must be separated before it can safely be used as a ledger owner.
- Warehouse must be tied to an exact Warehouse/ProductionArea instance, not a static role label.

### 3. Null ledger endpoints are underspecified

A null endpoint can mean external stock source, production source, discard sink, or legacy unknown. Those meanings are not interchangeable. The target endpoint algebra distinguishes Account, ExternalSource, ExternalSink and LegacyUnknown. New writes must never use LegacyUnknown.

### 4. Multi-source settlement must preserve allocation

When multiple order-scoped liquid accounts return resources to Ship, the ledger must preserve the actual source account per amount. A synthetic aggregate account would falsify ownership history.

## Decisions recorded

- Added ADR 0011: World-scoped Resource Owner and Account Identity.
- Added the normative architecture detail: World-scoped Resource Owner Identity.
- Added explicit identity layers: WorldInstanceId, DefinitionId, OwnedInstanceId, OwnerId, AccountId, PortId, ResourceIdentity and DisplayName.
- AccountId v2 is scoped by WorldInstanceId + OwnerKind + LocalOwnerId + AccountKind, using versioned length-prefix encoding. AccountId v1 remains a legacy read format.
- Endpoint semantics are a tagged union: Account, ExternalSource, ExternalSink, LegacyUnknown.
- Resource identity and display attributes remain separate; Name is a compatibility field only.
- Multi-source settlement retains per-source legs.

## Files changed

- docs/decisions/0011-world-scoped-resource-owner-and-account-identity.md
- docs/architecture/world-scoped-resource-owner-identity.md
- docs/work-records/2026-11-26-world-scoped-resource-owner-identity.md
- docs/work-records/README.md

## Validation

This slice changes documentation only. It does not change production code, DTOs, checkpoint schema, resource movement, or CI gates.

- GitHub file read verified the source facts against the current integration branch.
- The preceding code baseline e78abd3 has exact successful verification: xUnit 965/0, Session Replay 33 commands, Solver Differential 4004/4004, rule/modifier differential 400/1600, configuration contract matched, security scan 6 projects / 0 vulnerable packages.
- Documentation commit f53a691 has successful verify Run 45659073251.
- New documentation commit CI must be checked separately; no result is claimed.

## Remaining work

1. Implement WorldInstanceId as an explicit, persisted, replay-stable value.
2. Introduce OwnerIdentity and AccountId v2 with v1 compatibility.
3. Add Ship, Circuit/Production Area, Warehouse and Producer owner identities.
4. Introduce tagged ledger endpoint kinds.
5. Separate DisplayName from Name and version the checkpoint/Wire contract.
6. Preserve per-source settlement allocations.
7. Implement ResourceTransferBatchRequest/Receipt and B-class multi-account Prepare/Commit/Compensate with fault injection.
8. Update Space's Core pin only after Core produces a verified integration SHA.

PR #63 remains Draft/Open. PR #2 remains unchanged. No GUI acceptance or B-class compound atomicity is claimed.


## GitHub write and verification follow-up

The documentation changes were written to the existing integration branch in separate commits:

- ADR 0011: commit 3f8510127036d6099fdf51b92764ba971e2c4436.
- Normative architecture detail: commit d0651160e497b35ad4829c360084a7325d6e3dea.
- This work record: commit 2beefbcb65be9df1ea199aa4aca70842a2a597ab.
- Work-record index: commit 2f9cf04de4cd537dacae4a57a06afb01ca8e9786.

The final index commit is the latest commit created in this documentation slice and includes the three preceding documentation commits.

Workflow verification limitation:
- The GitHub connector's fetch_commit_workflow_runs wrapper returns only pull-request-triggered runs. It returned no PR-associated run for 2f9cf04.
- The combined commit-status endpoint returned an empty status list.
- The repository workflow declares push, pull_request and workflow_dispatch triggers. Therefore an empty PR-run list is not evidence that no push run exists; this slice records CI as unconfirmed, not passed.

No production source, tests, DTOs, checkpoint schema, or runtime behavior were changed in this documentation slice.


## Code implementation — WorldInstanceId slice

The first production-code slice is now on the integration branch:

- Added `Domain.WorldInstanceId` as a host-assigned value type. It rejects blank and padded values; boundaries also reject the invalid default struct value.
- Added optional explicit `worldInstanceId` injection to `WorldV2Context.Create(...)`. It is intentionally optional only during this compatibility transition so existing call sites and old in-memory fixtures remain source-compatible; new host-created worlds must pass a stable ID. No random or configuration-derived ID is generated by Core.
- Captured the identity in `WorldV2ExecutionResourceCheckpoint` and persisted it in `WorldV2ExecutionCheckpointWireDto`.
- Restore now requires exact WorldInstanceId equality. A checkpoint with missing identity cannot silently restore into an explicitly identified world, and two identified worlds cannot cross-restore.
- Added an explicit `legacyMigrationWorldInstanceId` argument to wire DTO migration. It may supply identity only when the old payload has none; it cannot overwrite a persisted identity. The host is responsible for choosing and persisting the same migration identity for restore/replay.
- Bumped checkpoint JSON schema from 5 to 6. Schemas 3, 4 and 5 remain readable as legacy payloads with missing WorldInstanceId; schema 7 remains rejected. No identity is inferred from configuration fingerprint, display name, or definition identity.

### Code and test files

- `src/JarSystem.Core/Domain/WorldInstanceId.cs`
- `src/JarSystem.Core/World/WorldV2Context.cs`
- `src/JarSystem.Core/World/WorldV2ExecutionCheckpointWireDto.cs`
- `src/JarSystem.Core/World/WorldV2CheckpointJsonCodec.cs`
- `test/JarSystem.Tests/WorldV2/WorldV2ContextTests.cs`
- `test/JarSystem.Tests/WorldV2/WorldV2CheckpointJsonCodecTests.cs`

### Current validation boundary

- Added a test proving an explicit world ID is captured, survives checkpoint JSON encode/decode, and blocks restore into a different WorldInstanceId.
- Added schema compatibility coverage for version 5 and updated unsupported-version coverage to version 7.
- A PR-associated `verify` run was observed at commit `7579194d853279b73d89d0e2ce71adc0f1f2e3e8` (run `45661528175`); it was still `in_progress` when inspected. That run started before the subsequent schema-v6, JSON-roundtrip, and legacy-migration follow-up commits, so it does not verify the current branch head.
- The latest queried commit had no combined status entries and no PR-associated run returned by the connector. CI for the current branch head is therefore **unconfirmed**; no local build/test has been claimed.
- OwnerIdentity, AccountId v2, concrete owner injection, tagged endpoints and compound transfer atomicity remain unimplemented. Space integration and GUI acceptance remain untouched.



### CI failure diagnosis and correction

The observed run `45661528175` has now completed with **failure**, not merely in-progress. Its uploaded `verify-log` artifact was inspected. The first blocking error was:

- `WorldV2ExecutionCheckpointWireDto.cs(83,60): CS0426` — the fully-qualified `JarSystem.Core.Domain.WorldInstanceId` expression was resolved through the repository's `JarSystem` type rather than the global namespace.
- All later build/test/contract stages were consequently blocked; their missing evidence markers are cascading effects, not independent regressions.
- Corrected the reference to `global::JarSystem.Core.Domain.WorldInstanceId` in commit `f643e71ca547f05890096f61e8cb6119ba75cc20`.

Further hardening after that correction:
- Exposed the explicit migration identity through `WorldV2CheckpointJsonCodec.DecodeExecutionResourceCheckpoint(...)`, not only the lower-level DTO conversion.
- Added a schema-5 payload migration test that removes the new field and supplies a fixed migration identity.
- Corrected the JSON property escaping in that test.

The current branch head has advanced beyond the failed run. The connector has not returned a new PR-associated run or combined status for the newer commits; current-head CI remains unconfirmed. The earlier failure is retained as evidence and is not represented as passed.


Additional follow-up commits after the compiler correction:
- `df10f32d1b052e1472de7919847275c49e87eabb`: expose the migration identity at the JSON decode API.
- `0fc3609b157ee4f7326339412576b1608a8ea57b` and `e81e5f2d1de920639b8b0bae868543d930077d9d`: add and correct the schema-5 JSON migration regression.
- `2aff4665e033f399a2e77281f69e60621a150f59`: reject the invalid default value of the struct at World creation.
- These follow-up commits have not yet been covered by a returned CI run. The current head's combined status remains empty.


## Host lifecycle and replay identity implementation

The WorldInstanceId slice has now been carried through the actual Host/Application/Godot bridge lifecycle, rather than stopping at Context and DTO:

- `WorldV2Factory.Create(..., worldInstanceId)` accepts an injected identity and passes it to Context. The options/configuration object does not own WorldInstanceId; it is runtime instance identity, not definition data.
- `WorldV2HostService.Replace(options)` generates a fresh opaque ID exactly once at the Host boundary. `Replace(options, worldInstanceId)` is the deterministic path for a save/session owner that already persisted an ID.
- `WorldV2ApplicationService` exposes explicit-ID creation and `GetWorldInstanceId()`; `WorldV2BridgeSession` exposes the same operations to Godot.
- Normal checkpoint restore builds the replacement Context using the checkpoint's exact WorldInstanceId before applying any runtime state. The ID is not regenerated during restore.
- Legacy in-memory or JSON checkpoints with no WorldInstanceId are rejected by the Host restore boundary unless the caller explicitly supplies a migration identity. The identity is copied into the migrated checkpoint before restore, and conflicting migration IDs are rejected.
- JSON restore APIs pass the migration identity through both decoding and Host preparation, so a schema-3/4/5 migration is not lost between layers.
- Added Host tests for generated and explicit IDs, identity preservation across checkpoint replacement, and explicit legacy migration; added Godot bridge coverage for identity preservation across JSON restore.
- Existing test fixtures that construct deliberately modified checkpoints now retain the original WorldInstanceId, ensuring their intended validation branch is reached rather than failing early on a missing identity.

### Changed files in this lifecycle slice

- `src/JarSystem.Core/World/WorldV2Options.cs`
- `src/JarSystem.Core/World/WorldV2HostService.cs`
- `src/JarSystem.Core/World/PreparedWorldV2Restore.cs`
- `src/JarSystem.Core/World/WorldV2ApplicationService.cs`
- `hosts/godot/WorldV2Bridge/WorldV2BridgeSession.cs`
- `test/JarSystem.Tests/WorldV2/WorldV2HostServiceTests.cs`
- `test/JarSystem.Tests/WorldV2/WorldV2BridgeSessionTests.cs`

### Validation boundary

- Source and test files were re-read from the integration branch after the edits. The new test paths and call signatures are present.
- The previous compiler failure was fixed in the earlier slice; no new compile or test run is available from this environment.
- `fetch_commit_workflow_runs` and combined status for the latest lifecycle test commit returned empty results. Because the connector wrapper only returns PR-triggered runs, this is not evidence that push-triggered CI did not run. Current-head CI is **unconfirmed**.
- No local build, xUnit run, or Godot GUI run is claimed.
- The lifecycle path now supplies stable WorldInstanceId for Host-created worlds. Direct low-level `WorldV2Factory.Create(options)` remains a compatibility path with no ID; production Host creation no longer uses that path.

### Next implementation slice

Introduce OwnerIdentity as a typed, World-scoped value and AccountId v2 as a canonical, versioned encoding. Then thread stable local owner identities into Ship, Board/Production Area, Warehouse, Producer, Workshop and order staging. Do not use display names or Master/Slave roles as local owner IDs. Keep AccountId v1 as an explicit legacy read path and do not reinterpret nullable legacy endpoints until tagged endpoint semantics are implemented.
