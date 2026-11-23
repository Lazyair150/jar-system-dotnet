# 9l — A-class concrete solver entry integration

## Scope

A-class Jar specifications use the concrete 9l path at the actual solver registry entry point. The stacked B-class integration now also routes production solving through `WorldOrderBClassBranchEnumerator`; `PartitionSearch` is retained only as an explicit differential oracle.

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

## Physical preparation multiplicity

Rule profiles are inventory-independent and contain one preparation candidate per source Jar state. At world-bound enumeration time, each candidate is expanded to the number of distinct unmarked physical Jars with that exact source state. This is required for orders that need multiple identical Jars to produce the same free liquid. The concrete binder still assigns distinct physical IDs and rejects any attempt to reuse one Jar. When only preparation multiplicity changes, cached requirement solutions remain reusable if the ordered requirement candidate lists are unchanged.

## Schedule contract

Requirement indices identify which demand a step satisfies; they do not define execution order. The program builder now accepts an explicit `masterOperationOrder`, validates that it contains every Master node exactly once, and preserves that order in node sequence. The A-class enumerator permutes all staging-affecting operations under a finite budget. If the budget is exhausted, the result is Undetermined.

## Workshop ban semantics

The candidate catalog evaluates full-Jar self-consuming reshape against the current workshop RuleContext. A Jar containing a liquid banned by the current workshop cannot be operated there, including full-Jar self-consuming reshape. This is intentional: the self-consumed internal liquid is still being used by that workshop's operation even though it never enters staging.

The ban is not a universal Jar-ownership filter:
- Master-to-Slave handoff and workshop-switch resource migration move the physical Jar as-is; they do not call the workshop's Jar-operation liquid check or stage its internal liquid.
- `AddJar` / `TransferIn` accept a physical Jar independently of its internal liquid. The receiving workshop's own rules are consulted only when it later operates that Jar.
- `ReplaceCapability` checks the free-liquid pool against the new ban and rejects a capability change only if that pool still contains newly banned liquid. Jar-internal liquid is not part of this validation.
- Free-liquid staging storage and use are checked separately by the LiquidRules contract.

Thus a full Jar containing a liquid banned by the Master can cross to a permissive Slave and be self-consumed there, but it cannot be self-consumed while still operated by the Master.

## Cache boundary

The solve book passes its immutable `WorldOrderRuleCandidateBook` through the solver registry to cache-aware solvers. Cache keys exclude ResourceVersion; concrete physical binding and ordered resource replay are always repeated for the current WorldSnapshot.

## Deliberate remaining work

- Run the expanded differential and execution-session matrix against the concrete B-class entry point. Keep `PartitionSearch` as a differential oracle until parity coverage is broad enough to retire it deliberately.
- Runtime admission and execution continue to consume only immutable plans and their Manifest proofs; this integration preserves the existing execution-session protocol.
