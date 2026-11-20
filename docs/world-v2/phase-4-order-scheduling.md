# Phase 4.5e — Order scheduling coordination

## Purpose

Connect the existing demand board, solve book, solver registry, execution admission, and runtime without moving their responsibilities into one large manager.

This phase introduces the orchestration boundary only. It does not change solver rules, resource-manifest rules, reservation semantics, execution steps, cooldown duration, or fulfillment policy.

## Existing ownership

| Component | Owns | Does not own |
|---|---|---|
| `OrderDemandBoard` | Active/passive membership, passive list order, cooldown progression, one-shot/continuous fulfillment | Solving, resource reservation, execution |
| `WorldOrderSolveBook` | Per-demand Dirty/Current/NotReady/Detached state and cached result; cooldown is an eligibility gate | Choosing which order starts |
| `WorldOrderSolverRegistry` | Dispatching a demand specification to its solver | Scheduling or resource reservation |
| `WorldOrderExecutionAdmission` | Fresh snapshot validation, manifest validation, atomic resource reservation | Execution time or order priority |
| `WorldOrderExecutionSession` | Applying one immutable admitted plan and settling its resources | Re-solving or selecting another plan |
| `ResourceWorldRuntime` | Shared logical clock, resource production, cooldown advancement, replacement, and registered sessions | Choosing the next demand |
| `WorldOrderScheduler` | Calling the above in a defined order for one scheduling pass | Owning world time or inventing domain rules |

## Scheduling pass

The host calls `RunSchedulingPass()` after a world-advance step and after relevant user/board changes.

1. Capture one immutable world snapshot.
2. Evaluate all attached demands against that same snapshot. The solve book reuses a cached result when its content version still matches; it invokes the solver only when the record is Dirty, eligible after cooldown, or stale against the snapshot.
3. Visit passive demands in the exact current order of `OrderDemandBoard.Passive.Demands`.
4. For each feasible passive demand, call the Runtime's execution-start entry. Admission recaptures the live snapshot, validates the current plan, and reserves resources before a session can begin.
5. A clean admission refusal does not block later passive demands. This allows a later demand to start when an earlier demand is currently infeasible or conflicts with already reserved resources.
6. After a successful start, capture a fresh snapshot before considering the next passive demand. Session construction may immediately apply zero-time MATCH operations and change jar marks or even complete an order; a later demand must not use a stale solve result.
7. At the end, refresh the solve records against the final snapshot so the pass report does not present earlier results as current after immediate world mutations.

A pass can start multiple passive sessions if their resource reservations do not overlap. The reservation book remains the authority for conflicts; the scheduler does not duplicate resource ownership logic.

## Active and passive behavior

- Both areas use the same solver registry, snapshot, solve book, admission, manifest, reservation, and execution-session rules.
- A scheduling pass solves active demands but never starts them automatically.
- An active demand starts only through the explicit `TryStartActiveDemand(demandId)` request. The caller/user chooses which active demand to start.
- Passive demands are automatic candidates and are considered in their user-defined list order.
- The first passive candidate is not an unconditional blocker: if it is not currently feasible or admission refuses it, the scheduler can continue to the next passive candidate.
- This phase does not establish a global priority between automatic passive work and a user-issued active-start request. Their interaction is determined by when the host submits the explicit request relative to a scheduling pass; a future policy layer may arbitrate this if the product requires a different rule.

## Trigger and cost model

The scheduler is deliberately called by the host; it does not create another clock, timer, thread, or frame loop.

The scheduler provides a canonical combined entry point:

~~~csharp
var result = scheduler.AdvanceWorldAndSchedule(deltaWorldTimeMilli);
~~~

It delegates time advancement to `ResourceWorldRuntime.AdvanceWorldTime` and then invokes `RunSchedulingPass` exactly once. The scheduler does not own or calculate time; it only prevents the host from accidentally omitting or reversing the required sequence.

The host may still call `RunSchedulingPass()` directly after a board/user change that should be reflected immediately without advancing world time. Do not call both the combined entry point and a second pass for the same world step unless a separate board mutation occurred between them. Repeated calls do not imply repeated DFS: the solve book caches results by the immutable content fingerprint, while CD expiry and board changes already mark affected records Dirty through existing events.

World snapshot versioning covers the current solve inputs on the master/slave workshops. A producer changing warehouse stock alone does not invalidate a solve result until that stock changes a workshop solve input; the warehouse itself is not a solver candidate pool.

## Continuous demands and cooldown

A successful continuous demand remains in its original area and starts its own configured logical-time cooldown. While the cooldown is positive, its solve-book state is `NotReady`; scheduling and world-version invalidation cannot bypass this gate. When the cooldown expires, the demand-board event automatically marks it `Dirty` so the normal scheduling pass can solve it again.

A zero-duration cooldown creates no waiting period: the retained demand becomes `Dirty` immediately. A passive continuous demand can then be selected by the existing passive list order; an active continuous demand is re-solved but still requires an explicit `TryStartActiveDemand` request.

Ordinary execution failure does not start cooldown and is re-evaluated against the current world. Workshop-replacement interruption continues to start the demand's own cooldown. One-shot success removes the demand and does not start cooldown.

## Fault behavior

- A faulted Runtime rejects the scheduling pass before solving or starting work.
- Ordinary infeasibility and clean admission refusal are per-demand outcomes, not world faults.
- Exceptions caused by Runtime execution startup retain the 4.5d sticky-fault behavior.
- Solver exceptions are not silently converted into infeasibility.

## Verification targets

- Passive list order wins a shared-resource conflict.
- An infeasible earlier passive demand does not prevent a later feasible demand from starting.
- Active demands are solved but not auto-started.
- An explicit active-start request uses the same execution pipeline.
- A passive demand cannot be started through the active-request entry.
- Continuous success starts the configured demand cooldown; CD expiry returns it to Dirty and the passive/active launch rules remain distinct.
- Global and per-demand invalidation cannot bypass a running cooldown.
- Ordinary failure does not start cooldown; workshop-replacement interruption still does.
- Existing solve-book cache, snapshot invalidation, resource admission, and multi-session reservation tests remain valid.

## Explicit non-goals

- No active-vs-passive global priority policy.
- No separate rearm manager or resource-change wake-up policy; the demand's own cooldown defines the next eligible cycle.
- No multi-threaded scheduler or concurrent solver execution.
- No independent scheduling clock or unconditional per-frame solver search.
- No change to the single master/slave workshop-role model.

## Combined world-step boundary (4.5f)

- `AdvanceWorldAndSchedule(deltaWorldTimeMilli)` delegates one world-time advance to Runtime, then runs one scheduling pass.
- A Runtime advance exception propagates and prevents the scheduling pass; the 4.5d sticky-fault behavior remains authoritative.
- A faulted Runtime is rejected by `RunSchedulingPass()`; no order is solved or started.
- Direct board/user changes can still request a scheduling-only pass without advancing time.
- This is a convenience/orchestration boundary, not a new clock, timer, background loop, or automatic event subscription.

## Verification status

- Owner's local `重构报告\\verify.bat`: passed; xUnit 430, Errors 0, Failed 0, Skipped 0; final `VERDICT=OK`.
- GitHub Actions `verify` for HEAD `5f70fa41a07e73070798466b261396ce4b8a3256`: completed successfully (run #422).
- PR #13 remains Draft and unmerged.
