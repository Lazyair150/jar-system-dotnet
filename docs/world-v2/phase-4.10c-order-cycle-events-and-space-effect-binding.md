# World V2 phase 4.10c — order-cycle events and Space effect binding

Status: domain contract. This document extends the World V2 order lifecycle without making Jar depend on Space gameplay code.

## 1. Purpose

A World V2 order is the production/execution prototype of a ship capability. In the Space game, selected orders are part of the player's pre-battle build. A successful order cycle triggers a Space-owned world effect; cooldown-based buffs are a primary example.

Jar remains responsible for:
- order feasibility, admission, reservation, and execution;
- liquid/jar/workshop/resource lifecycle;
- order fulfillment policy and the order's own cooldown duration/remaining time;
- authoritative fact that a successful order cycle completed.

Space remains responsible for:
- ship, production-device, workshop-combination, and order loadout selection;
- mapping the selected order to a versioned effect definition;
- buff/effect creation, duration, stacking, refresh, target, and actual world mutation;
- combat/world consequences and persistence of processed effect identities.

Jar must not reference Godot, Space actors, enemies, players, health, damage, score, wallet, rewards, or a Space effect implementation.

## 2. Distinguish order cooldown from effect duration

These are two independent clocks/states, both driven by the shared Space simulation time but owned by different domains:

| Concept | Owner | Meaning |
|---|---|---|
| Order cooldown | Jar | Time before the same retained order can be solved/executed again |
| Buff/effect duration | Space | How long the world effect produced by a successful cycle remains active |
| Effect stacking/refresh | Space | How a new successful cycle interacts with an existing effect |
| Order execution progress | Jar | Progress through the immutable order execution plan |

Pausing the Space world freezes all of them because no shared simulation time is advanced. It does not cancel execution or clear an active buff.

A Jar order cooldown is not the buff duration. Space may choose an effect duration shorter or longer than the order cooldown, but that is effect-catalog policy, not a Jar rule.

## 3. Order binding

A runtime demand ID is not a stable order definition identity. A retained demand may fulfill repeatedly, and a new world may allocate different runtime IDs.

The loadout integration therefore needs an opaque stable order-definition key, supplied when the pre-battle order selection is materialized into the Jar demand board. Jar may store and return this key but must never interpret it.

Space's immutable battle loadout stores the mapping:

    orderDefinitionKey -> (effectDefinitionId, effectDefinitionVersion, effectParameters)

The exact effect parameter schema is Space-owned and should not be serialized into Jar domain objects. Jar stores only the opaque order-definition key required to correlate the event with the selected loadout.

The binding must be captured with the active battle's loadout. Catalog edits after combat begins must not change the effect of an order already selected for that battle.

## 4. Successful cycle identity

Current World V2 behavior allows a retained order to complete, enter its own cooldown, become ready again, and complete another cycle. Thus one DemandId can produce multiple successful cycles.

Each successful cycle requires:
- demandId: runtime demand identity;
- orderDefinitionKey: stable opaque selected-order identity;
- cycleSequence: monotonically increasing successful cycle number scoped to the demand identity;
- eventId: globally unique and stable identity for this cycle;
- completedAtWorldTime: Jar logical world time at completion.

cycleSequence increments only after the successful fulfillment transition commits. Failure, cancellation, interruption, admission rejection, and ordinary solve failure do not consume a successful cycle number.

A suitable event ID may be derived from (shipWorldId, jarWorldInstanceId, demandId, cycleSequence) if all components are stable and checkpointed. Alternatively Jar may allocate an event ID and persist it atomically with the outbox. Do not derive event identity from wall-clock time or a UI callback.

If demand IDs can be reused across world replacement, include a stable Jar world-instance identity in the key.

## 5. Outbox event

Introduce a detached value-only event such as OrderCycleCompleted:

- eventId
- shipWorldId (host-supplied world identity)
- jarWorldInstanceId
- orderDefinitionKey
- demandId
- cycleSequence
- completedAtWorldTime
- fulfillment result/requirement summary
- optional Jar-owned output references, only where Space is explicitly allowed to consume them

The event reports that Jar successfully completed one configured order cycle. It does not report that Space successfully applied a buff.

### Atomicity boundary

The event must be recorded in the same Jar-side logical mutation boundary as:
1. successful order resource settlement;
2. demand successful-fulfillment policy (remove or retain and enter cooldown);
3. successful cycle sequence increment.

If any part of the success transition fails, there must not be a visible success event. Conversely, once the event is visible, the order's successful fulfillment/cooldown transition must already be committed.

Do not publish callbacks into Space from inside Jar Core mutation code. Store the event in a durable outbox and expose detached reads/acknowledgment through ApplicationService and Bridge.

## 6. Read and acknowledgment

The Application/Bridge contract should support:
- read pending events, ordered by stable sequence;
- acknowledge one or more event IDs;
- repeated read returns the same unacknowledged event;
- repeated acknowledgment is harmless;
- unknown event IDs are rejected or reported explicitly;
- event payloads are immutable detached DTOs.

The outbox is not the bounded resource ledger. Ledger retention/compaction must not delete unacknowledged domain events.

Space applies effects idempotently:
1. Validate ship-world and selected-order binding.
2. Resolve effect definition/version from the battle loadout.
3. Check the Space processed-event set.
4. Apply the effect and persist processed identity/effect state.
5. Acknowledge the Jar event only after Space's effect state is durable.

If the Space save cannot atomically persist effect state and processed identity, the effect handler itself must be idempotent on eventId.

## 7. Checkpoint and restore

The Jar execution/resource checkpoint must include:
- each demand's orderDefinitionKey;
- each demand's successful cycle sequence;
- pending outbox events;
- acknowledged/retained state required to prevent replay;
- the stable Jar world-instance identity, if used in event IDs.

Restore must validate uniqueness and consistency:
- no duplicate event IDs;
- cycle sequence is non-negative and consistent with emitted events;
- every pending event refers to a valid order-definition key and demand identity in the checkpoint history;
- event completion world time does not exceed the checkpoint world clock;
- acknowledgment cannot erase the only durable evidence needed for idempotency if an old event can be replayed from a stale save.

The checkpoint format must be versioned. Old checkpoints with no event state need an explicit migration rule (empty event history only when no successful event could have been lost, or a migration barrier); do not silently fabricate completion events.

## 8. Implementation order

1. Add opaque orderDefinitionKey to the demand creation/configuration path and its checkpoint wire DTO.
2. Add persisted successful cycle sequence to demand state and snapshots.
3. Introduce immutable OrderCycleCompleted event and an outbox owned by the World V2 Context/Application boundary.
4. Make successful fulfillment and outbox append one failure-safe logical transition.
5. Add pending-event read/ack ApplicationService and Bridge methods.
6. Add tests for repeated cycles, failure/cancel/interruption non-events, checkpoint restore, duplicate read/ack, and event identity stability.
7. Integrate Space effect binding and idempotent dispatcher only after the Bridge contract is available.

Do not combine this event feature with energy-stone production ingress or arbitrary liquid import. Those are separate resource adapters.

## 9. Explicit non-goals

- No buff/effect implementation in Jar.
- No effect duration, stacking, target, or numeric balance rules in Jar.
- No migration of legacy reward/wallet/material/attack-upgrade behavior.
- No assumption that every order must be a buff; the selected Space effect binding determines the world meaning.
