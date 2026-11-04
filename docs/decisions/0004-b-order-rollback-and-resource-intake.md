# ADR-0004: B-order temporary slave ownership and resource-intake ordering

## Status

Accepted

## Date

2026-11-03

## Context

A B-class order temporarily moves selected jars and liquids from the master to the slave. The slave is
a raised tool position for that order, not a second warehouse: the warehouse flows only into the
master, and a non-empty workshop cannot be selected as the slave. The system still must account for
resources produced on the slave during execution. In particular, pouring a full tool jar creates free
liquid that is absent from the original transfer ledger.

All receiving paths reuse the same low-level workshop capacity methods. The caller determines whether
overflow waits, fails the transfer, remains in the warehouse, or is discarded. The tick phase order
also gives rollback resources an implicit priority over warehouse flow in the same asynchronous tick.

## Decision

1. Require the slave to be empty when selected and again when a B-class order starts.
2. Remove delivered jars before rollback.
3. Drain every remaining jar and every non-zero free liquid from the slave to the master. Do not limit
   the return to the original transfer ledger: a tool jar may spill free liquid that was never recorded
   as transferred liquid.
4. If the master cannot accept the returned resource, discard it and increment the global discard ledger.
5. Keep the slot receiver generic:
   - `Warehouse.FlowToMaster`: leave jar excess in warehouse; partially receive each liquid;
   - B transfer: fail the batch if required jars cannot enter the slave; leave skipped liquid excess
     with the master;
   - rollback: partially receive liquid, discard the remainder;
   - workshop switch: preserve high-value jars first and discard overflow.
6. Keep the tick order explicit: events → modifiers → processes → production → call-boundary flow.
   A B order that completes in the process phase therefore returns resources before same-tick warehouse
   flow uses the remaining capacity.

## Alternatives Considered

### Keep resources that existed on the slave

Rejected. A populated workshop is not a valid temporary slave. Allowing this creates two competing
notions of ownership and lets a B-class order consume warehouse-adjacent resources that never passed
through the master intake.

### Return only the transferred ledger

Rejected. The ledger describes inputs, not the final slave state. Tool jars can produce free liquid;
if that liquid is not consumed, it remains on the slave and must return with the other leftovers.

### Move rollback overflow into the warehouse

Rejected. The rule is one-way warehouse-to-workshop flow; rollback overflow is discarded and counted.

### Make the workshop receiver choose a global priority

Rejected. The receiver is a capacity primitive. Different use cases require different policies:
warehouse excess waits, B-transfer can fail atomically, rollback discards overflow, and switch values
resources differently.

### Run warehouse flow before process completion

Rejected for this change. It would give warehouse inflow priority over returning the player's borrowed
resources and would alter gameplay. If that policy is desired, it must be an explicit product decision,
not an invisible call-order side effect.

## Consequences

- Delivered jars, remaining tool jars, free liquid produced during execution, and overflow are
  distinguishable and handled by one drain operation.
- The idle-slave residual invariant detects any implementation path that forgets to drain.
- Rollback and switch overflow agree with the discard accounting contract.
- Same-tick priority is documented and tested instead of being an accident of method placement.
- A future configurable intake-priority policy can replace the fixed phase order without changing the
  low-level workshop receiver.
