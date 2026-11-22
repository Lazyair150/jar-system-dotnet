# 4.10b 9l — Concrete operation materialization

## Scope

This slice joins three existing facts:

1. The symbolic program describes the exact rule candidate and ordered Jar versions.
2. The physical Jar binder maps each symbolic Jar key to one distinct initial Master Jar ID.
3. The captured WorldSnapshot and role-specific candidate profiles identify the state/rule universe under which the program was built.

`WorldOrderConcreteOperationMaterializer` emits concrete role-local `SolverStep` paths from those inputs.

It is deliberately not a solver-entry integration and does not itself certify resource feasibility.

## Concrete materialization invariants

- Every symbolic Jar key must have exactly one positive, unique physical Jar ID in the binding.
- Every bound Jar must exist in the initial Master snapshot and exactly match its symbolic root shape, internal liquid, and unmarked state.
- Every rule node must reference a candidate contained in the matching role's captured profile. A fabricated RuleId or candidate is rejected as Undetermined.
- The profile's rule-set and workshop-profile versions must match the current WorldSnapshot. Master and Slave profiles must also agree on the specification fingerprint.
- Every node is emitted in symbolic sequence order. The materializer does not sort by requirement index.
- Master snapshots contain the full Master Jar array. Slave snapshots contain only the handoff batch, with role-local JarIndex values and `MasterOrigin=true`.
- The handoff preserves the current physical Jar state and operation mark. It is not a rule operation and never clears the order-level mark.
- A global set of physical Jar IDs guards against a second rule operation across both roles.
- RuleId and RuleVersion are retained on `SolverOperation`, immutable `WorldPlanOperation`, and the execution-session wire DTO.

## Operation mapping

| Candidate operation | Concrete operation | Free-liquid effect |
|---|---|---|
| MATCH | MATCH | none |
| POUR_IN | POUR_IN | consumes target liquid |
| POUR_OUT / PREPARE_POUR_OUT | POUR_OUT | produces the Jar's internal liquid |
| RESHAPE_EMPTY | RESHAPE, FullReshape=false | consumes CostLiquid from staging |
| RESHAPE_FULL | RESHAPE, FullReshape=true | consumes the Jar's internal liquid only |

The full-reshape `Consumed` witness records the internal liquid material, but it is not debited from the free-liquid staging balance. The resource manifest validator now checks that distinction explicitly.

## Liquid snapshots

The materializer derives the minimum prefix supply for the Slave path, then derives the Master path's minimum prefix supply while preserving the Slave transfer requirement at the Master boundary. It fills each step's `AfterLiquids` with the resulting role-local ledger.

This is an accounting witness only. It does not simulate the workshop staging policy: reject/ban rules, capacity overflow, or whether a produced liquid is accepted into staging. Those remain the responsibility of ordered resource replay and the existing boundary projector. A Complete materialization must never be surfaced as an executable order without those checks and `WorldOrderResourceManifest.Create`.

## Explicit non-goals

- No change to the production A/B solver route.
- No replacement or deletion of `PartitionSearch`.
- No assumption that a jar-internal liquid is a free-liquid staging balance.
- No repeated Jar operations; a handoff does not reset the mark.
- No claim that the current `WorldOrderResourceManifest` alone implements reject/ban replay.

## Tests

- A-class direct match materializes the exact physical Jar and retains RuleId/RuleVersion.
- B-class slave Jar is sourced only from the single Master handoff and receives a role-local index.
- A Jar operated on the Master and then handed off remains marked and cannot be operated again.
- A candidate not present in the captured profile is rejected.
- Immutable plan copying retains exact rule identity.
