# Resource Event Identity and Idempotency Audit

Date: 2026-11-26

Repository: `Lazyair150/jar-system-dotnet`  
Branch: `architecture/core-liquid-identity-consolidation`  
PR: #63 (Open / Draft; not merged)

## Scope

Continue ADR 0010's event-fact identity and idempotency vertical slice. Keep physical Jar identity, display Shape, liquid vocabulary identity, operation identity, checkpoint persistence, and legacy compatibility distinct. Do not claim compound B-class transfer atomicity is complete.

## Implemented

1. **Jar identity allocation continuity**
   - `JarSystem.ResetProducerModuleCore` now passes the world's injected `JarIds` generator into the replacement `ProducerModule` / `Warehouse`.
   - Previously the replacement module fell back to process-global `DefaultIdGenerators.Jars`. The deterministic session replay then ran two worlds against a shared static generator and diverged in exposed Jar IDs even though command log and process journal matched.
   - A regression test exercises the same injected identity source after explicit producer-module replacement and full world reset.

2. **Warehouse Jar identity**
   - Warehouse assigns a positive JarId when a Jar enters its authoritative storage, including overflow Jars before they are returned to the object pool.
   - Immutable `WarehouseJarFact` projections preserve JarId + Shape before a Jar may be recycled.
   - Production and discard ledger facts are emitted per Jar, amount=1; Shape remains display data.
   - Warehouse and Workshop allocator restore paths advance to checkpoint JarId high-water marks. A non-restorable generator is rejected when a positive high-water restore is required; zero is a no-op.

3. **Zero-resource ledger semantics**
   - `RecordBatchOnce` no longer creates an OperationId tombstone or fingerprint for an empty mutation batch.
   - Zero-resource settlement advances its domain settlement lease; it does not manufacture a resource-ledger operation.
   - The old test that expected an empty batch to create an OperationId was replaced with the ADR 0010 invariant.

4. **All inspected legacy migration / rollback facts carry identity**
   - `ResourceTransferrer`: Jar transfer/discard facts use canonical JarId; liquid facts use vocabulary identity. Pending facts retain identity until commit.
   - `SlaveRollback`: Jar return/discard and liquid return/discard facts carry identity.
   - Jar identities are preflighted before workshop migration mutates ownership; rollback validates a Jar identity before removing it from the slave.
   - Regression assertions cover migration transfer, migration discard, slave return, and rollback liquid identity.

5. **Checkpoint operation/fingerprint consistency**
   - Restore now requires each event with OperationId to have both an OperationId index entry and a retained payload fingerprint.
   - Transfer receipts must also have their operation fingerprint.
   - The World V2 ledger Wire DTO enforces the same relationship, preventing a checkpoint from retaining an event/receipt while silently losing the payload binding.
   - Added negative tests for event-without-fingerprint and receipt-without-fingerprint restores, plus Wire DTO rejection.

6. **Contract baseline**
   - The generated contract diff showed only identity-value changes: workshop Jar IDs and dependent solver-path JarIds shifted because Jar identity is now assigned at Warehouse admission, and the Warehouse Jar no longer has a null ID.
   - This is an intentional observable semantic change, not schema drift. The contract baseline was updated from the generated artifact rather than weakening the snapshot gate.

## CI evidence

Successful runs, each associated with the exact listed HEAD:

- HEAD `f86397b9fea186d6fad2810191f2cdce36d2d82e`: runs `45652265562` and `45652270942` — Success. This validates the intentional contract baseline update and the prior reset/replay/empty-batch fixes.
- HEAD `3106de8a8fd011a291d3031cacca0eb4ed829e6f`: runs `45652432129` and `45652439281` — Success.
- HEAD `5163f1c561a71f28f804674441854aae841dc496`: runs `45652464290` and `45652473035` — Success.
- HEAD `640480c84fd889634c3d54b1c70bfbcd540914ae`: runs `45652634833` and `45652648418` — Success.
- HEAD `95eba9524760dbee57c3c7f1eec0708247d96f18`: runs `45652896703` and `45652918550` — Success.

The latest code HEAD at the time this record was written is `2bd8524057c84a2a7e488cfc3335180fe9aeba6b`. Its new fingerprint-linkage validation and negative tests have runs `45653547924` and `45653563944` in progress; they are **not** recorded as passing.

## Failed validations and corrections

- HEAD `a57bc8e` failed Session Replay with `stateEqual=False`, while `logEqual=True` and `journalEqual=True`. Root cause: `ResetProducerModuleCore` omitted the injected Jar ID source. Passing `JarIds` into the replacement module restored deterministic replay.
- The first reset-source regression test incorrectly expected a producer tick after `ResetProducerModule()`, although that API intentionally creates a fresh, unconfigured producer module. The test was corrected to admit a Jar directly into the replacement Warehouse and assert the injected allocator's next ID. Production behavior was not weakened.
- HEAD `e22113d` passed build, xUnit, and replay, but failed the contract snapshot because IDs intentionally changed. The generated diff was reviewed and the baseline updated; the subsequent exact HEAD CI passed.

## Remaining work and known boundary

1. **Stable account endpoints are now implemented for the explicit account-transfer paths.**
   - `ResourceEvent` / `ResourceLedgerMutation` carry `FromAccountId` and `ToAccountId` separately from the human-readable `From` / `To` labels.
   - `RuleResourceAccount.AccountId` uses the versioned length-prefixed `account:v1` encoding.
   - Single-resource port transfers derive and verify endpoint IDs from the immutable receipt accounts.
   - B-class handoff records Jar Storage endpoints and either free-liquid-staging or order-liquid-staging endpoints from the exact `OwnedInstanceId`.
   - Workshop migration and slave rollback also record account IDs when their Workshop instances are Board-owned.
2. Some legacy or incompletely identified resource paths still legitimately produce null endpoint IDs: the legacy `JarSystem` ProductionService Warehouse events, World V2 initial ship-stock / ship-to-workshop funding, and settlement that merges multiple order sub-accounts before returning to the ship. These must not infer account identity from display labels. Completing them requires explicit Warehouse/Ship owner identity and, for multi-source settlement, preserving per-source allocation rather than inventing one aggregate owner.
3. `ResourceLedgerMutation.Name` remains the compatibility/display-name field; a separately named `DisplayName` API alias is not yet introduced.
4. `ResourceTransferBatchRequest/Receipt` and a multi-account prepare/commit/compensate coordinator are not implemented.
5. B-class Jar Storage + Order Liquid Staging is now represented by stable endpoint IDs in the ledger facts, but its physical mutation still lacks one durable compound prepare/commit/compensate boundary and fault-injection proof. Do not claim whole-transfer atomicity.
6. After Core reaches a verified integration SHA, update Space's pinned Core SHA and run the cross-repository workflow. Space remains unchanged in this slice.

## Latest verification update

- HEAD `2bd8524057c84a2a7e488cfc3335180fe9aeba6b`: runs `45653547924` and `45653563944` — Success. xUnit reported 958 passed, 0 failed; Session Replay and contract snapshot passed.
- Current code HEAD when this update was recorded: `bb524358a795873ac540bb67ea5be9f150998524`.
- Current HEAD runs `45655401604` and `45655380510` are In Progress; no pass is claimed yet.
- PR #63 remains Open / Draft. Space's Core pin is unchanged.

## Repository state

- Core PR #63 remains Open / Draft and is not merged.
- Space PR #2 and its pinned Core SHA were not modified.
- No claim is made that GUI validation or compound B-class atomicity is complete.

## Account endpoint and resource-fact implementation update

- Added canonical `RuleResourceAccount.AccountId` using a versioned length-prefixed encoding of `AccountKind + OwnerId`; tests cover stability, account-kind separation, and ambiguous concatenation pairs.
- Added `FromAccountId` / `ToAccountId` to `ResourceLedgerMutation` and `ResourceEvent`. The fields are copied through `ResourceLedgerSnapshot`, `Capture`, `Restore`, clone paths, and `ResourceEventWireDto` / `ResourceLedgerWireDto`.
- `RecordTransferOnce` now derives missing endpoint IDs from the receipt's exact source/target accounts, then validates both endpoint IDs against those receipt accounts. Explicitly supplied but mismatched IDs are rejected. Since the fields participate in the mutation fingerprint, a replay with changed account endpoints conflicts.
- B-class order handoff emits distinct account identities for Jar Storage, free-liquid staging, and order-liquid staging. Workshop migration and slave rollback also attach the relevant account identities where the workshop has an exact `OwnedInstanceId`.
- The public `WorldV2ResourceEventDto` projection now exposes both endpoint identity fields, keeping them separate from the display-oriented `From` / `To` strings.
- World V2 checkpoint schema is now version 5. Schemas 3 and 4 remain accepted; missing endpoint identities deserialize as null/unknown. No migration derives an account identity from `Workshop.Name`, and no JarId is inferred from Shape.
- `ResourceAreaAdvanceResult` now preserves immutable per-Jar facts for production, overflow discard, and warehouse-to-workshop flow. `ResourceWorldRuntime` writes those facts individually with canonical JarId, Shape as display name, and amount=1. Liquid production/discard/flow facts retain the canonical World Vocabulary identity. Flow-to-workshop events include the target workshop account identity when `OwnedInstanceId` is present.
- Regression coverage now includes canonical liquid production/discard identity, Jar production-to-flow identity continuity, overflow Jar identity after object pooling, B-class handoff/rollback endpoint identities, and single-resource transfer account IDs surviving snapshot + checkpoint Wire round trips.

## Compatibility and API-contract review

- The DTO field additions are an additive public projection change, but they alter the serialized checkpoint Wire shape. This is why the checkpoint envelope was explicitly bumped from schema 4 to schema 5 rather than treating the new fields as an unversioned implementation detail.
- An intermediate CI run exposed stale test expectations: a bridge test still hard-coded schema 4, and an unsupported-version test treated schema 6 as unsupported while the intermediate codec version was 6. The contract was normalized to current schema 5 with explicit legacy support for 3 and 4; tests now use `CurrentSchemaVersion` and reject unsupported version 6.
- Subsequent test-only failures were traced to the legacy-envelope test fixture itself: first its raw interpolated JSON delimiter was malformed, then the empty payload was compared to a non-nullable record's default-value expectation. The fixture now uses a `$$"""` raw interpolated string with explicit payload values. No production validation was relaxed.
- Failed runs are retained as failure evidence, not hidden: `45654313983` (HEAD `ce2d1d7`) exposed stale schema-version expectations; `45655313024` (HEAD `7651f3f6`) exposed the raw-string compile error; `45656078754` (HEAD `c5869935`) and `45656243879` (HEAD `7baf8242`) exposed the empty legacy payload expectation. These do not validate the later corrected HEAD.

## Remaining gaps after this slice

1. The Warehouse / Producer / Ship accounts still lack explicit persisted owner identities. Consequently some production, initial-stock, ship-to-workshop funding, and aggregate settlement events correctly retain null `FromAccountId` / `ToAccountId`; display labels must not be used to fabricate these IDs.
2. The legacy `JarSystem` ProductionService Warehouse events still need an explicit owner identity before stable endpoints can be populated.
3. `ResourceLedgerMutation.Name` remains the display label for compatibility; a separately named `DisplayName` contract alias has not been introduced.
4. `ResourceTransferBatchRequest/Receipt`, a multi-account prepare/commit/compensate coordinator, and B-class physical transaction fault-injection proof remain outstanding. Stable endpoint facts do not imply compound transfer atomicity.
5. Space's Core pin remains unchanged until this Core slice reaches a verified integration SHA.

## Latest exact-head status

- Code HEAD: `b7c376c0c5f3c624b14b2132e2314f90b8cb5bad`.
- Runs `45657100739` and `45657120041` are associated with this exact HEAD and were In Progress when recorded. No pass is claimed.
- PR #63 remains Open / Draft and unmerged.


## Follow-up verification and test hardening

- Exact HEAD `ee861f3fbc1bd46bd8fe525b95662255a46a2c9c` passed CI run `45658267325` (and the paired run `45658248002`): xUnit 964 passed / 0 failed; `CONFIG-WORLD-MATCH`; solver differential 4004/4004; rule/modifier differential 400 scenarios / 1600 checks; `REPLAY-OK` for 33 commands; dependency scan `SEC-OK` with 0 vulnerable packages; final `VERDICT=OK`.
- The successful exact HEAD includes the account-ID event projection, Jar production/discard/flow facts, and the liquid-only correction to the existing liquid receipt test. The latter now filters ledger facts by `ResourceKinds.Liquid`, rather than summing Jar and Liquid amounts together.
- Two test-only hardening changes followed: `RecordBatchOnce` round-trip now asserts account endpoints survive the Wire DTO and snapshot, and a changed account endpoint under the same OperationId must conflict; legacy ledger Wire compatibility now tests both schema 3 and schema 4, asserting ResourceIdentity and both account endpoints remain null/unknown.
- Code HEAD at this record update: `e78abd388aeee0785933feef894e53292b955e60`. Runs `45658706720` and `45658719513` are associated with this exact HEAD and remain In Progress; the added tests are not yet claimed as passing.
- The immediately preceding failed runs are classified: `45657100739` failed because Jar ledger tests were placed outside their test class; `45657633099` failed on the same brace placement; `45657765218` failed because the liquid-only assertion lacked the Production namespace import. These were test-source defects, not runtime behavior failures; subsequent commits corrected them.
- PR #63 remains Open / Draft and unmerged. Space remains untouched.


### Verification correction

- The exact code HEAD `e78abd388aeee0785933feef894e53292b955e60` completed run `45658706720` with **Success** after the previous in-progress note was written.
- Exact-run evidence: xUnit 965 passed / 0 failed; configuration contract matched; solver differential 4004/4004; modifier/rule differential 400 scenarios / 1600 checks; Session Replay 33 commands passed; dependency scan covered 6 projects with 0 vulnerable packages; `VERDICT=OK`.
- Paired run `45658719513` for the same HEAD remained In Progress at this check. The additional endpoint fingerprint and schema 3/4 compatibility tests are included in the 965-test result.
- The current branch subsequently contains documentation-only commit `f53a691a4b79c7b136443d62e35843d241a22203`; the successful run above validates its exact parent code HEAD, not this documentation commit.
