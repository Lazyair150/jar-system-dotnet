# World V2 — Order funnel and workshop replacement contract

## Purpose

The funnel metaphor is the domain model for order solving:

- The order enters as a set of Jar resource requirements.
- The solver finds a complete placement and operation path for every requirement.
- A B-class order must satisfy the Slave-side constraints and the Master-side constraints, including the one-way liquid dependency between them. This is a domain constraint priority, not a claim about the current implementation's call order.
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

### Stage S — Slave feasibility constraints

For a B-class order, the final partition must prove that the requirements assigned to Slave can be completed after transfer from Master. Slave constraints are a required part of the funnel; they are not an irreversible greedy pre-assignment.

The current implementation enumerates requirement partitions. For each not-yet-pruned partition it solves the Master subset first, obtains the Master's actual terminal Jar/liquid state, then solves the Slave subset against the transferable resources. Therefore the current algorithm is not literally Slave-first. The domain model may use Slave constraints to describe the restrictive funnel, but implementation order and domain priority must remain separately documented.

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

The current implementation has these layers, but does not yet expose one unified funnel trace explaining candidate counts, rejection reasons, selected assignments, and final resource proof. In particular, PartitionSearch returns the first feasible partition rather than enumerating the complete solution set. Its current master-subset monotonicity pruning and duplicate-multiset partition elimination are search optimizations, not a general rule engine.

A first resource-independent rule-candidate layer is now present:

- `WorkshopRulebook.Definitions` is currently a stable **operator metadata catalog** (rule ID, semantic version, operator name and category). It covers four Jar transformations plus `MATCH`, and its identity participates in `RuleSetVersion`. It is not yet an executable/plugin registry: evaluation and transition construction still have built-in dispatch code.
- `WorkshopRulebook.EvaluateMatchTransition` makes direct matching a first-class pure transition. Live `Workshop.MarkJar`, `StateSpaceSolver`, symbolic candidate projection and the greedy fallback honor the same MATCH suppression/Require rule instead of independently marking a Jar.
- Live `Workshop` operations apply the rule engine's `After` facts through one generic Jar transition applier. The Workshop retains ownership of its resource transaction (consume/receive) and Jar membership; it no longer reinterprets each operation to mutate Jar shape/liquid/mark.
- The four chemical operations now share one `EvaluateCore` across ordinary per-liquid inventory and order-scoped staging. The two resource boundaries still differ: ordinary inventory uses per-liquid `MaxLiquids`; order staging uses aggregate capacity plus the current order's isolated balance. Their Jar transition proof must remain identical whenever both contexts permit the operation.
- `WorldOrderRuleCandidateCatalog` projects each requirement into legal one-operation candidates using the snapshot's rule context. It also records generic `POUR_OUT` preparation operations that can produce free liquid for later steps.
- Candidate evaluation uses the World V2 `OrderLiquidScope` contract. It does not use the legacy per-liquid `MaxLiquids` account as a substitute for total staging capacity.
- `WorldOrderRuleCandidateBook` caches profiles by rule-set version, workshop-profile version, order-specification fingerprint and role. Resource-only changes do not invalidate these profiles.
- `WorldOrderRuleCandidateProfile.PreparatoryOperations` separately records rule-valid `POUR_OUT` operations that may produce liquid for later operations. These are possibilities, not an assertion that a suitable Jar exists.
- `WorldOrderSymbolicSolutionBuilder` enumerates bounded combinations of one-step requirement candidates. Its liquid totals are gross consumption/production summaries, not net external demand or a staging-capacity proof. `WorldOrderSolveBook` refreshes this derived set when an order is evaluated under a changed rule/profile input.
- B-class `PartitionSearch` now emits a deterministic, read-only `PartitionSearchReport` alongside its unchanged selected outcome. It records masks/unique partitions, Master/Slave solve counts, monotonicity and duplicate pruning, capacity rejections, selected partition and Undetermined exits. The report is diagnostic only; it does not influence search order, branch decisions, Jar locks or liquid balances.
- A solver that says `Feasible` but omits its path is treated as `Undetermined`, not as a proven infeasible partition. This preserves the distinction between a search proof and a broken solver contract.
- Opaque rules and enumeration-budget exhaustion remain `Undetermined`. The status `EnumerationComplete` means only that this one-step candidate-combination layer was exhausted; `NoDirectCandidate` means no direct candidate was found in this projection and is not proof that the full order is infeasible.
- Snapshot fingerprints deliberately separate `RuleSetVersion`, `WorkshopProfileVersion` and `ResourceVersion`. Static rule candidates depend on the first two plus the order specification; resource-only changes retain that compiled candidate set. A concrete `WorldOrderSolveResult` still binds a full `WorldVersion`, because its Jar IDs, operation path, liquid requirements and terminal states are resource-specific.
- Runtime `Suppress`/`Grant` changes are rule changes and must alter `RuleSetVersion`; capability/role/capacity changes alter `WorkshopProfileVersion`; Jar/liquid/prefill changes alter `ResourceVersion`. A `Require` backed by an arbitrary closure is intentionally opaque to a detached snapshot and therefore yields `Undetermined`.
- B-class terminal state is a paired proof: `FinalJars` and `FinalLiquids` must both be present, or both be available from the final path step. The search must never combine post-operation Jars with pre-operation liquid balances. Partition enumeration is bounded by the domain maximum of 10 requirements (the subset-pruning cost is exponential beyond that bound).

**Important boundary:** this is not yet the complete symbolic execution-plan set. The candidate layer deliberately has no concrete Jar identities, does not prove distinct-Jar allocation, does not schedule preparation operations against staging peaks, and does not prove Master-to-Slave transfer. The authoritative `StateSpaceSolver` / `PartitionSearch` still performs those resource-bound checks and produces the executable plan. The new layer is a reusable rule projection, not a replacement solver and not an execution reservation.

## 7. Next implementation slice

The next work is ordered by semantic dependency. A trace is useful, but it must explain one authoritative rule engine rather than make the current duplicated rules more observable.

1. Finish the shared rule decision/transition contract by making the operator registry authoritative rather than metadata-only. Built-in MATCH and four Jar transformations now share stable identity/version, decision, pure Jar after-state and resource effects across live Workshop and solver execution. A future new operator must register its stable identity/version and supply one evaluator/transition implementation; it must not add parallel switches to Workshop, solver and candidate projection.
2. Expand the resource-independent layer from one-step candidates to explicit symbolic operation programs, including bounded preparation sequences and their liquid-flow/capacity constraints. Do not call the current candidate combinations complete order solutions.
3. Add version-aware access to symbolic profiles: rule/profile/specification changes rebuild them; resource-only changes retain them while the concrete binding/feasibility solve is refreshed.
4. Generalize the funnel search kernel without folding A/B allocation policy into rule definitions. Preserve exact B-class partition/backtracking and one-way Master-to-Slave transfer semantics.
5. Add deterministic, read-only funnel trace and adversarial tests for backtracking, capacity, resource-source proof, and complete-vs-undetermined outcomes.
6. Connect Master replacement as a forward resource-transformation problem using the same rule contract, then expose a compact diagnostic projection to Space.

No production balance values are implied by this contract.