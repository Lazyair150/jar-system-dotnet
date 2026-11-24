# World V2 — Order funnel and workshop replacement contract

## Purpose

The funnel metaphor is the domain model for order solving:

- The order enters as a set of Jar resource requirements.
- The solver finds a complete placement and operation path for every requirement.
- A B-class order must satisfy the Slave-side constraints and the Master-side constraints, including the one-way liquid dependency between them. This is a domain constraint priority, not a claim about the current implementation's call order.
- A result is feasible only when every requirement has a valid physical Jar, operation path, capacity scope, and liquid supply path.

The order does **not** prescribe a workshop assignment. It declares the required Jar outcomes. The solver discovers where and how those outcomes can be produced.

## 0. Canonical rule/world/funnel boundary

The solver is only meaningful when its vocabulary and world are explicit. The intended dependency direction is:

1. **Rule definitions** describe stable operators and constraints: applicability, preconditions, pure Jar after-state, free-liquid effects, Jar-liquid effects, and stable rule identity/version. A rule definition does not own a Workshop, Jar instance, inventory, or order.
2. **Rule context** is the effective rule set at one world instant: immutable workshop capability plus active modifiers. Runtime Suppress/Grant changes create a new rule fingerprint. Opaque external predicates are not guessed; detached solving reports Undetermined.
3. **World/profile** supplies the environment in which rules run: workshop role, marks, bans, capacities and resource-container boundaries. A Workshop owns these properties and its live Jar/liquid containers; it is not the authority that redefines operator semantics.
4. **Order specification** states only terminal Jar facts and multiplicity. It does not carry precomputed Master/Slave assignments, estimated liquid costs, Jar IDs or execution steps.
5. **Funnel solver** applies rule operators to derive candidate paths and placements. It does not mutate the world. A resource binder then matches those paths to current Jar identities and liquid accounts; admission creates the resource lease only after the complete proof succeeds.

Order solving and workshop replacement share the rule vocabulary but run in opposite directions:

- **Order solving is goal-directed/reverse:** begin with the required final Jar facts, enumerate rule operations that can produce them, then filter backwards to legal source Jar facts and required resource flows. The B-class role partition and one-way transfer constraints are part of this search, not fields supplied by the order.
- **Workshop replacement is resource-directed/forward:** begin with the old workshop's actual Jar/liquid resources and the target workshop's properties, then apply permitted transformations to obtain the best valid target state. It is not an order solve and does not create an order allocation.

The cache boundary follows these inputs:

- Compiled rule candidates are keyed by `RuleSetVersion + WorkshopProfileVersion + SpecificationVersion`; resource-only changes retain them.
- Concrete resource binding is keyed by `ResourceVersion` and produces a plan containing actual Jar identities and liquid requirements. If only `ResourceVersion` changes, `WorldOrderSolveBook` may rebind the same immutable plan to the new WorldVersion only after checking every referenced Jar's initial facts, exact liquid supply, and staging/role capacities. If any used Jar changed/disappeared or supply is insufficient, it falls back to the full solver. It never reuses a plan across rule, workshop-profile or specification changes.
- Execution admission is bound to the complete `WorldVersion`; the resource manifest is revalidated before any lock, reservation or mutation.
- Snapshots capture authoritative world facts and version fingerprints, not compiled solver caches. A cache miss must be reproducible from the same snapshot inputs.

This separation is the intended simplification target: changing a Jar balance should not rebuild rule definitions or the order's rule candidate space; changing a rule or workshop profile must rebuild the affected candidate space; changing the active resource arrangement first attempts a guarded plan rebind, and only a failed rebind invokes the full solver. A cached Jar-ID plan is never treated as executable merely because its rule/profile fingerprint still matches.

## 1. Funnel input: requirements, not a pre-authored allocation

The input is the immutable order specification:

- required Jar shape;
- optional required liquid;
- multiplicity and stable requirement order/tie-breaking policy.

It does not contain Space-side estimates such as liquid units per activation. Actual free-liquid quantities are derived from the concrete Jar operation path and then verified by the resource manifest.

Each requirement must eventually be assigned to one concrete Jar identity. A Jar can satisfy at most one terminal requirement in a given order, while its operation history may span both workshop stages.

## 2. B-class role assignment and execution stages (current contract)

A B-class order uses both workshop roles as a one-way resource circuit. The order does not pre-assign requirements. The solver evaluates each requirement against both exact role profiles and assigns it to exactly one role.

The current bounded enumerator keeps a **Slave-first candidate preference** for compatibility with established B-class paths. It tries Master candidates when Slave has no candidate or the Slave-first branch cannot bind the required physical resources. This preference is not a greedy commitment: alternatives remain searchable until a complete concrete plan is assembled.

A valid B-class plan must contain at least one concrete Slave demand operation. This preserves the B-class Slave-use event. Master may complete some requirements, but a pure Jar-only handoff with no Slave demand operation is not a B-class completion.

### Stage M — Master execution before handoff

- Execute every requirement operation assigned to Master before the handoff.
- Master preparation resources may fund Master operations and may produce the exact free-liquid amount needed by later Slave operations.
- Master staging Ban/capacity applies to its own free-liquid account; Jar-internal liquid operations still require Master-Jar polarity compatibility.
- The Master must retain the exact liquid balance required for the planned Slave transfer after its own operations.
- Master-completed requirement Jars remain valid handoff resources; Slave does not repeat their operations.

### Stage T — One-way Master-to-Slave handoff

- Transfer exact Jar identities selected by the immutable plan and the exact free-liquid amount required by Slave.
- Jar handoff changes only the owning inventory account. It preserves Jar ID, shape, internal liquid, derived polarity and Mark.
- Handoff does not test source or destination Workshop polarity and does not require the receiver to be able to operate the Jar.
- Free-liquid transfer is different: source staging Ban blocks extraction; destination staging Ban/capacity blocks admission. The Master cannot transfer a liquid banned by its staging rules.
- Slave begins without independent Jar or free-liquid order input. It receives only the permitted one-way resources from Master.

### Stage S — Slave execution after handoff

- Execute only the requirements assigned to Slave, plus any selected Slave preparation operations.
- A Master-completed requirement Jar is held/handed off but is not operated again by Slave.
- Every actual Slave Jar operation checks Slave polarity compatibility at the operation boundary.
- Slave preparation can fund only Slave-side needs; Master preparation may fund Slave only through the explicit liquid handoff.
- Slave staging peak and Jar capacity are validated against the exact concrete plan.

### Stage E — Complete solution

The funnel emits Feasible only when all stages succeed together:

- every requirement appears exactly once across Master and Slave steps;
- at least one requirement operation is assigned to Slave;
- Master operations precede handoff and Slave operations follow handoff;
- all Jar identities, liquid balances, capacities and one-way transfers are accounted for;
- each operation is valid under the role that actually executes it;
- the immutable plan, ordered resource replay and resource manifest agree.

Partial placement is not a solution. A failed branch must be backtracked or rejected; it must not leak partial locks, reservations, Jar mutations or liquid movements into the live world.

## 3. Search order is not greedy commitment

Slave-first describes candidate preference and compatibility with existing B-class plans; it does not mean the entire order executes on Slave, nor does it prohibit Master from completing requirements.

If a Jar/requirement can be fulfilled by either role, the solver must preserve both alternatives until a complete concrete path is known. A locally valid Slave assignment may make later resource binding impossible; the search must backtrack and try a Master assignment where allowed. Conversely, an opaque role profile cannot be treated as an empty candidate set: a known feasible branch in the other role may still be returned Feasible, but if no known branch works the overall result remains Undetermined rather than falsely Infeasible.

The solver may use sound prechecks to prove impossibility early. A precheck may reject only when the condition is necessarily impossible; it must not treat a lower bound or heuristic as proof.

A budget-exhausted search is Undetermined, not Infeasible. A candidate plan is Feasible only after full plan/manifest validation.

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

1. Finish the rule authority boundary. The operator catalog currently supplies stable metadata, while evaluation/transition dispatch remains built-in code. Separate stable Jar operators from runtime rule constraints (Suppress/Grant/Require and future declarative predicates); runtime constraints must participate in `RuleSetVersion`, and an opaque predicate must remain `Undetermined`.
2. Expand the resource-independent layer from one-step candidates to explicit symbolic operation programs. Include preparation Jar selection, operation order, per-role resource flow and staging peaks. The current `EnumerationComplete` status is deliberately limited to one-step candidate combinations; it must not be promoted to a complete-order proof.
3. Complete the guarded resource-only rebind path with adversarial coverage for same-ID Jar state mutation, Jar removal/replacement, liquid depletion, capacity changes and B-class transfer Jar state. Reuse is allowed only when the old immutable plan is still physically valid.
4. Generalize the funnel result from first-feasible partition to a bounded set of complete alternatives where useful, without folding A/B allocation policy into rule definitions. Preserve exact B-class partition/backtracking, the one-way Master-to-Slave dependency and explicit budget/Undetermined semantics.
5. Keep the deterministic read-only trace and extend it to the complete symbolic-program and resource-binding stages; trace must never change branch ordering, Jar locks, liquid balances or snapshot contents.
6. Connect Master replacement as a forward resource-transformation problem using the same rule contract and a migration plan/manifest, then expose a compact diagnostic projection to Space.
7. Audit snapshot/checkpoint responsibilities after the new cache boundary is established. Persist authoritative world state and active execution leases; do not persist/reconstruct compiled solver caches as if they were world state.

No production balance values are implied by this contract.