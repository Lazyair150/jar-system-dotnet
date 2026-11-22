# 4.10b 9l — Actual order-solver integration audit

## Status

This change is an integration-boundary audit, not a claim that the symbolic branch model now drives executable orders. The current B-class execution path remains the legacy `PartitionSearch` path until a concrete resource binder and plan materializer are implemented and verified end to end.

Base: 9k multi-Jar operation ordering.

## Verified current call path

`WorldOrderSolverRegistry.Solve`
→ `JarOrderSpecificationSolver.Solve`
→ A-class: `SnapshotProjectedSolver.Solve` → `ISolver.Solve` → `WorldOrderSolvePlan.SinglePhase`
→ B-class: `PartitionSearch.Search` → `WorldOrderSolvePlan.Partitioned`.

The B-class method still reports `CheckMethod = "partition"`. This is the actual executable-plan source today.

Separately, `WorldOrderSolveBook.Evaluate` calls `CacheRuleCandidates` before invoking the registry. That method builds and caches Master/Slave `WorldOrderRuleCandidateProfile` instances and a `WorldOrderSymbolicSolutionSet`. The cache is analysis-side data; the registry/solver does not consume it to construct the returned plan.

## Why the 9f–9k output cannot be copied into a plan

The existing layers intentionally describe different things:

1. `WorldOrderRuleCandidateProfile` contains rule-legal single-operation candidates. Its Jar source shape/liquid are candidate facts, not physical Jar identity or current ownership.
2. `WorldOrderSymbolicSolutionSet` combines one candidate per requirement across roles. Its own contract says it is not resource-bound and carries no Jar IDs.
3. `WorldOrderSlaveRuleBranchEnumerator` expands Slave candidate alternatives, independent preparation Jars, and distinct-Jar operation schedules. It produces OR alternatives of boundary AND groups.
4. `WorldOrderResourceRequirementGroup` describes the one Master→Slave boundary. `SlaveOperationOrder` is a symbolic node-ID witness, not a concrete `SolverStep` sequence.
5. `WorldOrderSolvePlan` requires concrete before/after Jar snapshots and operation Jar indexes/IDs. Its constructor enforces one operation per physical Jar across the complete Master+Slave plan.
6. `WorldOrderResourceManifest.Create` replays the concrete plan against the snapshot, derives exact Jar locks and liquid requirements, checks the one-time transfer and staging peaks, and is the admission input.

Therefore, turning a boundary group directly into a `WorldOrderSolvePlan` would be unsound: the group does not identify which physical unmarked Jar satisfies each JarState, does not prove there are enough distinct Jar identities, and does not contain the Master-side concrete production path.

## Integration contract

The new path must be a concrete planning pipeline, not another parallel report/cache:

1. **Profile acquisition** — reuse profiles only when rule-set, workshop-profile, and specification fingerprints match. Resource changes may reuse rule candidates, but never a previously bound Jar assignment without revalidation.
2. **Candidate-to-physical-Jar binding** — bind each candidate to a real Jar in the order's initial Master inventory. Match exact shape/internal-liquid state, stable ID, ownership, and `IsMarked == false`. Reserve each physical ID at most once across all requirements and preparation operations, including both roles.
3. **Operation selection** — retain the exact rule candidate and rule version selected for each bound Jar. Do not silently fall back to a different operation after the resource schedule is chosen.
4. **Concrete schedule materialization** — translate the symbolic node schedule into concrete ordered operations and before/after snapshots. The symbolic Jar key → physical Jar ID mapping must be explicit and immutable.
5. **Master-side execution proof** — prove all Master operations happen before the single handoff, apply consume-before-produce staging semantics, and produce every Jar/free-liquid resource required at the boundary. Master staging ban applies only to free liquid in Master staging; Jar-internal liquid remains independent.
6. **Single atomic handoff** — transfer the complete set of Jars that Slave will operate, including independent preparation Jars, plus only the required free-liquid staging vector. No Slave initial inventory, no second handoff, and no reverse transfer.
7. **Slave-side replay** — execute the exact retained `SlaveOperationOrder` after handoff. Every output liquid first enters Slave staging; rejection/overflow discards it. The one-operation-per-physical-Jar order mark survives revisions and handoff.
8. **Plan and manifest validation** — construct the existing immutable `WorldOrderSolvePlan`; then require `WorldOrderResourceManifest.Create` to validate the complete plan against the same snapshot before it can be reported Feasible.
9. **Budget semantics** — incomplete candidate, binding, or schedule enumeration is `Undetermined`; never turn a partial set of alternatives into `Infeasible` or `Complete`.
10. **Actual entry-point tests** — exercise `WorldOrderSolverRegistry.Solve` / `JarOrderSpecificationSolver.Solve`, then manifest creation and execution admission. Enumerator-only tests are not integration proof.

## Required separation of responsibilities

- The symbolic enumerator chooses legal rule branches and records boundary conditions/schedule witnesses.
- The concrete binder allocates physical Jar identities and enforces the order-level operated mark.
- The materializer creates the exact executable path and the one handoff.
- The manifest is the final resource proof; it does not repair an incomplete symbolic plan.
- The admission service remains the only point that reserves live resources.

Do not merge these responsibilities into `WorldOrderSolveBook`. The solve book may cache rule profiles, but it must not become a second planner or resource reservation authority.

## Non-negotiable regression cases

- A physical Jar can appear in at most one operation across Master and Slave, regardless of revision or handoff.
- A preparation Jar is distinct from every requirement Jar and every other preparation Jar.
- A high-index producer may need to precede a low-index consumer; the selected schedule must survive into the concrete plan.
- Producer-first and consumer-first schedules may require different boundary free-liquid amounts.
- Master ban rejects only banned free liquid that must cross the staging boundary; a Jar containing that liquid remains eligible for JarState handoff.
- Slave-local produced liquid may satisfy a later Slave operation only when it was accepted into staging.
- Slave reject and total staging capacity apply to free-liquid staging only.
- One B-class order has exactly one Master→Slave batch handoff and starts with an empty Slave.
- A-class plans have no Slave resources or handoff.
- A completed symbolic alternative set is not proof of physical Jar availability, free-liquid balance, or execution admission.

## Next implementation slice

Implement a dedicated concrete binder/materializer with explicit inputs:

- immutable `WorldSnapshot`;
- selected rule candidate branch and its schedule witness;
- actual Master Jar inventory and initial order-level marks;
- Master/Slave staging capacities and policies;
- Master liquid supply/prefill accounts.

Its output must be either a fully materialized `WorldOrderSolvePlan` plus a successful manifest validation, or a typed `Infeasible` / `Undetermined` result. Only after that path exists should `JarOrderSpecificationSolver` switch its B-class primary path. Keep `PartitionSearch` as a differential oracle during rollout; do not delete it until the new path has parity coverage for supported cases.

## Explicitly not changed

- No change to current solver behavior or feasibility claims.
- No merge of Master/Slave liquid accounts.
- No change to Jar internal-liquid vs free-liquid staging semantics.
- No relaxation of the one-operation-per-physical-Jar rule.
- No claim that PR #45 or its base PR #44 is merged.
