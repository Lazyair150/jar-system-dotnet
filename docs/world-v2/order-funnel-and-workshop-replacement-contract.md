# World V2 — Order funnel and workshop replacement contract

## Purpose

The funnel metaphor is the domain model for order solving:

- The order enters as a set of Jar resource requirements.
- The solver finds a complete placement and operation path for every requirement.
- A B-class order must pass the Slave-side constraints and then the Master-side constraints, including the liquid dependency between them.
- A result is feasible only when every requirement has a valid physical Jar, operation path, capacity scope, and liquid supply path.

The order does **not** prescribe a workshop assignment. It declares the required Jar outcomes. The solver discovers where and how those outcomes can be produced.

## 1. Funnel input: requirements, not a pre-authored allocation

The input is the immutable order specification:

- required Jar shape;
- optional required liquid;
- multiplicity and stable requirement order/tie-breaking policy.

It does not contain Space-side estimates such as liquid units per activation. Actual free-liquid quantities are derived from the concrete Jar operation path and then verified by the resource manifest.

Each requirement must eventually be assigned to one concrete Jar identity. A Jar can satisfy at most one terminal requirement in a given order, while its operation history may span both workshop stages.

## 2. B-class funnel stages

### Stage S — Slave filter

For a B-class order, search candidate placements that can be completed on the Slave side after transfer from Master.

The candidate must satisfy:

1. The Slave role exists and is eligible for this order.
2. Slave starts empty: no Jars and no free-liquid staging balance.
3. Its Jar capacity can hold the transferred Jar set.
4. Every assigned requirement can be reached using the Slave's base/additional marks, liquid bans, Jar states and supported operations.
5. Its liquid staging peak fits the Slave's staging capacity.
6. Its required initial free-liquid amount can be computed from the concrete Slave path.

The Slave has no independent ship-liquid input. Its only free-liquid supply for this order is the amount transferred from Master.

### Stage M — Master filter

The remaining requirements must be completed on Master. Master must satisfy:

1. Every remaining requirement has a concrete, distinct Jar assignment and executable operation path.
2. Master Jar capacity and supported marks are respected.
3. Liquid bans and actual Jar-state chemistry are respected.
4. The Master path's own initial free-liquid requirement fits the current Master prefill plus ship inventory.
5. After executing its Master path, the Master retains enough free liquid for the Slave transfer requirement.
6. Master liquid staging peak fits its own staging capacity.

The Master is not allowed to solve its own requirements while ignoring the Slave's future liquid claim. The transfer amount is a dependency of the Master path, not a post-solve guess.

### Stage T — Transfer feasibility

The Master-to-Slave transfer is one-way and exact:

- Transfer only the free-liquid quantity required to start the Slave path.
- Do not pump all remaining Master liquid.
- Do not give Slave an independent ship-inventory source.
- Transfer only the Jar identities selected by the immutable plan.
- The same Jar ID may appear in both `MasterJarIds` and `TransferJarIds` if it is processed on Master and then handed to Slave.
- `LockedJarIds` is the union of the two sets.

### Stage E — Complete solution

The funnel emits a feasible result only if all stages succeed together:

- every requirement has a terminal Jar placement;
- every operation is valid under the correct workshop rules;
- all Jars and liquid balances are accounted for;
- Master and Slave staging peaks fit;
- Master can fund both its own path and the exact Slave transfer;
- the immutable plan and resource manifest agree.

Partial placement is not a solution. A failed branch must be backtracked or rejected; it must not leak partial locks, reservations, Jar mutations or liquid movements into the live world.

## 3. Search order is not greedy commitment

"Slave first, then Master" defines the constraint funnel and the diagnostic order. It does not authorize irreversible greedy assignment.

If a Jar can serve either a Slave requirement or a Master requirement, the solver must preserve alternatives until the complete path is known. A locally valid Slave assignment may make the remaining Master requirements impossible. The search must backtrack and try another partition.

The solver may use sound prechecks to prove impossibility early. A precheck may reject only when the condition is necessarily impossible; it must not treat a lower bound or heuristic as proof.

A budget-exhausted search is Undetermined, not Infeasible. A candidate plan is Feasible only after the full plan/manifest validation.

## 4. A-class bypass

A-class orders do not enter the Slave funnel:

- no Slave demand placement;
- no Slave Jar capacity reservation;
- no Slave liquid staging account;
- no transfer event;
- all requirements must reach their terminal state on Master.

This is a true bypass, not a B-class order with an empty Slave phase.

## 5. Workshop replacement is a separate funnel using the same chemistry

A Master switch is not an order admission and does not create a Slave order phase. It is a world operation that replaces the physical workshop instance occupying the fixed Master role.

Its resource lifecycle is:

1. Validate the target as a spare workshop and interrupt/settle any active order according to the replacement contract.
2. Open a prefill transaction for the target Master, bounded by its staging capacity and funded from ship liquid inventory.
3. Run the existing Jar migration/reorganization path against the old and target workshops. Every reshape/transfer must obey the same Jar operation chemistry, workshop capabilities, bans, Jar limits and liquid capacity rules.
4. Commit the new physical workshop into the fixed Master role only after migration succeeds.
5. Commit the target's prefill transaction.
6. Return the retired Master's unused role-bound prefill to ship inventory, subject to ship capacity; overflow is recorded/discarded according to the resource ledger.

The prefill is free liquid staged for the new Master; it is not liquid already inside a Jar. Liquid inside a Jar remains owned by that Jar. The migration's transformed/transferred Jars are the physical products of the Jar rules. These two resource categories must never be conflated.

A failed or cancelled migration before role commit must not consume the old Master's state or silently retain the target prefill. After role commit, uncertain external settlement is a fault boundary, not a fictional rollback.

The existing replacement process already expresses the major commit ordering and rollback boundaries. The next implementation work is to make its migration plan and resource report use the same explicit, inspectable operation/manifest vocabulary as order execution.

## 6. Existing implementation mapping

| Funnel concept | Current World V2 component |
|---|---|
| Requirement input | `JarOrderSpecification` |
| Single-workshop operation search | `ISolver` / `StateSpaceSolver` |
| B-class candidate partitioning | `PartitionSearch` |
| Snapshot-safe workshop rules | `JarOrderSpecificationSolver.SnapshotProjectedSolver` |
| Immutable operation path | `WorldOrderSolvePlan` |
| Exact liquid/Jar accounting | `WorldOrderResourceManifest` |
| Atomic resource/capacity admission | `WorldOrderExecutionAdmission`, `OrderLiquidAllocationService`, `OrderResourceLease` |
| One-way Slave handoff | `OrderTransferService` |
| Master replacement lifecycle | `WorkshopReplacementProcess`, `WorkshopSwitchEvent`, prefill transaction |

The current implementation has these layers, but does not yet expose one unified funnel trace explaining candidate counts, rejection reasons, selected assignments, and final resource proof.

## 7. Next implementation slice

1. Add a deterministic, read-only funnel trace to the solve result: stage, requirement identity, candidate Jar IDs, rejected constraints, backtracks, and final placement.
2. Preserve solver purity: trace collection cannot mutate snapshots, reserve resources, or alter search order.
3. Add adversarial tests where a locally valid Slave choice blocks Master, requiring backtracking.
4. Add tests proving Master liquid is sufficient only after its own path and exact Slave transfer are both accounted for.
5. Add replacement tests that distinguish target prefill, Jar-contained liquid, transformed Jars, returned old prefill, and discarded overflow.
6. Expose a compact diagnostic projection to Space for balance analysis, without making Space a second solver.

No production balance values are implied by this contract.
