# Phase 6 — Runtime workshop switch-event conservation

> Status: fixed-seed runtime tests authored on top of the current Core PR #60 target branch. The new tests have not yet been executed; no verification result is claimed.

## Baseline

- Current Core PR #60 target branch snapshot: `8ea8106367a9246a4e97aee3191f10114c1f2f99`.
- Test: `test/JarSystem.Tests/WorldV2/WorkshopReplacementTests.cs`.
- Fixed seed: `0x5A17C`.
- Generated runtime switch cases: 512.

## Runtime path exercised

Each sample constructs real Master/target/Slave Workshop instances, a `WorkshopRoster`, `ResourceWorldRuntime`, injected `WorkshopSwitchEvent` factory, `JarPool`, and `ResourceLedger`. It starts a Master replacement through `ResourceWorldRuntime.BeginReplacement` and advances logical time across both migration phases in one call.

Generated inputs vary:
- source Jar count from 0 through 4;
- destination Jar capacity from 0 through 4;
- Jar shape across the configured vocabulary;
- Jar internal state across empty, Cathode (`酒`), and Anode (`蜜`).

## Per-sample invariants

- The runtime commits the target as Master only after both switch-event phases complete.
- Every source Jar leaves the old Master exactly once.
- Every original Jar is either retained as the same live object on the target or recycled into the pool; no Jar is duplicated or lost.
- Retained Jar identity, shape, internal liquid, and Mark are unchanged.
- Jar-internal liquid remains inside the Jar; it is not silently converted into free-liquid staging during ownership migration.
- Discard callback count, object-pool return count, and discard-ledger amount agree.
- The switch event succeeds for empty, Cathode, and Anode Jars without treating physical ownership transfer as a Jar operation or applying target operation-polarity checks.
- Runtime does not fault after a successful switch.

## Relationship to existing Stage 6 samples

The existing 6,720 generated cases cover plan replay, staging admission, ship return and inter-workshop free-liquid transfer. Those do not replace a runtime-level test of the two-phase `WorkshopSwitchEvent` lifecycle under the owning `ResourceWorldRuntime`. These 512 cases are additional coverage, not a rewrite of the prior sample counts.

## Verification

- [x] 512 fixed-seed runtime switch-event cases authored.
- [ ] .NET test execution.
- [ ] Exact-head CI verification.
