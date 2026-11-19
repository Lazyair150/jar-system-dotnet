# World v2 — Phase 4.1: immutable solve snapshot

## Scope

This phase introduces the read-only input boundary used by future order solving:

- `WorldSnapshotFactory.Capture(WorkshopRoster)` captures the current master/slave roles once.
- `WorkshopSolveSnapshot` stores solver-relevant workshop data as pure values.
- `WorldSnapshot.Version` is a deterministic SHA-256 fingerprint of those solve inputs.
- Each solver call obtains fresh mutable `JarState`, `WorkshopInfo`, and liquid dictionary inputs from the immutable snapshot.

This phase does **not** implement order solving, dirty scheduling, execution reservation, or execution-time validation.

## Why this is not WorkshopSnapshot

`WorkshopSnapshot` in `Domain/Workshop.cs` is a rollback memo. It intentionally retains original `Jar` references so a failed workshop migration can restore entity identity.

A solve snapshot has the opposite requirement: it must not expose live entities or mutable solver state. It therefore projects each jar into the immutable `WorldJarState` value and copies the liquid dictionary.

## Snapshot contents

For each occupied role (master and slave):

- workshop name and role position (master/slave ordering is part of the fingerprint);
- jar and liquid capacities;
- immutable `WorkshopCapability`;
- jars in current list order: ID, shape, liquid, and operation-mark state;
- free-liquid counts, preserving the exact dictionary iteration order supplied to the solver;
- currently effective static-solver modifiers.

The resource-area warehouse is intentionally excluded. Resources become candidates only after they flow into the master workshop.

The slave snapshot is retained because its role identity and empty-workshop invariant are relevant to B-type solving. The later specification adapter must reject or report an invalid slave state; this phase does not silently merge slave resources into the master pool.

## Modifier handling

`ModifierStack.CaptureEffectiveModifiers()` excludes modifiers whose `Window.From` has not yet arrived. The snapshot captures:

- `SuppressModifier` values;
- `GrantModifier` values;
- opaque descriptors for `RequireModifier` and unknown custom modifier implementations.

`RequireModifier.Violation` is a delegate and may close over mutable state. It is deliberately **not copied or invoked** by snapshot capture. A later static solver adapter must treat `HasOpaqueSolverRules` as *undetermined*, never ignore it and claim feasibility.

`RetimeModifier` and `IntensityModifier` are excluded from the static solve fingerprint: they affect process timing or production rates, not the current static feasibility input. Any produced resource still changes the snapshot once it reaches the master.

## Version semantics

Version is the content fingerprint, not a tick number or command sequence.

- Identical solver inputs produce the same version, even across separate object instances.
- Any captured solver-input difference produces a different version (subject to the cryptographic hash's negligible collision probability).
- If the world changes and later returns to exactly the same solver inputs, the version returns to the original value. This is intentional: a previously computed plan against exactly the same inputs can be considered again, subject to the later execution-time validation and reservation rules.
- Changes to production rates, world clock, order CD, or warehouse inventory alone do not change this version unless they alter the captured solver inputs.

The fingerprint includes jar IDs because plans and later resource reservation must identify the actual resource entities, not merely equivalent shapes.

## Tests

`WorldSnapshotTests` covers deterministic versions, resource/capability changes, role assignment, isolation from live entities and mutable solver arguments, future modifier activation, and opaque `RequireModifier` handling.

Local build and test execution are still required before this phase can be considered verified.
