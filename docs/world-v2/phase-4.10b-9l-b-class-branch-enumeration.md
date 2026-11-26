# Phase 4.10b 9l — B-class Slave-owned demand enumeration

## Domain boundary

B-class requirements are decomposed exclusively under the Slave rule profile. The Master is not a second requirement-solving role: it is a provider for the free-liquid boundary extracted from the Slave's candidate execution branch.

The Slave may use:
- Jars received in the one Master→Slave handoff;
- free liquid received in that same handoff;
- free liquid produced by its own operations, but only after the output is accepted into Slave staging.

The Master may perform only legal preparatory operations needed to produce free liquid demanded by the Slave branch. Master-produced output must first be accepted into Master staging. Only the minimum required free-liquid balance crosses the handoff. No branch is sent back to Master for recursive requirement solving.

## Enumeration

1. Build/cache Master and Slave rule profiles independently.
2. Filter both profiles' candidate Jar states against distinct physical Jars in the initial Master inventory.
3. Enumerate final requirement candidates from the Slave profile only.
4. Enumerate optional Master preparation operations that can produce a liquid required by the Slave branch.
5. Enumerate optional Slave preparation operations that can produce a liquid required by a later Slave step.
6. Enumerate Master preparation order and bounded Slave staging-affecting operation order.
7. Bind every symbolic Jar identity to a distinct physical Jar ID.
8. Perform exactly one atomic Master→Slave handoff.
9. Replay Master and Slave staging independently in actual operation order.
10. Materialize concrete steps and freeze an immutable plan; report Feasible only after Manifest validation against the same WorldSnapshot and replay witness.

The Master profile's requirement candidates are deliberately excluded from the symbolic demand solution set. The Master profile contributes only its preparatory-operation candidates. The Slave profile owns every final requirement step.

## Opaque Master rules

Slave rule candidates are mandatory because every final B-class requirement is solved in the Slave environment. An opaque Master rule profile does not by itself block a branch that performs no Master operation: Jar handoff is resource movement, not a Jar operation. If a Slave branch consumes free liquid and Master-side preparation may be needed, an opaque Master profile prevents proving that no legal preparation exists; absent another concrete feasible branch, the result is Undetermined rather than Infeasible.

## Physical preparation multiplicity

A rule profile contains one preparation candidate per source Jar state; it is not an inventory list and must not be mistaken for one physical Jar. During world-bound enumeration, each preparation candidate is expanded to the count of distinct, unmarked physical Jars in the initial Master inventory with that exact source state. This permits multiple identical Jars to independently produce the same liquid while preserving one-operation-per-physical-Jar. The concrete binder remains the final authority and rejects any assignment that attempts to reuse a physical Jar.

When only preparation multiplicity changes, the cached Slave requirement solution set remains reusable because its ordered requirement candidate lists are unchanged; physical preparation expansion does not alter the requirement Cartesian product.

## Physical ownership and operation mark

All physical Jars originate in the initial Master inventory. Slave inventory must be empty at B-class admission; it cannot be used as a second source. The single handoff contains every Jar needed by Slave requirement and preparation steps.

Each physical Jar can be operated at most once across the whole order. Revision changes and handoff do not clear the order-level operation mark. Distinct symbolic Jar identities must bind to distinct physical Jar IDs.

## Ban and transfer semantics

- Master ban blocks Master operations on Jars containing that liquid and blocks banned free liquid from Master staging storage/use/handoff.
- A JarState handoff is physical resource movement. Its internal liquid is not reclassified as staging liquid and is not rejected by Master ban.
- Slave ban blocks Slave operations on Jars containing that liquid and blocks banned free liquid from entering/remaining in Slave staging. Explicit Slave reject policy is additive.
- Thus a full Jar containing a Master-banned liquid may cross intact and be self-consumed by a Slave that permits that liquid.
- Master preparation output is eligible for handoff only after it has been accepted into Master staging. Slave-local output can satisfy only later Slave steps after successful staging acceptance.
- Every operation consumes free liquid before producing it. Banned output and capacity overflow are discarded under the owning workshop's staging policy.

## Feasibility and budgets

A branch is Feasible only after candidate validation, physical Jar binding, operation materialization, ordered staging replay, the one-way handoff proof, immutable plan freezing, and Manifest validation all succeed.

Opaque rule input or exhausted search budgets produce Undetermined, never Infeasible. The legacy `PartitionSearch` implementation is extracted as `WorldOrderPartitionBClassOracle`; it is callable only through `SolvePartitionOracleForDifferentialTesting`. It remains useful for targeted overlapping comparisons but is not authoritative where its historical requirement partitioning differs from the Slave-owned B-class contract.

## Production entry point

`JarOrderSpecificationSolver.Solve` now routes B-class orders through `WorldOrderBClassBranchEnumerator`. A successful concrete assembly returns `9l_concrete_plan`; enumeration-level Infeasible/Undetermined results are labeled `9l_concrete_b`. B-class still requires both roles and an empty initial Slave inventory.


- B-class input invariant: Slave begins with no pre-existing Jar or free-liquid inventory; the only Slave-side inputs are the single atomic handoff from Master and resources explicitly permitted by the current execution model.
