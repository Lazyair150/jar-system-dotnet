# World-scoped Resource Owner Identity

Date: 2026-11-26  
Status: Normative target model; WorldInstanceId lifecycle and typed OwnerIdentity/AccountId v2 contracts implemented; runtime owner mapping pending  
Related: ADR 0011, ADR 0010, Core conceptual model sections 20–22

## Purpose

This document formalizes the identity boundary for every resource-owning individual and every ledger endpoint. It supplements the Core conceptual model; it does not claim that the implementation already satisfies the target.

## 1. Identity layers

These identities answer different questions and MUST NOT be substituted for one another:

| Identity | Question answered | Example source |
|---|---|---|
| WorldInstanceId | Which isolated runtime world owns this state? | Host-created persistent world instance identity |
| DefinitionId / DefinitionVersion | Which authored template defines this object? | Component/board catalog |
| OwnedInstanceId | Which independently owned copy is this? | Player progression aggregate |
| OwnerId | Which resource-owning individual owns this account? | Stable local instance identity, scoped to World |
| AccountId | Which exact resource account is this? | World + Owner + AccountKind |
| PortId | Which boundary accepts/emits a resource? | Stable area-local port definition |
| ResourceIdentity | Which physical/logical resource is this? | JarId or World Vocabulary key |
| DisplayName | What should a human see? | Localized or authored display value |

A Board Definition is not a Board instance. A role such as Master/Slave is not an Owner. A Producer's display name is not a Producer identity. A Shape is not a Jar identity.

## 2. World identity

Every durable World has one immutable WorldInstanceId. It is distinct from its configuration fingerprint: two worlds built from identical options are still independent worlds.

The host may create a random identifier once, but must persist it and inject the same value during restore and deterministic replay. Core constructors and test fixtures must not silently generate new IDs in ways that alter replay output.

Legacy checkpoints without WorldInstanceId require an explicit migration identity supplied by the owning save/session boundary. Core must not derive it from configuration fingerprints or display labels because identical worlds could then alias.

The current implementation boundary is:
- A normal `WorldV2HostService.Replace(options)` creates one new opaque identity at the Host boundary.
- The Host also exposes an explicit `Replace(options, worldInstanceId)` path for a save/session owner that has already persisted the identity.
- `WorldV2Factory` and `WorldV2Context` accept the identity as an injected value; neither creates one implicitly.
- Execution checkpoints and the Godot bridge expose/persist the same identity. Restore creates the replacement Context with the checkpoint identity before applying the checkpoint.
- Legacy checkpoint restore is rejected unless the caller supplies a migration identity. The migration identity is copied into the migrated checkpoint; it is never guessed or silently generated during restore.
- The save/session owner must persist the identity returned by `GetWorldInstanceId()` or, preferably, the captured checkpoint. A fresh ordinary world replacement intentionally receives a different identity.

## 3. Owner identity

OwnerIdentity is the tuple:

(WorldInstanceId, OwnerKind, LocalOwnerId)

LocalOwnerId must be stable for the lifetime of the owned individual. Account identity adds AccountKind:

(WorldInstanceId, OwnerKind, LocalOwnerId, AccountKind)

Canonical serialization must be versioned and length-prefixed. DisplayName changes, role changes, localization, ordering, and object memory addresses must not change OwnerId or AccountId.

The typed contract now exists as `OwnerIdentity(WorldInstanceId, ResourceOwnerKind, LocalOwnerId)` and `AccountIdentity(OwnerIdentity, AccountKind)`. Its canonical account form is `account:v2:` followed by four UTF-8 byte-length-prefixed fields: WorldInstanceId, stable OwnerKind token, LocalOwnerId and AccountKind. The parser rejects truncated, non-canonical, unknown-kind and trailing data. The two-argument `RuleResourceAccount(AccountKind, OwnerId)` remains the explicit v1 compatibility constructor; the typed-owner constructor emits v2. Transfer contracts reject cross-World v2 endpoints and mixed v1/v2 endpoints. Resource-port adapters now have typed-owner overloads; their legacy overloads remain for staged migration.

The host-supplied `WorldV2OwnerIdentityMap` is now part of the runtime object graph and schema-7 checkpoint:
- It carries exact Ship, CircuitBoard, ProductionArea and Warehouse local IDs plus dictionaries keyed by exact ProducerInstanceId and Workshop OwnedInstanceId.
- Factory creation binds the map to ShipLiquidInventory, ResourceArea, Warehouse, Producer and Workshop objects. If a map is supplied, every live Producer and Workshop must have an exact configured instance ID and the map must contain exactly those IDs; duplicate IDs are rejected.
- Warehouse port adapters use the exact Warehouse owner; Workshop order staging derives a scoped local owner ID from the exact Workshop owner and order ID.
- The map is captured and restored with the checkpoint. Restore compares the full map before mutating the live context.
- Runtime `ProducerInstanceId` and `OwnedInstanceId` fields are excluded from the World definition fingerprint; the identity map is persisted independently. This keeps instance identity separate from definition/configuration identity.
- Ledger checkpoint restore validates every account:v2 endpoint against the current WorldInstanceId. Legacy account:v1 values remain readable without an inferred World.


The intended ownership mapping is:

- Ship -> exact Ship OwnedInstanceId.
- Circuit Board -> exact Board/Circuit instance identity, not BoardDefinitionId.
- Production Area -> Circuit instance + stable Area/SubArea identity, or its own OwnedInstanceId.
- Warehouse -> exact Warehouse instance identity, or a documented subordinate account of its ResourceArea owner.
- Producer -> ProducerInstanceId, distinct from Producer.Name.
- Workshop -> exact OwnedInstanceId; Master/Slave remains a role relation.
- Order staging -> Workshop owner + exact order/demand identity + AccountKind.

## 4. Endpoint algebra

A nullable AccountId cannot distinguish an external mint/sink from a legacy unknown endpoint. The target endpoint model is a tagged union:

ResourceEndpoint = Account(AccountId) | ExternalSource(SourceId) | ExternalSink(SinkId) | LegacyUnknown

New events must never emit LegacyUnknown. Legacy readers may preserve it without guessing.

Examples:

- Initial stock: ExternalSource(WorldInitialization) -> Ship Account.
- Production: Producer Output Port -> Warehouse Account.
- Warehouse flow: Warehouse Account -> Workshop Account.
- Transfer/return: Account -> Account.
- Discard: source Account -> ExternalSink(Discard).

If a producer later owns a real output buffer, that buffer becomes an Account endpoint; the event semantics must change explicitly rather than silently.

## 5. Resource fact and display contract

The target resource fact is:

(OperationId, ResourceKind, ResourceIdentity, DisplayName, Attributes, SourceEndpoint, TargetEndpoint, Amount, Cause, Reason)

Jar identity is canonical JarId; Shape is an attribute/display value. Liquid identity is the World Vocabulary key; localization is display-only. Account/Owner/Port identifiers are machine identities.

ResourceLedgerMutation.Name remains a compatibility input during migration. New code must populate DisplayName explicitly. Wire/checkpoint schema changes must be versioned; old Name values may populate DisplayName on read, but missing machine identity remains Unknown.

## 6. Multi-source settlement

When settlement drains multiple order-scoped accounts, each source allocation must remain a separate Transfer Leg/fact. A synthetic aggregate "order account" must not be invented merely to simplify the ledger. One Compound OperationId may group the legs while each leg retains its actual source and target AccountId.

## 7. Required migration gates

1. Introduce explicit WorldInstanceId and OwnerIdentity contracts.
2. Add stable identities for Ship, Circuit/Production Area, Warehouse and Producer.
3. Introduce World-scoped AccountId v2 while preserving v1 legacy reads.
4. Introduce tagged endpoint kinds.
5. Separate DisplayName from Name and version the checkpoint/Wire schema.
6. Migrate all production, stock, flow, transfer, return and discard fact producers.
7. Preserve per-source settlement allocation.
8. Only then implement the multi-account compound transfer coordinator.

The migration must preserve current resource behavior, Jar entity identity, Ban scope, deterministic replay, and old checkpoint readability.


## 8. Implementation status (2026-11-26)

The implementation now includes the tagged endpoint algebra `Account | ExternalSource | ExternalSink | LegacyUnknown` on ResourceEvent, ResourceLedgerMutation, snapshots and schema-8 checkpoint wire DTOs. Schema 3–7 remain readable; missing tags map from explicit legacy AccountId when present, otherwise to LegacyUnknown. WorldV2Context validates every tagged Account v2 endpoint against the restored WorldInstanceId.

B-class Workshop handoff now emits World-scoped account endpoints whenever exact Workshop OwnerIdentity is available. Order settlement captures the per-source balance before draining and stores per-source Requested/Accepted/Discarded allocations in the lease receipt/checkpoint. Ledger facts are emitted as separate source-account legs; accepted return targets the Ship Account, and discarded quantity targets an ExternalSink. A deterministic master-then-slave allocation policy is currently used for same-liquid fungible quantities.

This is still a staged migration:
- Traditional ProductionService stock/production/warehouse flow/discard Record sites have not all been given explicit owner endpoints.
- LegacyUnknown remains allowed for old and not-yet-migrated write paths.
- ResourceEvent.Name has not yet been fully separated into DisplayName.
- The durable cross-account physical transaction coordinator remains unimplemented.
- Code has been written to the feature branch. GitHub Actions push run #5132 passed at code/test head `7329b7fa77488b616a222e6c00f14a34583de639`: 994 xUnit tests passed (0 failures), solver differential 4004/4004, modifier differential 400/1600, session replay 33 commands, config-world match, and security scan 6 projects / 0 vulnerable packages. No local build/test run is claimed.


## 9. Ledger endpoint migration of World V2 runtime paths

World V2 stock, production, warehouse flow, prefill, rollback and retired-prefill facts now use explicit tagged endpoints whenever the corresponding runtime object has an OwnerIdentity:

- Initial stock is an `ExternalSource` entering the Ship account.
- Aggregate Jar production is attributed to the ProductionArea account and enters the Warehouse account. ShapeProductionLine currently aggregates multiple Producer rates and carries one shared fractional accumulator; assigning produced whole Jars to individual Producer accounts would invent attribution or change production semantics. Do not do that until production itself exposes deterministic per-Producer output facts.
- Warehouse overflow and ship-return overflow target explicit `ExternalSink` identities.
- Warehouse-to-Workshop flow uses exact Workshop OwnerIdentity, with account:v1 only as a compatibility fallback when a legacy exact OwnedInstanceId exists.
- Resource-port transfer participants now publish explicit source and target Account endpoints and retain the matching AccountId compatibility projections.
- ResourceLedger rejects account-to-account facts that mix account:v1 with account:v2 or cross WorldInstanceId.

The old `ProductionService` path remains an intentional LegacyUnknown migration boundary because it does not yet receive a stable World/Owner identity map. Display strings and producer names must not be used to fabricate ownership. This legacy path should be migrated only when its host assembly supplies stable identities.

## Host composition requirement — 2026-11-26

Normal `WorldV2HostService.Replace(options)` and `Replace(options, worldInstanceId)` calls must create a complete `WorldV2OwnerIdentityMap`, not only a WorldInstanceId. The Host assigns opaque local IDs for Producers and Workshops that do not already have persisted IDs, while preserving exact IDs supplied by the definition or BoardAssignmentSnapshot. The generated IDs are written into the normalized options snapshot; callers must persist that normalized configuration together with execution checkpoints.

`WorldV2ApplicationService.CaptureWorldOptions()` and the Godot bridge equivalent expose that normalized snapshot. The ApplicationService retains a bounded recent mapping from WorldInstanceId to its normalized options so a checkpoint can restore the exact earlier world after a replacement. Across process restarts, callers must persist and supply the matching normalized options explicitly; identities must never be reconstructed from names, collection positions, or roles.

The legacy `JarSystem` composition root now accepts the same explicit World/Owner identity map. If any legacy ProducerInstanceId, OwnedInstanceId, or WorldInstanceId is supplied without the complete map, construction fails closed. Existing definition-only legacy configurations remain readable and continue through the documented LegacyUnknown compatibility boundary.