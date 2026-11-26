# 4.10b 9l — Concrete plan materialization seam

## Scope

This slice adds `WorldOrderSolvePlan.FromMaterializedPaths` as the single immutable boundary for a future symbolic-program materializer.

It accepts already materialized `SolverStep` paths and freezes them into the world-plan representation. It does **not** bind physical Jars, select rules, schedule operations, replay staging, calculate a handoff, or prove resource sufficiency.

A caller must not report an order as executable merely because this factory returned a plan. The final gate remains `WorldOrderResourceManifest.Create(result, snapshot)`.

## Invariants at this boundary

- Master and Slave paths are kept in the exact sequence supplied by the materializer; no requirement-index sorting occurs here.
- A-class plans cannot carry Slave steps, transferred Jar IDs, or transferred liquids.
- Transfer Jar IDs must be positive and unique.
- Transfer liquid names must be non-empty and amounts non-negative.
- The plan constructor checks one operation per physical Jar ID across the concatenated Master and Slave paths. Handoff does not reset the mark.
- `SolverStep` inputs are copied into immutable plan records; later mutation of the caller's lists, dictionaries, or step objects cannot mutate the plan.

## Why this seam is separate

The symbolic program owns operation identity and ordering. The physical binder owns symbolic-to-physical Jar identity. The materializer must join those two facts and create correct before/after snapshots. Resource replay must then prove consume-before-produce staging behavior and the single atomic handoff. Keeping plan freezing as a separate step prevents a partially built path from silently becoming an executable plan.

## Required next implementation

1. Materialize each symbolic rule node using the exact bound physical Jar ID and candidate operation.
2. Construct role-local Jar arrays and stable JarIndex values from those arrays; do not reuse a Master index in the Slave path.
3. Preserve the exact `SlaveOperationOrder` witness when emitting Slave steps.
4. Apply each physical Jar's order-level operation mark globally across both phases.
5. Replay role-local free-liquid staging in operation order. A produced liquid enters only the current role's staging account; rejected or over-capacity output is discarded.
6. Apply one atomic Master-to-Slave handoff. The Master ban checks only the free-liquid vector in Master staging; Jar-internal liquid remains part of the Jar state and is not subject to that ban.
7. Freeze paths through `FromMaterializedPaths`, create a result bound to the same snapshot, and require `WorldOrderResourceManifest.Create` to succeed.
8. Add entry-point tests through `WorldOrderSolverRegistry.Solve` for A and B orders before changing the production B-class route. Keep `PartitionSearch` as the differential oracle until parity is demonstrated.

## Tests in this slice

- Materialized Master/Slave stage order is retained.
- Reusing one physical Jar across Master and Slave operations is rejected.
- A-class transfer payload is rejected.
- Existing plan tests continue to cover deep-copy immutability and duplicate physical Jar operations.
