# ADR-0003: Order lifecycle has one guarded source of truth

## Status

Accepted

## Date

2026-11-03

## Context

The public order contract already had four status values: pending, executing, success, and failed.
The domain order only moved from pending to success/failed, while `OrderEvent` kept a second string
status that moved to executing when execution started. During asynchronous execution the public order
therefore still said "pending" while a live process was running. `WorldInvariants` also had an
`ORDER-EXECUTING-WITHOUT-PROCESS` check that could never observe a domain order in the executing state.

This was not merely incomplete presentation. Two owners could disagree about the same lifecycle.

## Decision

1. `Order.Lifecycle` is the single typed state owned by the domain order.
2. The public `Order.Status` string is a mapping of that state, not a mutable second field.
3. `OrderEvent.Status` reads the order status; it does not store or assign another copy.
4. Legal transitions are guarded:
   - pending → executing;
   - executing → succeeded;
   - executing → failed;
   - repeated terminal success/failure is idempotent.
5. `OrderEvent.Start` is the execution-start boundary that moves the order to executing.
6. Delivery/failure completion moves the order to its terminal state before the final public
   projection is emitted.
7. Process lifecycle, execution session stages, and jar operation marks remain separate state axes;
   they must not be flattened into the order status.

## Alternatives Considered

### Keep two string statuses synchronized manually

Rejected. Two writable authorities can drift, and the asynchronous execution path already exposed the
drift.

### Move the order status into `OrderEvent`

Rejected. `OrderEvent` is one execution attempt, while an order is a longer-lived aggregate. A retry,
cancellation, or synchronous execution must not recreate the order's identity.

### Encode order and process state as one enum

Rejected. A blocked process is still an executing order. A process can finish successfully before
delivery rejects the order. These are concurrent, related lifecycles.

### Put `IsMarked` into the same enum

Rejected. Jar content, operation lock, location, and object-pool lifecycle are orthogonal properties.
Flattening them would produce a combinatorial and incorrect state list.

## Consequences

- During asynchronous execution, `state.orders[].status` now truthfully reads `执行中`.
- `WorldInvariants` can detect an executing order without a live process.
- Existing JSON values are unchanged; the TypeScript/state contract does not gain a new key.
- Direct callers cannot skip from pending to success/failure through assembly-internal mutators.
- Tests must cover legal and illegal order transitions independently from process transitions.
