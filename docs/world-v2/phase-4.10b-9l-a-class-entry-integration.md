# 9l — A-class concrete solver entry integration

## Scope

A-class Jar specifications now use the concrete 9l path at the actual solver registry entry point. B-class remains on `PartitionSearch` as a differential oracle; this slice does not claim B-class migration is complete.

## A-class pipeline

1. Resolve the immutable Master rule-candidate profile from the solve book's candidate cache (or build it from the supplied WorldSnapshot for direct registry calls).
2. Enumerate one candidate per requirement, then enumerate independent preparation Jar assignments for any free liquid needed by those candidates.
3. Build a symbolic program with one order-level operation per physical Jar identity.
4. Enumerate bounded permutations of every Master operation that consumes or produces staging free liquid. Jar-only operations use a stable tail order because they do not affect staging feasibility.
5. Bind each symbolic Jar key to a distinct unmarked physical Jar in the initial Master snapshot.
6. Replay Master staging in the selected order. Consumption occurs before production; banned outputs and capacity overflow are discarded; an unavailable input may only be supplied by the actual Master liquid source.
7. Materialize concrete `SolverStep` paths with physical Jar IDs, before/after Jar snapshots, and exact rule identity/version.
8. Freeze the immutable solve plan, create its resource Manifest, and compare Manifest balances/capacity/transfer evidence against the ordered replay witness.

Only step 8 can produce a Feasible solve result. A symbolic solution, a physical binding, or a completed materialization alone is not sufficient.

## Schedule contract

Requirement indices identify which demand a step satisfies; they do not define execution order. The program builder now accepts an explicit `masterOperationOrder`, validates that it contains every Master node exactly once, and preserves that order in node sequence. The A-class enumerator permutes all staging-affecting operations under a finite budget. If the budget is exhausted, the result is Undetermined.

## Workshop ban semantics

The candidate catalog evaluates full-Jar self-consuming reshape against the current workshop RuleContext. A Jar containing a liquid banned by the current workshop cannot be operated there, including full-Jar self-consuming reshape. The same Jar remains transferable as a physical resource; after transfer, the receiving workshop's rules govern any subsequent operation. Free-liquid staging storage and use are checked separately by the LiquidRules contract.

## Cache boundary

The solve book passes its immutable `WorldOrderRuleCandidateBook` through the solver registry to cache-aware solvers. Cache keys exclude ResourceVersion; concrete physical binding and ordered resource replay are always repeated for the current WorldSnapshot.

## Deliberate remaining work

- B-class still uses `PartitionSearch`; its replacement needs full assignment enumeration across Master and Slave, one atomic handoff, role-local ordered staging replay, physical Jar binding, and end-to-end parity.
- Runtime admission and execution continue to consume only immutable plans and their Manifest proofs; this PR does not change the execution session protocol.
