# Phase 4.4b — Resource reservation and execution admission

## Scope

This phase adds the boundary between an optimistic solve result and an execution that owns resources.

- A solve result remains a shared, optimistic proposal. Solving does not reserve anything.
- Admission rechecks the demand, solve-book state, world version, live workshop resources, and transfer constraints.
- Only after every check passes does the reservation book atomically claim all referenced jar IDs and required initial free-liquid amounts.
- Admission success means **reserved**, not started or completed.
- Release removes reservation records only. It never rolls back liquid already consumed or jars already changed.

## Resource accounting

The resource manifest is the single source of the resource list:

- LockedJarIds is the distinct union of jars referenced by the master path and all B-plan transfer jars.
- MasterInitialLiquidAmounts is the minimum initial free-liquid inventory needed to satisfy every path prefix, plus the amount that must remain at the master end to supply the slave path.
- TransferLiquids is the slave path's required starting liquid amount. It is a movement budget, not an additional master inventory reservation. Adding it to MasterInitialLiquidAmounts would double-count the same liquid.

The reservation book reserves MasterInitialLiquidAmounts once, and serializes jar/liquid claims under one lock. A failed attempt writes nothing.

## Admission checks

1. Demand still belongs to the board and its cooldown has ended.
2. Solve-book record is Current and contains a feasible result; AwaitingRearm is not implicitly awakened.
3. The result is still current for a freshly captured roster snapshot.
4. The manifest can be reconstructed from the immutable plan.
5. Every locked jar still has a valid unique ID and is physically on the current master.
6. For B plans, the slave still exists and is empty; all transfer jars fit; required transfer liquids fit the slave's per-liquid capacity and are not banned there.
7. The reservation book can claim every jar and initial-liquid amount without conflicting with earlier admitted orders.

The method is synchronous and intended to run on the single world-logic scheduling thread. The reservation book also protects its internal check-and-write operation with a lock. No world mutation or scheduler callback should be inserted between live-state validation and TryReserve.

## Lifecycle boundary

The caller that owns the execution lifecycle must call Release(demandId) on success, cancellation, or interruption. The current phase does not yet connect this book to a running execution session; that integration belongs to a later execution-lifecycle phase.

## Deliberate non-decisions

- No active-vs-passive priority policy is selected here.
- No multi-roster or multi-combination scheduling is introduced.
- No automatic retry or rearm policy is added for continuous demands.
