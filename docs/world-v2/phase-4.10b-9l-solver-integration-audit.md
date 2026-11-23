# Phase 4.10b 9l — Concrete solver integration and execution closure

## Status

The concrete A/B solver path is integrated into the production JarOrderSpecificationSolver entry point. This document supersedes the earlier audit that described PartitionSearch as the active B-class path.

The retained WorldOrderPartitionBClassOracle is differential-test-only. It is not authoritative for production feasibility and is not called by Solve().

## Production solve path

WorldOrderSolverRegistry.Solve
→ WorldOrderSolveBook.Evaluate
→ JarOrderSpecificationSolver.SolveCore

A class:

WorldOrderAClassBranchEnumerator
→ Master-owned symbolic requirement branches
→ optional independent Master preparation Jar assignments
→ Master resource-operation schedule enumeration
→ WorldOrderPhysicalJarBinder
→ WorldOrderConcreteOperationMaterializer
→ WorldOrderOrderedResourceReplay
→ WorldOrderConcretePlanAssembler
→ immutable WorldOrderSolvePlan + validated WorldOrderResourceManifest.

B class:

WorldOrderBClassBranchEnumerator
→ Slave-owned symbolic requirement branches
→ optional Master/Slave preparation Jar assignments
→ Master and Slave resource-operation schedule enumeration
→ one physical identity binding against initial Master inventory
→ exactly one Master→Slave handoff
→ role-local ordered resource replay
→ concrete materialization
→ immutable plan + replay-validated Manifest.

Both paths report Feasible only after the complete concrete assembly and Manifest validation succeed. Budget exhaustion or opaque rule semantics remain Undetermined.

## Resource-proof continuity

The ordered replay result is retained on the concrete WorldOrderSolveResult as ResourceReplay.

This proof contains:
- exact Master initial and final free-liquid balances;
- the one-time B-class transfer vector;
- Master and Slave staging peaks;
- per-step accepted production witnesses, including discarded output;
- the Slave boundary group and its operation-order witness.

The same proof is used at each later boundary:

1. The assembler creates the Manifest with CreateWithReplay.
2. Execution admission validates that same replay proof against the current immutable snapshot.
3. Resource-only solve-result rebinding revalidates the same proof against the new resource supply and physical Jar state, while retaining the new WorldVersion.
4. The execution session receives the resulting Manifest and immutable plan; it does not rerun a different solver or invent a new resource schedule.

This prevents generic path simulation from silently treating every POUR_OUT as accepted when ordered staging replay discarded it because of policy or capacity.

A rule/Profile/specification change still invalidates the old plan. A resource-only change may reuse it only when the same physical Jar inputs, replay proof, liquid supply, and staging capacities remain valid.

## Domain invariants enforced by the concrete path

- Every B-class final requirement is solved under the Slave rule profile only.
- The initial Slave must contain no Jar and no free liquid.
- Every physical Jar originates in the initial Master inventory.
- Each physical Jar identity is bound once and can be operated at most once per order, across both roles.
- Preparation Jars are distinct from requirement Jars and from other preparation Jars.
- Master preparation produces only free liquid needed at the Slave boundary; Master requirement solving is not merged into the B-class demand set.
- The B-class handoff is one-way and occurs exactly once.
- The handoff contains every Jar required by Slave operations and only the free-liquid vector proven by replay.
- Master and Slave staging accounts remain independent.
- Each operation consumes before producing; output is accepted only by its owning staging policy and capacity.
- Jar-internal liquid is not a staging balance. Full-Jar self-consumption is governed by the current workshop's Jar-operation rules.
- A Master-banned free liquid cannot cross as staging liquid, while a full Jar's internal liquid is not reclassified during physical handoff.
- A-class plans contain no Slave steps or transfer resources.
- Opaque Master rules make only branches with actual Master rule-operation nodes Undetermined; a Slave-only B branch is not blocked by unrelated Master rules.
- B-class Slave rules must be statically known because the Slave owns every final requirement operation.

## Admission, execution, settlement, and recovery

The production execution path is:

WorldOrderExecutionAdmission.TryAdmit
→ Manifest revalidation
→ physical Jar/liquid reservation
→ WorldOrderExecutionSession
→ ordered Master steps
→ B-class transfer preflight and single commit
→ ordered Slave steps
→ delivery validation/removal
→ resource settlement
→ reservation release
→ successful-cycle event.

The transfer service checks the complete Jar batch, liquid batch, role ownership, capacity, and lease stage before committing. It marks rollback ownership before the first mutation; failures after that point are cleaned up by the session rather than being reported as an untouched preflight rejection.

The session checkpoint stores its execution cursor and immutable plan/Manifest. World checkpoint restore validates the cursor against the same checkpoint's Master/Slave Jar snapshots and admission/reservation state. Restore does not re-admit, withdraw ship liquid again, repeat the handoff, replay completed steps, or clear order marks.

Success and failure settlement remain separate:
- success validates all delivery Jars before removing any, settles liquid resources, releases reservations, then publishes fulfillment and the cycle event;
- failure preserves completed work, returns only resources owned by a committed Slave handoff, settles remaining order-scoped liquid, releases reservations, and invalidates the solve result.

## Legacy oracle boundary

WorldOrderPartitionBClassOracle.Solve is retained only behind SolvePartitionOracleForDifferentialTesting. It may be compared on overlapping supported cases, but historical Master/Slave requirement partitioning is not a correctness oracle for the Slave-owned B-class contract.

Do not route production solving or execution admission back through the oracle.

## Required regression coverage

The 9l test families cover:
- A-class and B-class candidate enumeration and physical Jar multiplicity;
- identical preparation Jars and distinct requirement identities;
- one-operation-per-physical-Jar;
- Master-banned full Jars crossing as JarState but not being operated on by Master;
- Master/Slave ban and explicit reject behavior;
- staging capacity, rejected output, and consume-before-produce order;
- one-way handoff and empty initial Slave;
- concrete materialization, ordered replay, and Manifest agreement;
- execution admission, Jar/liquid reservation, and role exclusivity;
- Master execution, transfer preflight/commit, Slave execution, delivery, failure, interruption, rollback, settlement, and cooldown;
- checkpoint restore at Master, transfer, and Slave boundaries without repeating funding or transfer.

## Completion gate

4.10b is complete only when the branch's full verification workflow is green and the production-entry, admission, execution, settlement, and checkpoint regressions pass together. A successful build alone or an enumerator-only test is not sufficient.
