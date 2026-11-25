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
