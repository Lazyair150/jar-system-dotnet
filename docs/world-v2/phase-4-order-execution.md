# Phase 4.5b — World order execution session

## Goal

Turn an admitted immutable world plan into a time-driven execution session. The session applies the exact plan against the live master/slave workshops, performs the B-class transfer boundary, settles delivery, and releases the reservation.

This phase does not solve orders, choose scheduling priority, or automatically rearm continuous demands.

## Entry and ownership

~~~text
WorldOrderExecutionSession.TryStart
  -> WorldOrderExecutionAdmission.TryAdmit
  -> current WorldOrderSolveBook result + immutable WorldOrderSolvePlan
  -> WorldOrderResourceManifest
  -> WorldResourceReservationBook reservation
  -> WorldOrderExecutionSession
~~~

TryStart is the session entry point. Callers must not construct a session from an arbitrary plan or bypass admission.

- The demand must still be on the board and have a current feasible plan.
- The world snapshot is re-captured by admission before resources are reserved.
- The session holds the exact plan and manifest that passed admission.
- A-class sessions reserve their referenced jars and initial master free-liquid budget.
- B-class sessions additionally reserve the world's single slave role. Two B-class sessions cannot both be admitted while that role is occupied.
- Reservations are ownership declarations, not mutations of inventory and not multi-threaded world transactions. The world remains driven by a serialized logical scheduler.

## State machine

~~~text
A:
  MasterSteps -> Completed
       \-> Failed

B:
  MasterSteps -> Transfer -> SlaveSteps -> Completed
       \          \             \
        \----------+-------------+-> Failed

Any active stage -- workshop replacement --> Interrupted
~~~

- Each non-MATCH plan step consumes one configured logical-time unit.
- The B-class transfer stage consumes one configured logical-time unit.
- MATCH is a delivery selection, not a processing operation. It consumes no execution time; its jar ID is retained and removed from the delivery workshop only after all required processing succeeds. This preserves the old execution contract where direct matches did not consume a process unit.
- logicalTimePerUnit is mandatory at session creation. The session does not invent a conversion from the old ProcessSystem tick to WorldClock logical time.
- ResourceWorldRuntime can register a session and advances it with the exact logical delta returned by its WorldClock. A host that does not register it may advance it directly with logical time.

## Applying a step

WorldPlanStepApplier remains the single-step boundary.

- JarIndex is resolved through that step's Before[index].Id, never as a live workshop index.
- MATCH marks through Workshop.MarkJar. Legacy SolverStepApplier uses the same port.
- POUR_IN, POUR_OUT, and RESHAPE explicitly inspect the domain result's Success and Reason.
- POUR_OUT additionally checks that the returned liquid is the liquid recorded in the plan's Before state.
- A normal domain rejection stops the session. An invariant exception after a domain mutation is cleaned up as far as possible, then rethrown to the host.

## B-class transfer

Transfer is a single preflight/commit boundary, not a loop that silently tolerates partial success.

Before moving anything, the session checks:

1. The slave is still empty.
2. Every TransferJarId exists exactly once on the master and does not already exist on the slave.
3. The slave has room for the entire jar set.
4. Every required transfer liquid is available in the master.
5. The slave does not ban any required liquid and can store the full amount.

Only after all checks pass does the session move all listed jars and exact liquid amounts. It then clears marks on the slave before applying slave-path steps. A failed preflight moves no jar and removes no liquid.

The reservation of the slave role prevents a second B-class session from entering this transfer boundary concurrently.

## Delivery and cleanup

### Success

- The MATCH jar IDs are first all validated on the delivery workshop; only then are they removed as delivered resources.
- For B-class orders, the remaining slave resources are returned to the master using the established SlaveRollback policy. This is the return of temporary tools and leftover liquid, not restoration of consumed resources.
- Both workshops have operation marks cleared.
- OrderDemandBoard.RecordSuccessfulFulfillment applies the area's OneShot/Continuous policy.
- A continuous demand becomes AwaitingRearm through the existing solve-book event; this session does not automatically restart it.
- The reservation is released.

### Ordinary execution failure

- Stop immediately; do not apply later plan steps.
- Keep all successful earlier operations and consumed liquids. No snapshot rollback is performed.
- Return any remaining slave resources under the established SlaveRollback policy, clear marks, mark the still-attached demand Dirty, and release the reservation.
- Ordinary execution failure does not automatically start CD; the existing interruption contract only specifies CD for workshop-replacement interruption.

### Workshop-replacement interruption

- Return temporary slave resources, clear marks, start the demand's own CD, release the reservation, and enter Interrupted.
- If the demand has zero CD duration, it remains immediately eligible but is marked Dirty so it must be solved against the changed world.
- Already completed processing and consumed resources remain changed.

## Explicit non-goals

- No active/passive scheduling priority is selected here.
- No solver invocation occurs inside the session.
- No fallback to greedy execution occurs when a plan step fails.
- No implicit rollback of completed processing occurs.
- No multi-threaded transaction is claimed.
- No main/slave workshop replacement algorithm is implemented here; the session only implements the interruption port required by that later lifecycle.

## Tests added

- Workshop.MarkJar ownership and one-operation guard.
- Runtime advancement of a registered session using the shared WorldClock logical delta.
- A-class processing, delivery, and reservation release.
- Failure after external resource change: no later mutation, no consumed-resource restoration, reservation release, and solve-state invalidation.
- B-class single-slave reservation and replacement interruption cleanup/CD.

## Verification

Run the full local gate from the repository root:

~~~bat
重构报告\verify.bat
~~~

No CI or local verification result is claimed until the branch is checked out and the full gate has actually completed.
