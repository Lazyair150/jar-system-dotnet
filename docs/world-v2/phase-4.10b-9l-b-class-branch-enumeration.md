# Phase 4.10b 9l — B-class concrete branch enumeration

## Scope

This slice adds `WorldOrderBClassBranchEnumerator` as a concrete branch/materialization path. It does **not** switch `JarOrderSpecificationSolver` from `PartitionSearch`; the legacy path remains the differential oracle until parity and entry-point tests are complete.

## Branch model

For each final requirement, the combined Master/Slave rule profiles contribute OR candidates. A complete symbolic solution chooses exactly one candidate and one role per requirement. The enumerator then considers independent `PREPARE_POUR_OUT` Jars on both roles and bounded schedules for all staging-affecting operations on each role.

The Master schedule runs before handoff. The single handoff moves every physical Jar that the Slave will operate **and every completed Master requirement Jar** so the B-class output is collected on the Slave. Master-operated output Jars cross with their order-level mark intact and are never scheduled for a second operation. The Slave schedule runs after that handoff. Jar-only operations are placed in a stable tail because they do not alter staging feasibility.

## Physical ownership and operation mark

All symbolic root Jars are bound to distinct, unmarked physical IDs in the initial Master inventory. Slave profiles describe Slave rules, not a second inventory source. The Slave's pre-existing Jar and free-liquid inventories must be empty for a B-class solve; its resources come only from this order's one handoff. The one handoff includes Master-completed output Jars as well as Slave operation/preparation Jars. The binder rejects missing IDs, marked Jars, duplicate IDs, state mismatches, and insufficient distinct physical Jars.

Each physical Jar can be operated at most once across the whole order. A revision change or handoff does not clear its order-level mark. The handoff is a single Master→Slave batch and cannot be repeated or reversed.

## Ban and transfer semantics

- Master ban governs Master Jar operations, Master staging storage/use, and Master free-liquid handoff.
- A JarState handoff is physical resource movement; internal liquid is not reclassified as staging liquid and is not rejected by Master ban.
- Slave ban governs Slave Jar operations and prevents banned free liquid from entering/remaining in Slave staging; explicit Slave reject policy is combined with that receive boundary.
- Therefore a full Jar containing a Master-banned liquid may be transferred intact and reshaped on a Slave that permits that liquid. The same reshape remains illegal on the Master.

## Feasibility proof

A branch is feasible only after:
1. candidate assignment and preparation selection;
2. Master and Slave schedule enumeration;
3. distinct physical Jar binding;
4. concrete operation materialization;
5. ordered Master/Slave staging replay and one-way handoff projection;
6. immutable plan freezing;
7. Manifest validation against the same WorldSnapshot and replay witness.

The resource accounts are role-local. Slave-produced free liquid is usable only if it is accepted by Slave staging. Master-produced free liquid may contribute to the single handoff only after it has been accepted by Master staging.

Budget exhaustion and opaque rule input produce `Undetermined`; they are never collapsed into `Infeasible`.

## Remaining work

- Add differential tests comparing this concrete enumerator with `PartitionSearch` over targeted and generated small worlds.
- Prove parity for Master/Slave assignment, Jar transfer IDs, staging balances, capacity peaks, and reject/ban cases.
- Only then route B-class entry-point solving through this enumerator. Until then, the existing solver registry path remains unchanged.
