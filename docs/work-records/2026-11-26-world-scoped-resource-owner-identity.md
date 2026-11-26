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


## Typed OwnerIdentity and AccountId v2 contract slice

After completing the Host lifecycle boundary, the next implementation slice adds the typed identity contract without yet assigning invented identities to Core objects:

- Added `ResourceOwnerKind` with stable tokens for Ship, CircuitBoard, ProductionArea, Warehouse, Producer and Workshop.
- Added `OwnerIdentity(WorldInstanceId, ResourceOwnerKind, LocalOwnerId)`. It rejects invalid World IDs, undefined owner kinds, blank/padded/control-containing local IDs and invalid UTF-8.
- Added `AccountIdentity(OwnerIdentity, AccountKind)`. Canonical output is `account:v2:` plus four UTF-8 byte-length-prefixed fields in fixed order: WorldInstanceId, OwnerKind token, LocalOwnerId, AccountKind. This is byte-counted rather than UTF-16 char-counted, so non-ASCII and embedded separators remain unambiguous.
- Added strict `AccountIdentity.ParseV2`; it rejects malformed lengths, leading-zero lengths, truncation, unknown OwnerKind tokens, invalid UTF-8, trailing bytes and non-canonical encodings.
- Extended `RuleResourceAccount` with an explicit typed-owner constructor that emits AccountId v2. Its existing two-string constructor remains the explicit v1 compatibility path; v1 strings are not silently parsed or rewritten as v2.
- Added typed-owner overloads to Ship liquid inventory, Workshop free-liquid, order staging, prefill, Jar storage, and Warehouse Jar/Liquid port adapters. Order staging derives a scoped local owner ID from the exact Workshop owner plus exact order ID; Master/Slave role and display name are not used.
- Resource port optimistic version tokens now include canonical AccountId rather than only local OwnerId, so two same-named local owners in different Worlds cannot share a version token.
- Resource transfer request and receipt validation now reject v2 endpoints from different WorldInstanceIds and reject mixed v1/v2 endpoints. Legacy-to-legacy transfers remain compatible.
- Added identity tests for same-tuple determinism, cross-World separation, owner/account-kind separation, Unicode byte-length encoding, parser strictness, v1/v2 separation, adapter construction, per-order staging separation, and cross-World transfer rejection.

### Changed files

- `src/JarSystem.Core/Domain/ResourceAccountIdentity.cs`
- `src/JarSystem.Core/Domain/RuleTransitionContract.cs`
- `src/JarSystem.Core/Domain/ResourcePortTransferContracts.cs`
- `src/JarSystem.Core/Domain/ResourcePortAdapters.cs`
- `test/JarSystem.Tests/ResourceAccountIdentityTests.cs`
- `docs/architecture/world-scoped-resource-owner-identity.md`
- `docs/decisions/0011-world-scoped-resource-owner-and-account-identity.md`

### Explicitly not completed

- Existing legacy adapter constructors still emit account:v1; production account creation sites have not all been migrated.
- Ship/Board/ProductionArea/Warehouse/Producer/Workshop stable local owner IDs have not yet been injected from the authoritative host/owned-instance boundary.
- Owner identity mapping is not yet part of the checkpoint wire payload.
- Resource ledger snapshots do not yet validate every v2 account endpoint against the restored WorldInstanceId.
- Tagged ExternalSource/ExternalSink/LegacyUnknown endpoint algebra, DisplayName migration, per-source settlement and compound B-class atomicity remain later stages.

### Validation boundary

- GitHub branch reads confirmed the new type, parser, adapter overloads, transfer guards and tests are present.
- No local .NET build/test execution is available in this environment.
- The latest queried commit status/workflow endpoints returned no PR-associated run. The connector's workflow-run wrapper filters to PR-triggered runs, so this is not proof that no push workflow ran. Current-head CI remains **unconfirmed**.


### Follow-up hardening after the first OwnerIdentity commit

- Added strict AccountId v2 parsing and round-trip validation. The parser requires exactly four canonical UTF-8 length-prefixed fields and rejects malformed lengths, leading zeros, truncation, invalid UTF-8, unknown owner-kind tokens, trailing data and v1 input.
- Transfer request and receipt validation now call the World-scope guard directly; same-World v2 transfers are accepted, cross-World v2 transfers and mixed v1/v2 transfers are rejected. Legacy v1-to-v1 remains a deliberate compatibility path.
- Workshop typed adapter overloads now require the supplied OwnerIdentity.LocalOwnerId to exactly match the live Workshop.OwnedInstanceId; a mismatched identity is rejected.
- Adapter optimistic version tokens use full canonical AccountId rather than LocalOwnerId, avoiding token collisions between same-local-ID owners in separate Worlds.
- Added regression coverage for parser round-trip/rejection, cross-World and mixed-version transfer rejection, exact Workshop owner binding and distinct order-scoped staging identities.

Latest lifecycle/identity implementation commits include:
- Host lifecycle and restore migration: `18e094f0f13679f058c34747e966365b2d46032f`, `4288f204d8956cc2ff4558a8a69489d827539aa7`, `4a6a9b762350a3390998138756beb93c0977449c`, `8f6194bbf1bfb0414b6368f1e17bd1d649d540f4`.
- Typed OwnerIdentity and AccountId v2: `3902e1b021d4b101763b152b9536c1067394bc2b`, `86e445d485ce08aedddda616cdae3488ed2a0bf7`, `f0eb02fb423aa70c25977f17e9557da8a4c9a0f0`, `d401b2d349eefb8df5fbe3d0dfb13b1b104e26d4`.
- Transfer isolation and adapter hardening: `227f50f5042ae1f7f38ecd4ec32f5d29f65f21f8`, `7065d67b7b44ac17f894fc6d63386442ccf5e6ed`, `f88ca26fcc1bb887394f55149b20f366f7150925`, `bfbea930143c44b12191e4ba04b24f4937595a88`.

The integration branch compares as 61 commits ahead and 0 behind the previously verified code baseline `e78abd388aeee0785933feef894e53292b955e60`. The latest queried commit has no returned combined status or PR-associated workflow run; CI remains unconfirmed. No local .NET build or xUnit execution is claimed.


## Persisted WorldV2OwnerIdentityMap and concrete runtime binding

The identity implementation has advanced from adapter overloads to the actual World V2 object graph:

- Added immutable `WorldV2OwnerIdentityMap`, containing exact local IDs for Ship, CircuitBoard, ProductionArea, Warehouse and exact-ID dictionaries for Producer and Workshop.
- Added `WorldV2OwnerIdentityMapWireDto`; the map is captured in the root execution checkpoint. Checkpoint JSON schema is now **7**; schemas 3–6 remain accepted as legacy formats without the owner map.
- Host, ApplicationService and Godot Bridge accept an explicit owner map together with WorldInstanceId. The map is restored from checkpoint on load; no new owner IDs are generated during restore.
- Factory binds Ship owner to `ShipLiquidInventory`, ProductionArea owner to `ResourceArea`, Warehouse owner to `Warehouse`, Producer owners to `Producer`, and Workshop owners to exact `Workshop.OwnedInstanceId`. CircuitBoard runtime identity is retained in the map for the host/Space board boundary (Core has no board object instance to attach it to).
- `WorldV2ProducerOptions.ProducerInstanceId` and `WorldV2WorkshopOptions.OwnedInstanceId` were added as explicit runtime identity references. If an owner map is supplied, every live Producer and Workshop must have one and the map must match the exact runtime ID set. Duplicate ProducerInstanceId and Workshop OwnedInstanceId are rejected both at configuration and runtime collection boundaries.
- If both board assignment and workshop options provide OwnedInstanceId, they must match exactly. No name, role, list index or definition ID is used to invent identity.
- `WorldV2ConfigurationFingerprint` now excludes `ProducerInstanceId` and `OwnedInstanceId`; the identity map is persisted separately. This keeps runtime identity separate from immutable World definition/configuration.
- `WorldV2Context.RestoreExecutionResourceCheckpoint` compares the entire owner map before applying state and validates every account:v2 ledger endpoint against the current WorldInstanceId. account:v1 remains legacy/unknown scope and is not assigned a guessed World.
- Added tests for owner-map checkpoint round-trip, explicit Host restore, definition fingerprint independence from local runtime IDs, duplicate runtime IDs, and v2 account endpoint World validation.

### Additional changed files

- `src/JarSystem.Core/World/WorldV2OwnerIdentityMap.cs`
- `src/JarSystem.Core/World/WorldV2OwnerIdentityMapWireDto.cs`
- `src/JarSystem.Core/World/WorldV2ExecutionCheckpointWireDto.cs`
- `src/JarSystem.Core/World/WorldV2CheckpointJsonCodec.cs`
- `src/JarSystem.Core/World/WorldV2ConfigurationFingerprint.cs`
- `src/JarSystem.Core/World/WorldV2Options.cs`
- `src/JarSystem.Core/World/WorldV2Context.cs`
- `src/JarSystem.Core/World/WorkshopRoster.cs`
- `src/JarSystem.Core/Production/ProducerModule.cs`
- `src/JarSystem.Core/Production/ResourceArea.cs`
- `src/JarSystem.Core/Production/Warehouse.cs`
- `src/JarSystem.Core/Domain/LiquidResourceAccounts.cs`
- `src/JarSystem.Core/Domain/Workshop.cs`
- `src/JarSystem.Core/Domain/ResourcePortAdapters.cs`
- `test/JarSystem.Tests/ResourceAccountIdentityTests.cs`
- `test/JarSystem.Tests/WorldV2/WorldV2HostServiceTests.cs`
- `test/JarSystem.Tests/WorldV2/WorldV2ContextTests.cs`
- `test/JarSystem.Tests/WorldV2/WorldV2CheckpointJsonCodecTests.cs`

### Remaining identity boundary

- Core's WorldV2 owner map is now explicit and checkpointed, but Space still must provide the real player Ship OwnedInstanceId and Board runtime instance ID when constructing the map.
- Existing two-string RuleResourceAccount constructors and legacy ResourcePortAdapter constructors still emit account:v1. Only typed-owner overloads emit v2; all production ledger write sites must be migrated deliberately.
- The Producer model now has ProducerInstanceId, but producer-facing presentation/status DTOs and every checkpoint/config contract consumer still need an explicit identity review.
- Account:v2 endpoints are checked against WorldInstanceId on checkpoint restore; a full mapping check from every ledger account to the permitted owner map (including order-staging derived identities) remains to be implemented.
- Tagged Account/ExternalSource/ExternalSink/LegacyUnknown endpoints, DisplayName migration, per-source settlement and compound B-class atomicity remain later stages.

Latest code slice was written through commit `62696c1172a2b15790d521ca365565f37c061805`; documentation updates follow in this work-record commit. No local build/test is claimed. The workflow connector returned no current-head PR run/status; CI remains unconfirmed.


### Runtime binding and checkpoint schema follow-up

Further implementation after the initial owner-map write:
- The map is now attached to actual `ShipLiquidInventory`, `ResourceArea`, `Warehouse`, `Producer` and `Workshop` objects. Core verifies these attached identities against the map at Context assembly.
- Producer options now carry optional `ProducerInstanceId`; Workshop options carry optional `OwnedInstanceId`. With an owner map, both are mandatory for every live instance and must exactly match the map. Board assignment OwnedInstanceId and Workshop option OwnedInstanceId may coexist only when identical.
- Runtime ProducerModule and WorkshopRoster reject duplicate instance IDs, not just duplicate display names.
- Warehouse port adapter v2 overloads use the exact Warehouse owner identity. Workshop staging adapters remain scoped to the exact Workshop owner plus order ID.
- OwnerIdentityMap is part of checkpoint wire schema 7. Schema 6 remains readable without the map; schema 3–5 remain readable without WorldInstanceId and map. Restore rebuilds the same runtime owner map from the checkpoint and validates object bindings.
- World definition fingerprint excludes runtime ProducerInstanceId and OwnedInstanceId. The owner map is stored separately, preventing owner identity changes from masquerading as definition changes.
- Checkpoint restore validates v2 ledger account endpoints against the active WorldInstanceId. The typed account transfer contracts separately reject cross-World and mixed-version transfer endpoints.

Latest implementation commit before this work-record append: `e0ce362a4dd238caaa6410f0c41d4b0bd9d51877`. Branch comparison against baseline `e78abd388aeee0785933feef894e53292b955e60` is 106 commits ahead, 0 behind. Latest queried workflow/status endpoints returned empty; current-head CI remains unconfirmed. No local build/test is claimed.

## Reconciliation and CI follow-up — 2026-11-26

This append reconciles this work record with the later implementation status recorded in ADR 0011 and the normative architecture document. Earlier sections above describe intermediate slices and must not be read as the current schema or endpoint status.

### Current implementation status confirmed from the branch

- Checkpoint JSON schema is now 9. Schema 3–8 remain readable through explicit compatibility rules.
- Tagged ResourceEndpoint kinds (Account, ExternalSource, ExternalSink, LegacyUnknown) are present in ResourceEvent, ResourceLedgerMutation, snapshots and checkpoint wire.
- World V2 initial stock, aggregate Jar production, Warehouse flow, prefill, rollback and retired-prefill paths now emit explicit endpoints when the runtime owner identity is available.
- B-class Workshop handoff emits scoped account endpoints when exact Workshop OwnerIdentity is available. Settlement retains per-source Requested/Accepted/Discarded allocations and writes separate ledger legs; it does not create a synthetic aggregate owner.
- ResourceEvent DisplayName is present in schema-9 wire and validated for schema 9+. Full migration of all legacy producers is not complete.
- Traditional ProductionService still contains an intentional LegacyUnknown migration boundary because its host assembly does not yet supply stable World/Owner identity.
- Durable cross-account physical transaction coordination remains unimplemented. Do not treat ledger batch atomicity as proof of physical multi-account atomicity.

### CI failure and correction

- PR #63 verification Run 45662558305 checked merge test tree ffefb13623e379a848f946c5638488426b4167b4 (PR head 60fd372a0817fa3b9ab4285dfa6add9fe0ba5580 merged into base 40e578e6e32472549d647ed903ef98cc98028bb6).
- The first blocking failure was CS0246 at WorldV2CheckpointJsonCodec.cs line 46: WorldInstanceId could not be resolved. Downstream builds, tests, contract snapshots, solver and replay were blocked by this compile failure; they are not independent test failures. Security scan completed with 6 projects and 0 vulnerable packages.
- Corrected the DecodeExecutionResourceCheckpoint migration parameter to use the explicit global type name global::JarSystem.Core.Domain.WorldInstanceId.
- Fix commit: 8b5f0cbbe5531a63aef3721ccd82c75aafcfaede.
- The changed file was re-read from the integration branch and the exact diff was confirmed. The GitHub connector did not return a PR-associated workflow run or combined status for this commit. Current-head CI remains unconfirmed; no local build or test is claimed.

### Next implementation sequence

1. Obtain a fresh verification result for the corrected Core head; inspect the first actual failing stage if any rather than treating cascading gates as separate defects.
2. Audit every remaining LegacyUnknown-producing write path, prioritizing traditional ProductionService and its host identity injection. Do not fabricate owner IDs from names, labels, roles, list positions or object references.
3. Complete DisplayName migration at all production event producers while preserving legacy Name read compatibility and historical operation fingerprints.
4. Add explicit tests that new writes cannot emit LegacyUnknown, while legacy checkpoint reads preserve unknown provenance without inference.
5. Only after the endpoint and identity migration is complete, implement durable B-class compound Prepare/Commit/Compensate across physical accounts with fault injection and a compound idempotency receipt.
6. Keep Space's Core pin unchanged until a verified Core integration SHA exists; then perform the cross-repository build and the previously outstanding GUI acceptance.

No PR was merged, no Space code was changed, and no GUI acceptance is claimed in this follow-up.

## CI root-cause correction — stale ResourceEndpoint test property (2026-11-26)

- The verification run for checkpoint codec fix 8b5f0cbbe5531a63aef3721ccd82c75aafcfaede completed and failed at the first Release build gate.
- The only compiler errors were in test/JarSystem.Tests/ResourceProvenanceTests.cs lines 102 and 134: the tests referenced ResourceEndpoint.ExternalEndpointId, but the current tagged endpoint contract exposes ExternalIdentity.
- Core, Web, WorldV2Bridge, and test executable projects compiled before the test-project compile errors were reported. Both legacy Godot host builds passed. The remaining xUnit summary failure was downstream of the test-project build failure.
- Contract snapshot, schema, World config match, protocol guard, solver differential (4004/4004), modifier differential (400/1600), session replay (33 commands), dependency scan (6 projects / 0 vulnerable packages), and secret scan (623 files) completed successfully in the same run.
- Corrected exactly the two stale test property references; no production behavior or endpoint schema was changed.
- Fix commit: 0202ec59341b538709114c6b70b9b9940d7cbd4d.
- Re-read the updated test file: zero ExternalEndpointId references remain and both assertions now use ExternalIdentity.
- The connector has not yet returned a PR-triggered workflow run for this fix commit. Its verification result is unconfirmed; do not treat the previous failed run as passing.
- The remaining LegacyUnknown boundary is still the legacy ProductionService path: its old JarSystem host constructs ProductionService(this) without WorldInstanceId/OwnerIdentity injection. This is a real host-boundary gap, not a missing property fix. The legacy initial-stock source and discard sink are already explicit external endpoints; their destination/source account ownership remains unknown until the legacy host supplies stable identity.
## Legacy production ledger endpoint migration — 2026-11-26

- Migrated the legacy ProductionService ledger writes away from generic Unknown endpoints wherever the source/sink is known.
- Production Jar and liquid output now use stable ExternalSource identities (`world.production.jar-output` / `world.production.liquid-output`). The receiving endpoint uses the exact Warehouse `OwnerIdentity` account when one is injected.
- Warehouse-to-master flow now resolves Warehouse and Workshop Account endpoints from their exact runtime OwnerIdentity values when available. Jar and liquid account kinds remain distinct (`production.warehouse.jar-storage`, `production.warehouse.liquid-storage`, `workshop.jar-storage`, `workshop.free-liquid-staging`).
- Warehouse overflow discard now uses the Warehouse Account as source when available and stable ExternalSink identities (`warehouse.jar-capacity-overflow`, `warehouse.liquid-capacity-overflow`).
- Initial stock continues to use `level.initial-stock` as ExternalSource and now resolves its target to the typed Warehouse liquid-storage Account when Warehouse OwnerIdentity is present.
- Compatibility behavior is deliberately fail-closed: when the legacy JarSystem host has not supplied Warehouse/Workshop OwnerIdentity, only the missing internal account endpoint remains LegacyUnknown. No display name, object reference, list index, or role is used to guess ownership.
- Focused regression assertions now pin the production source tag, explicit legacy target boundary, and stable overflow sink identity.
- Code commits: 7cb83362aa98ba1316c0b5f91024e164e104811e (ProductionService endpoint mapping), b46fa2ddde7e63f90bcae03f4efb83c23fda4230 (initial stock target), 572ada92c04579daad6778471bc456b5ec637135 and 9994fe48d6cd178fdfbb97109285a8219cc674d8 (focused tests).
- The connector exposed verification run 45682603289 for the first code commit, still in progress at inspection time. Later commits did not yet have a returned workflow run; this batch is not marked verified.
- Remaining blocker: the legacy JarSystem composition root does not yet accept a persisted WorldInstanceId plus an exact owner identity map, and legacy WorldOptions does not yet carry ProducerInstanceId / Workshop OwnedInstanceId. The next change must add those explicit host inputs rather than synthesize identities from names or positions.
- Follow-up test commit `9994fe48d6cd178fdfbb97109285a8219cc674d8` adds assertions for the typed production output source, the explicit legacy Warehouse target fallback, and the stable overflow sink. The associated verification run `45682603289` remained in progress at the latest inspection; no pass is claimed.

## Legacy JarSystem composition-root identity injection — 2026-11-26

- Corrected the previous gap assessment: World V2 already has ProducerInstanceId, OwnedInstanceId, WorldV2OwnerIdentityMap, host injection, and checkpoint wire round-trip coverage. The remaining gap was specifically the legacy WorldOptions -> JarSystem -> SystemBootstrapper composition path.
- Added optional `producerInstanceId` and `ownedInstanceId` to legacy ProducerOptions / WorkshopOptions. Null values are omitted from JSON so existing world.json payloads and approval snapshots remain byte-compatible. WorldOptions.Clone now preserves supplied IDs; validation rejects malformed or duplicate IDs.
- SystemBootstrapper now accepts exact Producer and Workshop owner dictionaries and injects each identity alongside its stable local instance ID. It fails closed if a supplied identity map is incomplete; it never uses display names or list positions.
- JarSystem now accepts optional explicit WorldInstanceId and WorldV2OwnerIdentityMap at the end of its constructor, preserving existing call sites. It validates world scope and exact set equality between configured ProducerInstanceId / OwnedInstanceId and the map. ProducerModule receives the exact Warehouse owner; Bootstrapper receives exact Producer/Workshop owners.
- Reset and initialization reuse the same immutable map held by the JarSystem instance, so owner/account IDs do not change during a reset of the same world.
- Added ResourceProvenanceTests coverage for legacy JarSystem production, initial stock and warehouse-to-workshop flow endpoints, plus rejection of incomplete identity maps. Existing no-map legacy construction remains available as an explicit compatibility path and may retain LegacyUnknown where ownership is unavailable.
- Code commits: 6467af20923bcfee6f2c12e40a3fd79a4036df44 (legacy option IDs and clone/validation), da082164adaf2ea1355986acaf49705dcc99e453 (Bootstrapper injection), c06f1c719bb96efc90c4c305e89adcfc2e0102b5 (JarSystem composition root), 0e11192c9c8247a653eaaa56c101f06999a6149f (exact-set validation), d1222ee9c8f6ac8b9b3a8139f2a128d6d388ca6b (explicit System namespace), 557a397a45831cfc61215088bb194dd4c6266a21 (regression tests).
- At inspection, verification run 45683518774 for the test commit was in progress. The log endpoint was not yet available while the job was running; no pass is claimed.
- Remaining work: add/verify JSON round-trip coverage for optional legacy instance IDs; inspect the complete LegacyUnknown write inventory and ensure only the documented no-map compatibility path can write unknown endpoints. Then consolidate any remaining call sites and move to cross-account physical transaction coordination.
## Identity injection hardening and CI analyzer correction — 2026-11-26

- Follow-up source inspection found one additional lifecycle hole: `JarSystem.ResetProducerModuleCore()` rebuilt `ProducerModule` without forwarding the configured Warehouse OwnerIdentity. This would have silently reintroduced LegacyUnknown warehouse endpoints after Reset / ResetProducerModule even when the world had an explicit identity map.
- Fixed the reset composition path to pass `OwnerIdentities?.Warehouse`; the full `Reset()` then reuses the same immutable OwnerIdentity map when Bootstrapper reconstructs Producers and Workshops.
- Added regression assertions that ResetProducerModule retains the Warehouse identity and full Reset reconstructs the same World, Warehouse, Producer and Workshop identities before resource production/flow resumes.
- Added JSON round-trip coverage for optional legacy ProducerInstanceId / OwnedInstanceId fields and confirmed legacy payloads omit absent identity properties.
- Audited key resource-producing/transfer files. Remaining LegacyUnknown branches are compatibility-only: ProductionService / JarSystem fallback when the legacy host did not supply an owner map; WorldV2Context when a legacy World has no Ship owner; workshop switch/rollback/order transfer helpers when legacy Workshop has no OwnedInstanceId; and checkpoint wire conversion for historical schemas with absent endpoint fields. With exact owner map injected, the legacy JarSystem production, stock and warehouse-to-workshop flow endpoints are Account v2 and no longer Unknown.
- Verification run 45683431411 completed successfully for the code/validation stage before the new focused identity tests were added: build passed, xUnit summary reported 1000 total / 0 errors / 0 failed, solver differential 4004/4004, modifier differential 400/1600, replay 33 commands, dependency scan 6 projects / 0 vulnerable packages, secret scan 623 files, and VERDICT=OK.
- The next run 45683518774 failed only at Release compilation due to xUnit2031 analyzer rules in the two new filtered Assert.Single calls (Where(...).Assert.Single). Production projects and both Godot host builds compiled; the separate test execution step itself reported exit 0, but the release build gate correctly failed.
- Replaced both Where + Assert.Single expressions with the xUnit predicate overload in commit fc3f8e2a26a29c84c13380163760646275f0ccfb. Verification for this final analyzer correction has not yet been returned; do not mark the latest batch fully verified.
- Additional code commits: 7dfdf9c34b6252fa332ebd86447839001692e542 (reset Warehouse identity), 84f7feeaf29b0d5153e6b8bd920e342b5180b5eb (reset identity tests), 1c4cd2753f04418ea6e3cb284b94b42ec74ec525 / c1cde4f67706e7b061626584594c388e8c2446ba (legacy JSON round-trip test and xUnit-compatible assertion).
## Default World V2 Host identity assignment and durable config pairing — 2026-11-26

- Closed the normal-host creation gap: `WorldV2HostService.Replace(options)` and `Replace(options, worldInstanceId)` now assign missing opaque ProducerInstanceId / OwnedInstanceId values at the host boundary and create a complete `WorldV2OwnerIdentityMap` for Ship, CircuitBoard, ProductionArea, Warehouse, Producers and Workshops.
- Existing explicit ProducerInstanceId values are preserved. Workshop OwnedInstanceId is first taken from the exact BoardAssignmentSnapshot entry matching its BoardSlotId; only an unassigned Workshop receives a fresh opaque GUID. No display name, collection position, or role is used as identity.
- The host validates the world definition before assigning IDs, so invalid definitions are rejected before the caller's options object is mutated. Direct Host callers receive the normalized IDs on their options object; ApplicationService clones caller input first and retains the normalized snapshot internally.
- `WorldV2ApplicationService.CaptureWorldOptions()` and `WorldV2BridgeSession.CaptureWorldOptions()` expose the normalized, persistable config containing host-assigned IDs. It must be persisted alongside checkpoints for cross-process reconstruction.
- ApplicationService now retains a bounded set (8) of recent normalized config snapshots keyed by WorldInstanceId. Restoring an earlier checkpoint after replacing the current world now selects the exact config snapshot belonging to that checkpoint rather than reusing the most recent world's different owner IDs.
- Explicit JSON restore tests now use the persisted identity-bound config snapshot. Tests also assert normal Host creation populates the owner map and injects the exact Producer, Workshop and Warehouse identities.
- The first Host auto-injection CI run exposed six expected test call-site assumptions: tests attempted to restore a checkpoint using fresh definition-only Options() without the generated instance IDs. Those call sites have been corrected to use persisted options or the per-world snapshot. This is not a production compile failure.
- Relevant commits: 38f463d5f32e3696ef6e7dcf32ea3d145955d221 (Host identity assignment), 3eb24ea6bf7ad0798c801b5c57d0bf44b77b9268 (ApplicationService snapshot preservation), 75005ed4177fdd760da3d8d069bb4aae99fc99da (host lifecycle tests), d7887b6c64c199e0cf3c25890243aa6274ac0b68 (validate before identity assignment), b8a8bab07f74389b28d180dd4bd975d38565560f and 62ea29c2775676608767ae0191e3e715ecb44c0e (persisted options tests), dfc977f560ec19bef7fea8b3438269470bed6837 (JSON restore tests), 97458282b2a0d8e3cde355a89fce6d0db2425bf4 (bounded per-world options snapshots), 602996f8035a0c5d64d921f9375f6af106c3830d (Godot bridge capture API), 629462e7cbd9973669f05184ae5cf70b11c3140d (bridge JSON restore test).
- CI for the latest Host/ApplicationService/Bridge batch is still running or awaiting a new run at the time of this note; no pass is claimed for this batch.
## Host identity batch verification and next transaction boundary — 2026-11-26

- Verification run 45686630445 completed successfully after the Bridge JSON restore and legacy migration fixture corrections: Release build succeeded; JarSystem.Tests total 1005, errors 0, failed 0, skipped 0; solver differential 4004/4004; modifier differential 400 scenarios / 1600 checks; replay 33 commands; dependency scan 6 projects / 0 vulnerable packages; secret scan 623 files; QUALITY-OK and VERDICT=OK.
- The two final test corrections were: Bridge producer-progress JSON restore now uses `CaptureWorldOptions()` so the exact host-assigned IDs accompany the checkpoint; the legacy migration test now captures a genuinely pre-identity `WorldV2Factory.Create(options)` context instead of stripping identity metadata from a host-created checkpoint.
- The latest passing run includes host-assigned World V2 identity injection, per-World config snapshot retention, persisted option capture through the Godot bridge, and the legacy restore test fixture correction.
- Next transaction audit: B-class `OrderTransferService` performs a multi-Jar handoff followed by a liquid handoff, verifies final ownership, then publishes one ledger batch. The current exception boundary is not a single whole-batch physical snapshot/compensation boundary: each Jar transfer has local recovery, while session cleanup/SlaveRollback is a later multi-step operation. By contrast, `OrderResourceSettlementService` already snapshots master/slave order staging plus ShipLiquidInventory and restores them when failure occurs before its settlement receipt is committed. The shared ResourcePortTransferCommitCoordinator covers individual port transfers, not this full B-class handoff.
- Next implementation priority is therefore a multi-account physical transaction boundary for B-class handoff and rollback: capture exact Workshop runtime states and active order staging states before CommitStarted; compensate every participant on any pre-publication failure; publish the ledger batch only after all physical postconditions and receipt checks; if compensation itself fails, preserve the faulted world and prohibit further simulation. Keep the existing distinction between ledger batch atomicity and physical-account atomicity.
## B-class multi-account handoff transaction — implementation in progress — 2026-11-26

- Added `WorkshopLiquidStaging.RestoreFrom(snapshot)`: validates by rebuilding a separate staging instance, then restores the exact order-account dictionary, prefill balances and capacity reservations in place. Existing `OrderLiquidScope` references remain attached to the authoritative staging object rather than a replacement instance.
- Added `WorldOrderExecutionAdmission.CaptureTransferSnapshot()` / `RestoreTransferSnapshot(...)`. The scoped-liquid path captures Master/Slave staging snapshots and active order scope/lease membership; compensation validates both snapshots, exact scope-to-staging references, OwnerIdentity/OwnedInstanceId, reservations and absence of foreign order accounts before restoring either side. The legacy no-Ship/no-staging path returns a null snapshot and remains supported.
- Added narrow `OrderResourceLease.RestoreTransferPendingAfterCompensation()`, allowing only B-class leases in TransferCommitted / SlaveExecuting to return to the pre-handoff TransferPending boundary.
- Wrapped the complete B-class handoff in `OrderTransferService`: capture Master/Slave `WorkshopSnapshot` (including original Jar references) and admission staging snapshot before commit start; move the complete Jar batch; move legacy free liquid or scoped order liquid; validate exact ownership and RuleTransition postconditions; advance lease stages; then publish one `ResourceLedger.RecordBatchOnce` batch as the final durable write.
- On any exception before/at final ledger publication, the service restores both original Workshop objects, both original staging objects in place, lease stage and the session's `_slaveTransferCommitted` flag. If compensation fails, it throws an AggregateException; `ResourceWorldRuntime` latches the fault and blocks subsequent world advancement. A successful compensation returns an ordinary B-order failure instead of leaving the runtime in an uncertain state.
- Moved lease `MarkTransferCommitted` and `MarkSlaveExecuting` from the Session into the handoff transaction. The Session can enter SlaveSteps only after the full physical/lease/ledger transaction returns success.
- Added fault-injection points after Jar moves, after liquid transfer, before ledger publication and before compensation. Tests cover prior partial Jar move failures, liquid staging restoration and correct original Master order-account provenance on subsequent Ship refund, ledger-publication failure, and compensation failure that must freeze the world while retaining the active lease/session and the uncertain physical state.
- Commits for transaction implementation: 9a365cd1da459d330974c9466502c961627518f3 (in-place staging restore), cf9f6a4fdf30c3cc2f39371019db4cb238816d46 (admission transfer snapshots and compensation), 5189c2ef3763edf4a40782ae59e7b05b93124c98 (lease stage compensation), 9e15e0d49c4ec13c40989312d6d87823000e5a08 (whole B-class handoff transaction), cdd178c56a0bcb6281c4b319f0c505707481f780 (Session lease commit integration), ada3927bc8e0b7e64e9446b9aa54320703a34ef1 / eeaa15fdf267318cfdc1d5438c84bbf25ef5f601 (legacy admission compatibility), 4f8c035b17b95e29e6ffa768824768009c7475a9 (lease transition before final ledger publish), cbc32b69a2e27b1e1076289f43509b9f282a479f / 8326fda7b601e24455d0e90b5a905d01fe503ba2 (handoff fault tests), 3c33e046cd7281a141b84c3bc2a648d7f8ea6b77 / ae31f4f05fe9ff4085b28aebf5ea5a35fcf7e3e0 (compensation-failure injection and Runtime freeze test), c412665a60fa863d83712c682d53c785df093836 and d120a7d3c32716f863ca7af4beca4f9ff4acc5ba (xUnit assertion compatibility and final settlement source-provenance assertions).
- CI triage: run 45754704349 exposed a stale call-site mismatch while the session signature was being updated; run 45754737774 exposed the pre-compatibility snapshot throw for legacy admissions plus two existing tests that still expected exceptions after successful compensation. The admission helper and test semantics were updated. Run 45754841209 exposed an xUnit overload mismatch in a new assertion; fixed in c412665. Run 45754953048 exposed the test fault adapter missing the newly added `BeforeCompensation()` member; fixed in ae31f4f. Run 45755202679 for commit c412665a is in progress at the time of this note; the additional provenance assertion change d120a7d has not yet been verified. Do not mark this transaction batch green until a run containing the latest branch head passes.
- Final verification must establish: (1) Release build and all xUnit tests pass; (2) B handoff failures after Jar, after liquid, and immediately before ledger publication restore both Workshops and all order staging/reservation state; (3) Transfer ledger batch remains absent on every compensated failure; (4) only compensation failure latches Runtime fault and prevents further clock/resource mutation; (5) a successful B handoff still records one ledger batch and restored SlaveSteps still records Return from the correct account.
- Follow-up root-cause correction after run 45755202679: `WorldOrderExecutionSession.AdvanceLogicalTime()` previously caught every exception from `AdvanceOneUnit()` and called `TerminateFailure()` before rethrowing. That would incorrectly release the active lease and remove the session after a B-class compensation failure, even though the physical ownership state was uncertain.
- Introduced `OrderTransferCompensationException` (still an `AggregateException` for callers) and a dedicated catch in `AdvanceLogicalTime()` that deliberately rethrows without ordinary cleanup. `ResourceWorldRuntime` now receives the marker exception, latches `IsFaulted`, and retains the active session / reservation / exact in-memory physical state for reconciliation; subsequent world advancement is rejected before the clock changes.
- The same failing run also confirmed the scoped-liquid compensation test must inspect the state after the normal failed-order settlement: both order staging scopes are drained, Ship inventory is refunded, and the Return event's source must still identify the original Master order-staging account. The corresponding assertion was corrected in commit d120a7d3c32716f863ca7af4beca4f9ff4acc5ba.
- Root-cause commits: 8acde6cc42031fed35fad1a674ca52f3d4ff84c8 (dedicated compensation failure marker) and 0da211cd43adc79b268f91e80c342bb05c557f28 (preserve Session/lease on that marker). Latest PR verification run 45755611943 is in progress; no green result is claimed yet.
## B-class SlaveRollback transaction — second direction covered — 2026-11-26

- Replaced the old per-resource `SlaveRollback` writes with one snapshot-compensated operation. It validates exact Jar IDs, captures both Workshop snapshots before mutation, returns every remaining Slave Jar and free-liquid balance, collects Return/Discard facts and presentation logs in memory, verifies the final Master/Slave physical state, then publishes the whole ledger projection with one `RecordBatchOnce(OperationId, tick, mutations)` call.
- Pre-publication failure restores both original Workshop snapshots (including original Jar entity references, Jar liquid/marks, free-liquid balances, capability and capacity). No ledger event, operation receipt, discard counter, or rollback log is emitted when compensation succeeds.
- Rollback operation IDs are stable and explicit: legacy OrderEvent uses `legacy-order:{Order.Id}:slave-rollback`; World V2 uses `{Manifest.WorldVersion}:order:{DemandId}:rollback`. When the same operation is retried after Slave is empty, the ledger receipt makes the call a no-op. If the operation is already recorded while Slave still owns resources, the system refuses to guess or repeat it.
- Log/stat callbacks run only after physical accounts and the ledger batch are committed. Failure in that post-commit projection phase raises `SlaveRollbackTransactionException` so the owner freezes the runtime and preserves the already-committed state rather than trying to reverse a durable ledger batch.
- Added `ISlaveRollbackFaultPort` boundaries after Jar movement, after liquid movement, before ledger publication, before compensation and after ledger publication. The World V2 session accepts an optional fault adapter, allowing the exact cleanup path to be tested without modifying game behavior.
- World V2 Session and `TerminateFailure` now preserve active session/lease and rethrow `SlaveRollbackTransactionException` just as they do for forward B-transfer compensation failures. `InterruptForWorkshopReplacement` also preserves the exception instead of wrapping it as an ordinary interruption error.
- Legacy `OrderEvent.Fail` now commits SlaveRollback before clearing Master/Slave marks. Thus a failed rollback does not snapshot already-cleared marks as its compensation baseline.
- Added `DeliveryRollbackTests` for: no partial mutations or ledger write after a post-liquid fault; one batch containing both Jar/Liquid Return and Discard facts; exact Account v2 source/target identities; and idempotent replay with unchanged LastSeq, logs and discard counters.
- Added a World V2 runtime test that fails the second Slave POUR_IN, injects a failure during SlaveRollback compensation, and asserts the Runtime is faulted/frozen, Session and lease remain registered, the lease remains SlaveExecuting, and the ledger has no false Return/Discard batch.
- Commits: 5c804c9ff3c2f94862bfd7d9e0ec0007993c52ed (transactional SlaveRollback), 57f331c2a0b704d75570cb75381539f2d9e4ae1c (legacy OperationId), eb5e86e20eb08c072414b1d360f35ddcc607c57c (Session preservation), 0882ad83b5b5c54701f75792d4fde0deed755567 (World V2 OperationId), ca83273145b16cd90e66c91438ec2634e87622ba (Session rollback fault injection), ef751977aa84e64ce6792b8f29c650353716868c (atomicity/idempotency tests), 3586c0f3d4db81ba4e62f65a7452847968f8763d (Runtime freeze test), de77714d9c23be50cf4f82c824c817be55a81a81 (legacy failure mark ordering), 247b65688483532cc9a88bd1d1e6dffd7e9b7b5b (xUnit2029 analyzer fix).
- CI run 45756428157 for the first SlaveRollback transaction/test commit failed at Release compilation only due to xUnit2029 on `Assert.Empty(dictionary.Where(value > 0))`; fixed with `Assert.DoesNotContain(collection, predicate)` in 247b656. The latest PR-head run 45756637428 is in progress; do not mark this rollback batch green until that run and a run containing the newest runtime fault test both pass.
- Latest follow-up CI run 45756637428 reached xUnit and reported one remaining old call-site failure: `ResourceProvenanceTests.SlaveRollbackRecordsWhatCameBackAndWhatWasLost` passed a ResourceLedger but no OperationId. Updated that call to supply the deterministic test operation ID `test:legacy-slave-rollback:9102-9103` in commit 37e16c03c90a8bdcf2cf98f66706e4dda20900fb. The same run's Release build passed and xUnit total was 1011 with exactly this one failure; this follow-up commit still needs its own complete run.
- xUnit2029 analyzer in `DeliveryRollbackTests` was fixed in commit 247b65688483532cc9a88bd1d1e6dffd7e9b7b5b (`Assert.DoesNotContain(collection, predicate)`), and the aggregate inner-cause assertion in the atomic rollback test was hardened in 350dcd21f468a59a6594f5980fe4b3366b3f4b14.