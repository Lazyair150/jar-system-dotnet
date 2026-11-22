# 4.10b 9k — Multi-Jar operation ordering

## Domain invariant

A physical Jar may be operated by at most one rule during one order implementation. Its successful operation sets the order-level mark. The mark survives state revision changes and the one-time Master-to-Slave handoff. A preparation Jar is therefore a distinct physical identity from every final-requirement Jar and from every other preparation Jar.

This phase does not reintroduce same-Jar transformation chains.

## Problem addressed

The previous symbolic program builder emitted Slave operations in a stable category/key order. That was deterministic, but it silently treated requirement index and Jar key as execution order. Requirement indices identify demands; they do not prescribe the order in which independent Jars are operated.

That can discard a valid branch. For example:

- Requirement 0 consumes one unit of free liquid X.
- Requirement 1 produces one unit of free liquid X.
- Both use distinct Jars.
- If requirement 1 runs first, its output enters Slave staging and requirement 0 can consume it.
- If requirement 0 runs first, the order needs X at the one-time Master-to-Slave boundary.

Both schedules are legitimate candidates; the first may be the only candidate when Master bans X.

## Implementation

- WorldOrderPreparatoryProgramBuilder.Build accepts an optional explicit Slave operation order expressed as node IDs.
- The single batch handoff contains every Jar that will be operated on by the Slave, including independent preparation Jars.
- The builder emits Slave rule nodes in the requested order and attaches the exact JarState edge from the handoff to each operation.
- WorldOrderSlaveRuleBranchEnumerator enumerates permutations of staging-affecting Slave operations (those that consume or produce free liquid). Operations with no staging effect use a stable tail order because they cannot affect staging feasibility.
- Every ordered program is replayed independently by WorldOrderSlaveBoundaryProjector; this preserves the actual sequence semantics: consume first, then produce, with rejection and capacity overflow discarding output.
- Equivalent projected AND groups are deduplicated. Different boundary resource requirements remain separate OR alternatives.
- Schedule expansion consumes the existing node/branch budgets. Exhaustion returns Undetermined, never a partial Complete set.

## Explicit limits

This is bounded permutation enumeration, not yet a dependency-pruned topological scheduler. Its worst-case cost is factorial in the number of staging-affecting operations; large spaces intentionally become Undetermined at the configured budget. The next optimization should derive mandatory producer-before-consumer constraints and enumerate only distinct legal schedules, while preserving all feasible orderings.

The enumerator still consumes the existing direct candidate profile and current one-step PREPARE_POUR_OUT catalog. It does not yet discover arbitrary multi-step Jar conversion paths, bind abstract Jar keys to live inventory IDs, or reserve live staging balances.

## Regression coverage

- A producer with a higher requirement index can run before a lower-index consumer.
- Producer-first and consumer-first schedules produce distinct OR boundary groups when both are legal.
- Master ban rejects the consumer-first boundary requirement but does not reject a producer-first local supply.
- Schedule budget exhaustion is Undetermined.
- Independent preparation Jars remain distinct and their permutations contribute to the same bounded search budget.
