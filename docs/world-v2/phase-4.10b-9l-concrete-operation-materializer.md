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

The full-reshape `Consumed` witness records the internal liquid material, but it is not debited from the free-liquid staging balance. Both `WorldOrderResourceManifest` and `WorldPlanStepApplier` validate that same witness; `ExpectedFreeLiquidDelta` deliberately applies no staging debit for a full reshape. This accounting separation does not grant operation permission: the current workshop must still allow operations on Jars containing that liquid.

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

## Ordered resource replay

`WorldOrderOrderedResourceReplay` now provides the next proof layer:

- It replays Master rule nodes in symbolic sequence order.
- Each operation consumes its free-liquid input before attempting to place its output in Master staging.
- A Master-banned output is discarded; a staging-capacity overflow is discarded.
- Missing initial Master staging supply is increased only when a replay proves a prefix shortage or the final Slave boundary is not funded; the path is then replayed from its beginning.
- The minimum initial vector is checked against the current ship liquid inventory (when present) plus Master prefill, or the legacy Master liquid source plus prefill.
- For B orders it invokes `WorldOrderSlaveBoundaryProjector` with the Master ban and an explicit Slave rejected-liquid policy. The returned AND group retains JarState and FreeLiquid conditions separately, and its Slave operation-order witness remains attached.
- Jar-internal liquid never becomes a Master free-liquid boundary demand. Separately, the Master rule profile still rejects operations on a Jar whose internal liquid is Master-banned; a Slave profile may admit the same Jar operation after handoff if the Slave does not ban that liquid. Only a Slave operation's `ConsumedFreeLiquid` can create a free-liquid boundary demand.

The replay returns a resource witness, not a reservation. It still does not rewrite the plan or certify execution. The next integration step must make the concrete plan, this replay witness, and Manifest validation agree on the same snapshot and accepted-output decisions.

## Fail-closed plan assembly

`WorldOrderConcretePlanAssembler` is now the join point for one already enumerated symbolic branch:

1. Run ordered resource replay on the captured WorldSnapshot.
2. Materialize physical Jar operations using the replay's exact Master initial and Slave transfer liquid vectors.
3. Freeze the role-local paths into `WorldOrderSolvePlan`.
4. Build a provisional version-bound `WorldOrderSolveResult`.
5. Require `WorldOrderResourceManifest.Create` to succeed.
6. Compare Manifest's Master initial/final balances, transfer vector, both staging peaks, and transfer Jar IDs against the policy-aware replay.
7. Return Feasible only when the two independent proofs agree exactly.

A Manifest/replay disagreement returns Undetermined with the candidate plan retained for diagnosis; it is never silently downgraded into a successful result. This deliberately fails closed for rejected or overflowed outputs that the current generic Manifest simulation still treats as accepted. That limitation is explicit and becomes the next integration target.

## Domain contract: workshop-local operation ban and transfer boundary

A workshop is the actor that operates a Jar. If the Jar contains a liquid banned by the current workshop, that Jar cannot be operated there. This includes `POUR_OUT`, full-reshape self-consumption, and `MATCH`; the fact that the liquid is inside the Jar rather than in free-liquid staging does not grant an operation exemption.

This is distinct from resource migration. Master-to-Slave handoff and workshop-replacement resource transfer do not themselves operate the Jar and therefore do not apply the source workshop's Jar-operation gate. After handoff, the receiving workshop's own rule profile decides whether a later operation is allowed. Thus a Master-banned Jar may be handed to a non-banning Slave and full-reshaped there, while the same operation remains unavailable on the Master.

Free-liquid policy remains separate: banned free liquid cannot be consumed or stored in workshop staging, and Master-banned free liquid cannot be transferred across the B-class boundary. JarState handoff is not converted into a free-liquid transfer merely because the Jar contains that liquid.

`LiquidRules.CheckJarOperation` makes the Jar-operation check explicit; `ReshapeRules.SelfConsume`, `WorkshopRulebook`, the candidate catalog, and the live Workshop facade use the current workshop's policy. MATCH is versioned to reflect the explicit Jar-operation gate.
