# Phase 4.5b — World order execution session

## Goal

Turn an admitted immutable world plan into a time-driven execution session. The session applies the exact plan against the live master/slave workshops, performs the B-class transfer boundary, settles delivery, and releases the reservation.

This phase does not solve orders, choose scheduling priority, or automatically rearm continuous demands.

## Entry and ownership

~~~text
ResourceWorldRuntime.TryStartExecutionSession
  -> WorldOrderExecutionSession.TryStart (runtime-internal)
  -> WorldOrderExecutionAdmission.TryAdmit
  -> current WorldOrderSolveBook result + immutable WorldOrderSolvePlan
  -> WorldOrderResourceManifest
  -> WorldResourceReservationBook reservation
  -> WorldOrderExecutionSession
~~~

ResourceWorldRuntime.TryStartExecutionSession is the public entry point. Session construction, registration, and logical-time advancement stay inside the runtime; callers cannot advance a session on an independent clock.

- The demand must still be on the board and have a current feasible plan.
- The world snapshot is re-captured by admission before resources are reserved.
- The session holds the exact plan and manifest that passed admission.
- A-class sessions reserve their referenced jars and initial master free-liquid budget.
- B-class sessions additionally reserve the world's single slave role. Two B-class sessions cannot both be admitted while that role is occupied.
- Reservations are ownership declarations, not mutations of inventory and not multi-threaded world transactions. The world remains driven by a serialized logical scheduler.
- ResourceWorldRuntime may advance multiple sessions in the same logical tick. The reservation book prevents overlapping jar/liquid ownership, and B-class sessions additionally reserve the single slave role. Mark cleanup is scoped to each session's locked jar IDs, so one order cannot clear another order's operation marks. The scheduler still decides which eligible orders to start and in what priority order.

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
- MATCH is not a processing operation and consumes no execution time, but it is still applied in plan order through `WorldPlanStepApplier` and `Workshop.MarkJar`. A MATCH at the current path position is applied immediately; it is not skipped. Delivery IDs are derived from each step's requirement and its After target state: the target jar is delivered when that final state satisfies the step's RequirementShape and RequirementLiquid. This includes direct MATCH and requirements satisfied by the final POUR_IN / POUR_OUT / RESHAPE operation.
- logicalTimePerUnit is mandatory at session creation. The session does not invent a conversion from the old ProcessSystem tick to WorldClock logical time.
- The recommended ResourceWorldRuntime.TryStartExecutionSession entry checks that no workshop replacement is active before admission. ResourceWorldRuntime registers the session and advances it with the exact logical delta returned by its WorldClock. Registration verifies that the session belongs to that runtime's demand board and workshop roster. Session advancement is runtime-owned; callers cannot advance a session on an independent clock.
- When workshop replacement begins through ResourceWorldRuntime, its interruption adapter first asks the supplied external adapter to interrupt its own orders, then interrupts every still-active session registered with that runtime. A failure after the external adapter commits is treated as an uncertain interruption and is not retried.
- If interruption outcome is uncertain, the runtime latches a sticky world fault. All future world advances, new execution starts, and workshop replacement requests are rejected; the clock, demand cooldowns, resource production, and sessions remain frozen. The runtime does not guess whether external interruption side effects committed.
- A session admitted after a replacement has already begun is cancelled during registration; it cannot begin operating on the old or migrating workstations.

## Applying a step

WorldPlanStepApplier remains the single-step boundary.

- JarIndex is resolved through that step's Before[index].Id, never as a live workshop index.
- MATCH marks through Workshop.MarkJar. Legacy SolverStepApplier uses the same port.
- POUR_IN, POUR_OUT, and RESHAPE explicitly inspect the domain result's Success and Reason.
- POUR_OUT additionally checks that the returned liquid is the liquid recorded in the plan's Before state.
- A normal domain rejection stops the session and follows ordinary failure settlement. An invariant exception is cleaned up as far as possible, then rethrown; the owning runtime latches a sticky world fault because resource ownership or settlement may be uncertain.

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

- The requirement-satisfying jar IDs are first all validated on the delivery workshop; only then are they removed as delivered resources.
- For B-class orders, the remaining slave resources are returned to the master using the established SlaveRollback policy. This is the return of temporary tools and leftover liquid, not restoration of consumed resources. Unbanned free liquid is accepted up to master capacity and any remainder is discarded; banned free liquid is wholly discarded. A full jar containing a banned liquid still returns unchanged because the ban applies to free-liquid storage/use, not jar identity. If a ledger is configured, the settlement tick is fetched before clearing delivery marks or removing delivery jars; the prefetched value is reused by the subsequent rollback.
- Only jars named by this order's `LockedJarIds` have their operation marks cleared on the master/slave workshops. Unrelated jar marks are preserved.
- OrderDemandBoard.RecordSuccessfulFulfillment applies the area's OneShot/Continuous policy.
- A continuous demand becomes AwaitingRearm through the existing solve-book event; this session does not automatically restart it.
- The reservation is released.

### Ordinary execution failure

- A domain operation that cleanly rejects before mutation is an ordinary order failure. Stop immediately; do not apply later plan steps.
- Keep all successful earlier operations and consumed liquids. No snapshot rollback is performed.
- If this session crossed the transfer commit boundary, return its remaining slave resources under the established SlaveRollback policy and clear slave marks. Before that boundary, do not touch slave inventory that may have been added externally.
- Clear master marks, mark the still-attached demand Dirty, and release the reservation.
- This session does not choose the ordinary-failure CD policy. It marks the demand Dirty and releases the reservation; the upper-level execution policy must decide whether to start CD before allowing another solve. The workshop-replacement interruption contract explicitly starts CD.
- If cleanup itself throws or violates a postcondition, the result is no longer an ordinary failure. The session terminates as far as it can, rethrows, and the runtime latches a world fault.

### Workshop-replacement interruption

- If this session owns transferred slave resources, return them and clear slave marks. Do not clear or move unrelated resources found on the slave before this session's transfer commit.
- Clear master marks, start the demand's own CD, release the reservation, and enter Interrupted.
- If the demand has zero CD duration, it remains immediately eligible but is marked Dirty so it must be solved against the changed world.
- Already completed processing and consumed resources remain changed.

## Runtime fault boundary

The runtime distinguishes a recoverable order failure from a world fault:

- A normal domain rejection with successful cleanup is local to that order. The session enters Failed, its reservation is released, and the world remains operational.
- An unexpected exception during world advancement, session construction/registration, or an uncertain/partially committed workshop interruption latches the first exception as the runtime's sticky fault.
- A faulted runtime rejects every later world advance, execution start, and workshop replacement request. It does not advance the clock, demand cooldowns, resource production, or active sessions.
- The tick in which the exception occurred is not claimed to be atomic. Earlier work in that tick may already have committed; the runtime freezes afterward rather than inventing a rollback.
- There is no automatic fault reset. Recovery requires the host/operator to inspect and explicitly reconstruct or replace the world runtime; silently continuing would risk duplicating or losing resources.

## Explicit non-goals

- No active/passive scheduling priority is selected here.
- No solver invocation occurs inside the session.
- No fallback to greedy execution occurs when a plan step fails.
- No implicit rollback of completed processing occurs.
- No multi-threaded transaction is claimed.
- No main/slave workshop replacement algorithm is implemented here; the session only implements the interruption port consumed by that lifecycle.

## Tests added

- Workshop.MarkJar ownership and one-operation guard.
- Runtime advancement of a registered session using the shared WorldClock logical delta.
- A-class processing, delivery, and reservation release.
- Failure after external resource change: no later mutation, no consumed-resource restoration, reservation release, and solve-state invalidation.
- B-class single-slave reservation and replacement interruption cleanup/CD.
- Ordinary order failure remains local when cleanup succeeds; uncertain interruption and settlement callback failures latch the runtime fault.
- A settlement tick callback failure before successful delivery does not remove delivery jars; a cleanup tick failure freezes the world with unsettled slave resources preserved for inspection.
- Slave rollback returns full jars containing banned liquids unchanged, discards banned free liquid, and partially returns unbanned free liquid according to master capacity.

## Verification

Run the full local gate from the repository root:

~~~bat
重构报告\verify.bat
~~~

Verified 4.5c baseline: commit `57ef91a2fa961e0bb7911a3166df88c7b35091d9` passed the repository owner's local full `重构报告\\verify.bat` (424 xUnit tests, 0 failures, `QUALITY-OK`, `VERDICT=OK`); its GitHub Actions verify run also succeeded. This is the baseline for the 4.5d fault-boundary branch.

## Phase 4.5c — settlement completeness and observability

- `ResourceWorldRuntime` accepts an optional `ResourceLedger` together with an explicit `Func<int>` tick source. They must be supplied together. World v2 does not silently convert `WorldClock.LogicalTimeMilli` into the legacy integer process tick.
- When a ledger is supplied, B-class jar/liquid transfers are recorded as `transfer`; `SlaveRollback` records accepted resources as `return` and rejected capacity/ban overflow as `discard`.
- One rollback batch captures its tick once, so all facts in that settlement share the same caller-defined tick.
- Each session exposes its rollback discard log through `ResourceSettlementLog`. The `ResourceLedger` is the authoritative resource-quantity record for both return and discard; the session log preserves the explanatory rollback entries.
- The ledger is optional so isolated World v2 runtimes need not invent a host tick. A runtime configured with a ledger must receive the tick source explicitly.
- Regression coverage includes ordinary B-class failure after the slave transfer commit, and interruption after transfer with ledger assertions for transfer, return, and banned-liquid discard.

The strengthened 4.5c branch head is verified by the owner's 424-test local full gate and GitHub Actions; see the 4.5d section below for the separate verification status of this follow-up.


## Phase 4.5d — world execution fault boundary

- The runtime exposes `IsFaulted`, `FaultReason`, and the first `FaultException`. The first fault is sticky and is never overwritten.
- Any exception escaping a world advance latches the fault. Future advances throw before touching the clock; execution starts and workshop replacement requests are rejected.
- Exceptions during session creation/registration also latch the runtime because a partially constructed session may already have applied zero-time MATCH operations or acquired a reservation.
- An uncertain interruption result, or an interruption that committed but could not initialize replacement, latches the same runtime fault. A clean external refusal before interruption commit remains a normal rejected replacement request.
- A normal plan operation rejection with successful cleanup remains an ordinary order failure and does not fault the world.
- Successful B-class delivery prefetches the external ledger tick before any delivery mark or jar ownership mutation, then passes that fixed tick into slave settlement. This closes the callback exception window after delivery removal.
- Verified head `9a4a96109e612fb547b1b814056aa622baf6c991` passed the repository owner's full local `重构报告\\verify.bat` (426 xUnit tests, 0 failures, `QUALITY-OK`, `VERDICT=OK`) and its GitHub Actions `verify` run succeeded on the same HEAD.
