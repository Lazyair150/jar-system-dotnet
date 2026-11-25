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
