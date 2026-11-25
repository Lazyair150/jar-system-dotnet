# World-scoped Resource Owner Identity

Date: 2026-11-26  
Status: Normative target model; WorldInstanceId host-lifecycle slice implemented, OwnerIdentity migration pending  
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
