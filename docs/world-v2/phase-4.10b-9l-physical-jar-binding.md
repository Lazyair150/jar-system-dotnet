# 4.10b 9l — Physical Jar identity binding

## Scope of this increment

This increment implements the first concrete step after the integration audit: a deterministic binder from symbolic Jar keys to physical Jar IDs in the initial Master snapshot.

It does **not** yet materialize a `WorldOrderSolvePlan`, replay liquid staging, or replace `PartitionSearch` in `JarOrderSpecificationSolver`. A successful identity binding is necessary, not sufficient, for an executable order.

## Contract

`WorldOrderPhysicalJarBinder.Bind(program, masterSnapshot)`:

- Reads the symbolic program's complete Jar version history.
- Finds the root (lowest revision) for each JarKey.
- Requires every root to be revision 0, owned by the Master role, and unmarked.
- Binds only against the initial Master snapshot. Slave inventory is never a source.
- Requires a stable positive physical Jar ID.
- Matches exact initial shape and internal-liquid state.
- Excludes already marked physical Jars.
- Allocates distinct physical IDs to distinct symbolic JarKeys.
- Preserves identity through handoff revisions: a handoff output is another version of the same JarKey, not another physical Jar.
- Returns a frozen JarKey → physical ID map.

For each exact shape/liquid state class, every symbolic key has the same candidate set. The binder allocates sorted keys to sorted physical IDs only after checking the whole class cardinality, avoiding a greedy cross-state allocation failure.

## Result states

- `Complete`: every symbolic JarKey has a unique physical identity.
- `Infeasible`: the snapshot does not contain enough unmarked Jars in the exact required initial states.
- `Undetermined`: the snapshot cannot provide reliable stable identities (missing or duplicate IDs), or the symbolic roots violate the required Master-origin/unmarked initial-state contract.

The binder intentionally does not inspect rule modifiers or liquid accounts. Rule legality belongs to candidate-profile generation; liquid balances, staging capacity, Master ban, Slave reject, and exact handoff quantities belong to ordered resource replay and manifest validation.

## Invariants protected

1. A physical Jar can satisfy at most one symbolic JarKey.
2. A marked Jar cannot be newly assigned for an order operation.
3. A Jar handed to Slave retains its original physical ID.
4. A Jar revision is not a fresh identity and does not reset the order-level mark.
5. No Slave-local initial Jar can satisfy a B-class program.
6. Jar-internal liquid is matched as Jar state; it is not converted into a free-liquid staging requirement.

## Tests

The binder tests cover exact state matching, distinct physical identities for distinct symbolic Jars, marked-Jar exclusion, Master-origin binding for a Slave operation across handoff, and empty programs.

## Remaining 9l work

The next slice must consume this mapping to materialize concrete ordered SolverSteps and before/after snapshots, including preparation Jars and schedule witnesses. It must then replay both workshop staging ledgers, produce exactly one atomic Master→Slave transfer batch, construct the immutable plan, and require `WorldOrderResourceManifest.Create` to succeed before reporting Feasible.

Only after actual `JarOrderSpecificationSolver.Solve` entry-point tests prove this end-to-end path should the B-class primary path switch. Keep `PartitionSearch` as the differential oracle until parity coverage is established.
